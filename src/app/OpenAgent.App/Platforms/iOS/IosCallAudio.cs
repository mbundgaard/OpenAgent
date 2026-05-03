using AVFoundation;
using AudioToolbox;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.App.Core.Voice;

namespace OpenAgent.App;

/// <summary>
/// iOS implementation of <see cref="ICallAudio"/>. Wraps AVAudioEngine for microphone capture and
/// AVAudioPlayerNode for playback. Configures the shared AVAudioSession for PlayAndRecord with
/// VoiceChat mode so iOS applies echo cancellation/AGC and routes through the bluetooth/speaker.
///
/// Capture path: VoiceChat's Voice Processing IO unit emits Float32 mono. We request 24 kHz via
/// <see cref="AVAudioSession.SetPreferredSampleRate"/> and verify after activation. When the
/// session honors it (the common case), the tap delivers Float32 mono at the right rate and we
/// just multiply/clamp/cast to Int16 in the tap callback — same trick the web's AudioWorklet uses.
/// AVAudioConverter is only instantiated as a fallback when iOS hands us something off-rate.
///
/// Buffer access: <c>Int16ChannelData</c> / <c>Float32ChannelData</c> are <c>const T * const *</c>
/// (pointer to channel-pointer array), not flat data pointers. Reading them as <c>T*</c> writes
/// into the channel pointer table, not the samples — silently producing zeros for playback and
/// garbage for capture. We go through <c>AudioBufferList[0].Data</c>, which is a flat
/// <c>void*</c> regardless of channel layout.
/// </summary>
public sealed class IosCallAudio : ICallAudio
{
    private readonly ILogger<IosCallAudio> _logger;
    private AVAudioEngine? _engine;
    private AVAudioPlayerNode? _player;
    private AVAudioFormat? _outFormat;
    private AVAudioFormat? _inputNativeFormat;
    private AVAudioConverter? _converter;
    private bool _useFastFloat32Path;
    private AVAudioPlayer? _thinkingPlayer;
    private NSData? _thinkingData;
    private bool _muted;
    private readonly object _lifecycleLock = new();

    public event Action<byte[]>? OnPcmCaptured;

    public IosCallAudio(ILogger<IosCallAudio>? logger = null)
    {
        _logger = logger ?? NullLogger<IosCallAudio>.Instance;
    }

