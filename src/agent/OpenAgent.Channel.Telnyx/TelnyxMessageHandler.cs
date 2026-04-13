using System.Text;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Orchestrates a Telnyx TeXML phone call. Returns TeXML response strings for
/// each webhook turn; persists user/assistant messages to the conversation store.
/// </summary>
public sealed class TelnyxMessageHandler
{
    private readonly TelnyxOptions _options;
    private readonly string _connectionId;
    private readonly IConversationStore _store;
    private readonly Func<string, ILlmTextProvider> _textProviderResolver;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<TelnyxMessageHandler> _logger;

    private const string ChannelType = "telnyx";
    private const string Source = "telnyx";

    /// <summary>Creates the handler. The factory is the only intended caller in production; tests may instantiate directly.</summary>
    public TelnyxMessageHandler(
        TelnyxOptions options,
        string connectionId,
        IConversationStore store,
        Func<string, ILlmTextProvider> textProviderResolver,
        AgentConfig agentConfig,
        ILogger<TelnyxMessageHandler> logger)
    {
        _options = options;
        _connectionId = connectionId;
        _store = store;
        _textProviderResolver = textProviderResolver;
        _agentConfig = agentConfig;
        _logger = logger;
    }

    /// <summary>
    /// Handles the initial inbound call webhook. Enforces the caller allowlist, creates or
    /// retrieves the conversation, and returns a TeXML greeting with a speech gather.
    /// </summary>
    public Task<string> HandleVoiceAsync(
        string callSid,
        string from,
        string to,
        CancellationToken ct)
    {
        _logger.LogInformation("Telnyx [{ConnectionId}] inbound call {CallSid} from {From} to {To}",
            _connectionId, callSid, from, to);

        // Reject caller if allowlist is non-empty and they are not on it
        if (!IsCallerAllowed(from))
        {
            _logger.LogWarning("Telnyx [{ConnectionId}] rejecting caller {From} — not on allowlist", _connectionId, from);
            return Task.FromResult(TeXmlBuilder.Reject("Not authorised."));
        }

        // Find or create the conversation bound to this caller's E.164 number
        var conversation = _store.FindOrCreateChannelConversation(
            channelType: ChannelType,
            connectionId: _connectionId,
            channelChatId: from,
            source: Source,
            type: ConversationType.Phone,
            provider: _agentConfig.TextProvider,
            model: _agentConfig.TextModel);

        // Store the caller's E.164 number as DisplayName so the Conversations UI is usable
        if (!string.Equals(conversation.DisplayName, from, StringComparison.Ordinal))
            _store.UpdateDisplayName(conversation.Id, from);

        var actionUrl = BuildActionUrl("speech");
        return Task.FromResult(TeXmlBuilder.GreetAndGather(
            greeting: "Hi, it's OpenAgent. How can I help you today?",
            gatherActionUrl: actionUrl));
    }

    /// <summary>
    /// Handles a speech-result webhook from a Gather verb. Adds the user's transcribed
    /// speech to the conversation, calls the text provider, and returns a TeXML reply
    /// with the next gather. An empty SpeechResult reprompts without calling the provider.
    /// </summary>
    public async Task<string> HandleSpeechAsync(
        string callSid,
        string from,
        string speechResult,
        CancellationToken ct)
    {
        var conversation = _store.FindChannelConversation(ChannelType, _connectionId, from);
        if (conversation is null)
        {
            _logger.LogWarning("Telnyx [{ConnectionId}] speech webhook for unknown conversation {From}", _connectionId, from);
            return TeXmlBuilder.Farewell("Sorry, something went wrong. Goodbye.");
        }

        // Empty speech — Telnyx did not detect any input; reprompt without touching the provider
        if (string.IsNullOrWhiteSpace(speechResult))
        {
            _logger.LogInformation("Telnyx [{ConnectionId}] empty speech result — reprompting", _connectionId);
            return TeXmlBuilder.RespondAndGather(
                reply: "I didn't catch that — could you say it again?",
                gatherActionUrl: BuildActionUrl("speech"));
        }

        // Build user message and call the provider
        var userMessage = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversation.Id,
            Role = "user",
            Content = speechResult
        };

        var reply = new StringBuilder();
        try
        {
            var provider = _textProviderResolver(conversation.Provider);
            await foreach (var evt in provider.CompleteAsync(conversation, userMessage, ct))
            {
                if (evt is TextDelta delta)
                    reply.Append(delta.Content);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telnyx [{ConnectionId}] text provider threw during call", _connectionId);
            return TeXmlBuilder.Farewell("Sorry, I'm having trouble connecting. Please try again later. Goodbye.");
        }

        var replyText = reply.ToString().Trim();
        if (replyText.Length == 0)
        {
            _logger.LogWarning("Telnyx [{ConnectionId}] provider returned empty reply", _connectionId);
            return TeXmlBuilder.Farewell("Sorry, I'm having trouble. Goodbye.");
        }

        return TeXmlBuilder.RespondAndGather(replyText, BuildActionUrl("speech"));
    }

    /// <summary>
    /// Handles a call-ended webhook. Logs the hangup; no TeXML response is needed.
    /// </summary>
    public Task HandleHangupAsync(string callSid, string from, CancellationToken ct)
    {
        _logger.LogInformation("Telnyx [{ConnectionId}] call {CallSid} ended (from {From})", _connectionId, callSid, from);
        return Task.CompletedTask;
    }

    // Returns true when the caller is permitted: empty allowlist = allow all, otherwise exact E.164 match.
    private bool IsCallerAllowed(string from)
    {
        if (_options.AllowedNumbers.Count == 0) return true;
        return _options.AllowedNumbers.Contains(from);
    }

    // Builds the Telnyx callback action URL for the given suffix (e.g. "speech").
    private string BuildActionUrl(string suffix)
    {
        var baseUrl = (_options.BaseUrl ?? "").TrimEnd('/');
        var webhookId = _options.WebhookId ?? "_";
        return $"{baseUrl}/api/webhook/telnyx/{webhookId}/{suffix}";
    }
}
