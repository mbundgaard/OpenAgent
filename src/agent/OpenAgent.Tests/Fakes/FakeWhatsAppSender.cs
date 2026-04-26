using OpenAgent.Channel.WhatsApp;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Test fake that records all WhatsApp sender calls for assertion.
/// </summary>
public class FakeWhatsAppSender : IWhatsAppSender
{
    private int _nextSendId;

    public List<string> ComposingCalls { get; } = [];
    public List<(string ChatId, string Text, string StanzaId)> TextCalls { get; } = [];

    public Task SendComposingAsync(string chatId)
    {
        ComposingCalls.Add(chatId);
        return Task.CompletedTask;
    }

    public Task<string?> SendTextAsync(string chatId, string text)
    {
        var stanzaId = $"FAKE-{++_nextSendId}";
        TextCalls.Add((chatId, text, stanzaId));
        return Task.FromResult<string?>(stanzaId);
    }
}
