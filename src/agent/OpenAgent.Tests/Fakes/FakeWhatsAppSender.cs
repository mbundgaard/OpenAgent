using OpenAgent.Channel.WhatsApp;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Test fake that records all WhatsApp sender calls for assertion.
/// </summary>
public class FakeWhatsAppSender : IWhatsAppSender
{
    public List<string> ComposingCalls { get; } = [];
    public List<(string ChatId, string Text)> TextCalls { get; } = [];

    public Task SendComposingAsync(string chatId)
    {
        ComposingCalls.Add(chatId);
        return Task.CompletedTask;
    }

    public Task SendTextAsync(string chatId, string text)
    {
        TextCalls.Add((chatId, text));
        return Task.CompletedTask;
    }
}
