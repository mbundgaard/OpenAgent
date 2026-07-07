using OpenAgent.Channel.Telegram;
using Telegram.Bot.Types;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Records all calls to the sender for assertion in tests.
/// </summary>
public sealed class FakeTelegramSender : ITelegramSender
{
    public List<(long ChatId, string Action)> TypingCalls { get; } = [];
    public List<(long ChatId, string Html)> HtmlCalls { get; } = [];
    public List<(long ChatId, string Text)> TextCalls { get; } = [];
    public List<(long ChatId, long DraftId, string Text)> DraftCalls { get; } = [];
    public List<(long ChatId, string Markdown)> RichMarkdownCalls { get; } = [];
    public List<(long ChatId, long DraftId, string Markdown)> RichMarkdownDraftCalls { get; } = [];

    /// <summary>When true, SendHtmlAsync throws to trigger plain-text fallback.</summary>
    public bool FailHtml { get; set; }

    /// <summary>When true, all send methods throw.</summary>
    public bool FailAll { get; set; }

    /// <summary>When true, SendDraftAsync throws.</summary>
    public bool FailDraft { get; set; }

    /// <summary>When true, the rich markdown send methods throw to trigger fallback.</summary>
    public bool FailRichMarkdown { get; set; }

    public Task SendTypingAsync(ChatId chatId, CancellationToken ct)
    {
        if (FailAll) throw new Exception("Send failed");
        TypingCalls.Add((chatId.Identifier!.Value, "typing"));
        return Task.CompletedTask;
    }

    /// <summary>Counter for generating fake Telegram message IDs.</summary>
    private int _nextMessageId = 1000;

    public Task<int> SendHtmlAsync(ChatId chatId, string html, CancellationToken ct)
    {
        if (FailAll || FailHtml) throw new Exception("HTML send failed");
        HtmlCalls.Add((chatId.Identifier!.Value, html));
        return Task.FromResult(_nextMessageId++);
    }

    public Task<int> SendTextAsync(ChatId chatId, string text, CancellationToken ct)
    {
        if (FailAll) throw new Exception("Text send failed");
        TextCalls.Add((chatId.Identifier!.Value, text));
        return Task.FromResult(_nextMessageId++);
    }

    public Task<DraftResult> SendDraftAsync(ChatId chatId, long draftId, string text, string? parseMode, CancellationToken ct)
    {
        if (FailAll || FailDraft)
            return Task.FromResult(new DraftResult { Ok = false, StatusCode = 429, RetryAfterSeconds = 1, Description = "Too Many Requests" });
        DraftCalls.Add((chatId.Identifier!.Value, draftId, text));
        return Task.FromResult(DraftResult.Success());
    }

    public Task<int> SendRichMarkdownAsync(ChatId chatId, string markdown, CancellationToken ct)
    {
        if (FailAll || FailRichMarkdown) throw new Exception("Rich markdown send failed");
        RichMarkdownCalls.Add((chatId.Identifier!.Value, markdown));
        return Task.FromResult(_nextMessageId++);
    }

    public Task<DraftResult> SendRichMarkdownDraftAsync(ChatId chatId, long draftId, string markdown, CancellationToken ct)
    {
        if (FailRichMarkdown) throw new Exception("Rich markdown draft send failed");
        if (FailAll || FailDraft)
            return Task.FromResult(new DraftResult { Ok = false, StatusCode = 429, RetryAfterSeconds = 1, Description = "Too Many Requests" });
        RichMarkdownDraftCalls.Add((chatId.Identifier!.Value, draftId, markdown));
        return Task.FromResult(DraftResult.Success());
    }
}