    public async Task StartAsync(int sampleRate, CancellationToken ct)
    {
        _logger.LogInformation("Audio start requested sampleRate={SampleRate}", sampleRate);

        var session = AVAudioSession.SharedInstance();
        // Drop DefaultToSpeaker — let iOS route to the earpiece, where the Voice Processing IO
        // unit's AEC works best. A speaker-toggle UI can override per-call later.
        session.SetCategory(AVAudioSessionCategory.PlayAndRecord,
            AVAudioSessionCategoryOptions.AllowBluetooth);
        session.SetMode(AVAudioSessionMode.VoiceChat, out var modeErr);
        if (modeErr is not null)
            _logger.LogWarning("AVAudioSession SetMode(VoiceChat) failed: {Error}", modeErr.LocalizedDescription);
        session.SetPreferredSampleRate(sampleRate, out var rateErr);
        if (rateErr is not null)
            _logger.LogWarning("SetPreferredSampleRate({Rate}) failed: {Error}", sampleRate, rateErr.LocalizedDescription);
        session.SetActive(true, out var activateErr);
        if (activateErr is not null)
            _logger.LogWarning("AVAudioSession SetActive(true) failed: {Error}", activateErr.LocalizedDescription);

        // What we actually got — VoiceChat normally honors 24 kHz, but log if not so we know
        // when we're falling back to AVAudioConverter.
        var actualSessionRate = session.SampleRate;
        _logger.LogInformation("Audio session active requestedRate={Requested}Hz actualRate={Actual}Hz mode={Mode} category={Category}",
            sampleRate, actualSessionRate, session.Mode, session.Category);

        lock (_lifecycleLock)
        {
            _outFormat = new AVAudioFormat(AVAudioCommonFormat.PCMInt16, sampleRate, 1, interleaved: true);

            _engine = new AVAudioEngine();
            _player = new AVAudioPlayerNode();
            _engine.AttachNode(_player);
            _engine.Connect(_player, _engine.MainMixerNode, _outFormat);

            var input = _engine.InputNode;

            // Engage the Voice Processing IO unit explicitly. AVAudioSession.SetMode(VoiceChat)
            // alone is a session-level hint; the engine's nodes still default to the regular
            // RemoteIO unit which has no echo cancellation. Without VPIO the speaker output
            // bleeds into the mic, OpenAI's server-side VAD treats it as user speech, and every
            // assistant response is barge-in-cancelled within a few hundred ms. Both nodes need
            // it: input for capture+AEC, output to provide the AEC reference signal.
            var inOk = input.SetVoiceProcessingEnabled(true, out var inVpErr);
            if (!inOk || inVpErr is not null)
                _logger.LogWarning("InputNode.SetVoiceProcessingEnabled returned ok={Ok} err={Error}",
                    inOk, inVpErr?.LocalizedDescription ?? "(none)");
            var outOk = _engine.OutputNode.SetVoiceProcessingEnabled(true, out var outVpErr);
            if (!outOk || outVpErr is not null)
                _logger.LogWarning("OutputNode.SetVoiceProcessingEnabled returned ok={Ok} err={Error}",
                    outOk, outVpErr?.LocalizedDescription ?? "(none)");
            _logger.LogInformation("Voice processing requested input={In} output={Out}", inOk, outOk);

            // Read native format AFTER enabling voice processing — VPIO changes the bus format.
            _inputNativeFormat = input.GetBusOutputFormat(0);

            // Fast path: VoiceChat hands us Float32 mono at the negotiated rate. Skip
            // AVAudioConverter entirely and inline the Float32→Int16 conversion (same as the
            // web's AudioWorklet). Falls back to AVAudioConverter only when the rate is wrong
            // or the channel count is unexpected.
            var rateMatches = Math.Abs(_inputNativeFormat.SampleRate - sampleRate) < 1.0;
            var monoFloat32 = _inputNativeFormat.ChannelCount == 1
                              && _inputNativeFormat.CommonFormat == AVAudioCommonFormat.PCMFloat32;
            _useFastFloat32Path = rateMatches && monoFloat32;

            if (!_useFastFloat32Path)
                _converter = new AVAudioConverter(_inputNativeFormat, _outFormat);

            _logger.LogInformation(
                "Audio input native={InRate}Hz/{InCh}ch/{InFmt} target={OutRate}Hz fastPath={Fast}",
                _inputNativeFormat.SampleRate, _inputNativeFormat.ChannelCount,
                _inputNativeFormat.CommonFormat, sampleRate, _useFastFloat32Path);

            input.InstallTapOnBus(0, 1024, _inputNativeFormat, (buffer, _) =>
            {
                if (_muted) return;
                var bytes = _useFastFloat32Path
                    ? Float32ToPcm16(buffer)
                    : ConvertToPcm16Mono(buffer);
                if (bytes is { Length: > 0 }) OnPcmCaptured?.Invoke(bytes);
            });

            _engine.Prepare();
            _engine.StartAndReturnError(out var err);
            if (err is not null)
            {
                _logger.LogError("Audio engine start failed: {Error}", err.LocalizedDescription);
                throw new InvalidOperationException(err.LocalizedDescription);
            }
            _player.Play();
            _logger.LogInformation("Audio engine running");
        }
        await Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _logger.LogInformation("Audio stop");
        StopThinkingLoop();
        lock (_lifecycleLock)
        {
            _player?.Stop();
            try { _engine?.InputNode.RemoveTapOnBus(0); } catch { }
            _engine?.Stop();
            _converter?.Dispose();
            _player = null;
            _engine = null;
            _converter = null;
            _outFormat = null;
            _inputNativeFormat = null;
            _useFastFloat32Path = false;
        }
        try { AVAudioSession.SharedInstance().SetActive(false, out _); } catch { }
        return Task.CompletedTask;
    }

    public void EnqueuePlayback(byte[] pcm16)
    {
        var player = _player;
        var format = _outFormat;
        if (player is null || format is null) return;

        var frameCount = (uint)(pcm16.Length / 2);
        if (frameCount == 0) return;

        var buffer = new AVAudioPcmBuffer(format, frameCount) { FrameLength = frameCount };

        // AudioBufferList[0].Data is a flat void* into the actual sample storage —
        // works for both interleaved and non-interleaved layouts. The Int16ChannelData
        // property is a pointer-to-pointer-array; casting it to short* is wrong.
        unsafe
        {
            var ab = buffer.AudioBufferList[0];
            var dst = (short*)ab.Data;
            for (var i = 0; i < frameCount; i++)
                dst[i] = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
        }

        player.ScheduleBuffer(buffer, () => { });
    }

    public void FlushPlayback()
    {
        _player?.Stop();
        _player?.Play();
    }

    public void SetMuted(bool muted) => _muted = muted;

