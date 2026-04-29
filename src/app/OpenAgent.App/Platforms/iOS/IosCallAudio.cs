using AVFoundation;
using AudioToolbox;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.App.Core.Voice;

namespace OpenAgent.App;

/// <summary>
/// iOS implementation of <see cref="ICallAudio"/>. Wraps AVAudioEngine for microphone capture
/// (with AVAudioConverter resampling to the negotiated sample rate) and AVAudioPlayerNode for
/// PCM16 playback. Configures the shared AVAudioSession for PlayAndRecord with VoiceChat mode
/// so iOS applies echo cancellation and routes audio through the Bluetooth/speaker as available.
/// </summary>
public sealed class IosCallAudio : ICallAudio
{
    private readonly ILogger<IosCallAudio> _logger;
    private AVAudioEngine? _engine;
    private AVAudioPlayerNode? _player;
    private AVAudioFormat? _outFormat;
    private AVAudioFormat? _inputNativeFormat;
    private AVAudioConverter? _converter;
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
        session.SetCategory(AVAudioSessionCategory.PlayAndRecord,
            AVAudioSessionCategoryOptions.AllowBluetooth | AVAudioSessionCategoryOptions.DefaultToSpeaker);
        session.SetMode(AVAudioSessionMode.VoiceChat, out _);
        session.SetPreferredSampleRate(sampleRate, out _);
        session.SetActive(true, out _);

        lock (_lifecycleLock)
        {
            _outFormat = new AVAudioFormat(AVAudioCommonFormat.PCMInt16, sampleRate, 1, interleaved: true);

            _engine = new AVAudioEngine();
            _player = new AVAudioPlayerNode();
            _engine.AttachNode(_player);
            _engine.Connect(_player, _engine.MainMixerNode, _outFormat);

            var input = _engine.InputNode;
            _inputNativeFormat = input.GetBusOutputFormat(0);
            var needsConversion = Math.Abs(_inputNativeFormat.SampleRate - sampleRate) > 1
                                  || _inputNativeFormat.ChannelCount != 1
                                  || _inputNativeFormat.CommonFormat != AVAudioCommonFormat.PCMInt16;

            if (needsConversion)
                _converter = new AVAudioConverter(_inputNativeFormat, _outFormat);

            _logger.LogInformation("Audio input={InRate}Hz/{InCh}ch out={OutRate}Hz/1ch resampling={Resampling}",
                _inputNativeFormat.SampleRate, _inputNativeFormat.ChannelCount, sampleRate, needsConversion);

            input.InstallTapOnBus(0, 4096, _inputNativeFormat, (buffer, _) =>
            {
                if (_muted) return;
                var bytes = _converter is not null ? ConvertToInt16Mono(buffer) : CopyInt16Mono(buffer);
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

        unsafe
        {
            var dst = (short*)buffer.Int16ChannelData;
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

    private static byte[] CopyInt16Mono(AVAudioPcmBuffer src)
    {
        var byteCount = (int)(src.FrameLength * 2);
        if (byteCount <= 0) return Array.Empty<byte>();
        var bytes = new byte[byteCount];
        unsafe
        {
            var srcPtr = (short*)src.Int16ChannelData;
            for (var i = 0; i < src.FrameLength; i++)
            {
                var s = srcPtr[i];
                bytes[i * 2]     = (byte)(s & 0xFF);
                bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
        }
        return bytes;
    }

    private byte[] ConvertToInt16Mono(AVAudioPcmBuffer src)
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

        var byteCount = (int)(dst.FrameLength * 2);
        if (byteCount <= 0) return Array.Empty<byte>();
        var bytes = new byte[byteCount];

        unsafe
        {
            var srcPtr = (short*)dst.Int16ChannelData;
            for (var i = 0; i < dst.FrameLength; i++)
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