    public void PlayThinkingLoop()
    {
        StopThinkingLoop();
        if (_thinkingData is null)
        {
            try
            {
                using var stream = FileSystem.OpenAppPackageFileAsync("thinking.m4a").GetAwaiter().GetResult();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                _thinkingData = NSData.FromArray(ms.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to load thinking.m4a: {Error}", ex.Message);
                return;
            }
        }
        _thinkingPlayer = AVAudioPlayer.FromData(_thinkingData);
        if (_thinkingPlayer is null) { _logger.LogWarning("Failed to create AVAudioPlayer for thinking sound"); return; }
        _thinkingPlayer.NumberOfLoops = -1;
        _thinkingPlayer.Volume = 0.6f;
        _thinkingPlayer.PrepareToPlay();
        _thinkingPlayer.Play();
        _logger.LogInformation("Thinking sound started");
    }

    public void StopThinkingLoop()
    {
        if (_thinkingPlayer is not null) _logger.LogInformation("Thinking sound stopped");
        _thinkingPlayer?.Stop();
        _thinkingPlayer?.Dispose();
        _thinkingPlayer = null;
    }

    public void SetSpeakerOutput(bool useSpeaker)
    {
        // OverrideOutputAudioPort flips the playback route on the active session WITHOUT
        // reconfiguring the engine — the Voice Processing IO unit stays engaged so AEC keeps
        // working in either route. Speaker mode means the loud bottom speaker; None returns
        // to the default route (earpiece in PlayAndRecord+VoiceChat).
        var port = useSpeaker
            ? AVAudioSessionPortOverride.Speaker
            : AVAudioSessionPortOverride.None;
        try
        {
            AVAudioSession.SharedInstance().OverrideOutputAudioPort(port, out var err);
            if (err is not null)
                _logger.LogWarning("OverrideOutputAudioPort({Port}) failed: {Error}", port, err.LocalizedDescription);
            else
                _logger.LogInformation("Audio output route override -> {Port}", port);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OverrideOutputAudioPort threw");
        }
    }

    /// <summary>
    /// Fast path: input is already Float32 mono at the target rate. Multiply/clamp/cast inline,
    /// matching the web AudioWorklet's PCM capture. No AVAudioConverter, no resampling.
    /// </summary>
    private static byte[] Float32ToPcm16(AVAudioPcmBuffer src)
    {
        var frames = (int)src.FrameLength;
        if (frames <= 0) return Array.Empty<byte>();

        var bytes = new byte[frames * 2];
        unsafe
        {
            var ab = src.AudioBufferList[0];
            var srcPtr = (float*)ab.Data;
            for (var i = 0; i < frames; i++)
            {
                var f = srcPtr[i];
                if (f >  1f) f =  1f;
                if (f < -1f) f = -1f;
                var s = (short)(f < 0 ? f * 0x8000 : f * 0x7FFF);
                bytes[i * 2]     = (byte)(s & 0xFF);
                bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
        }
        return bytes;
    }

    /// <summary>
    /// Fallback: input format/rate doesn't match. Run through AVAudioConverter, then read the
    /// resulting PCM16 from the dst buffer's flat data pointer.
    /// </summary>
    private byte[] ConvertToPcm16Mono(AVAudioPcmBuffer src)
    {
        var converter = _converter;
        var outFormat = _outFormat;
        if (converter is null || outFormat is null) return Array.Empty<byte>();

        var ratio = outFormat.SampleRate / src.Format.SampleRate;
        var frameCapacity = (uint)Math.Ceiling(src.FrameLength * ratio) + 64;
        var dst = new AVAudioPcmBuffer(outFormat, frameCapacity);

        var consumed = false;
        var outStatus = converter.ConvertToBuffer(dst, out var error,
            (uint _, out AVAudioConverterInputStatus s) =>
            {
                if (consumed) { s = AVAudioConverterInputStatus.NoDataNow; return null!; }
                consumed = true;
                s = AVAudioConverterInputStatus.HaveData;
                return src;
            });

        if (error is not null) return Array.Empty<byte>();
        if (outStatus is AVAudioConverterOutputStatus.Error) return Array.Empty<byte>();

        var frames = (int)dst.FrameLength;
        if (frames <= 0) return Array.Empty<byte>();
        var bytes = new byte[frames * 2];

        unsafe
        {
            var ab = dst.AudioBufferList[0];
            var srcPtr = (short*)ab.Data;
            for (var i = 0; i < frames; i++)
            {
                var s = srcPtr[i];
                bytes[i * 2]     = (byte)(s & 0xFF);
                bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
        }
        return bytes;
    }

    public ValueTask DisposeAsync()
    {
        StopAsync().GetAwaiter().GetResult();
        return ValueTask.CompletedTask;
    }
}
