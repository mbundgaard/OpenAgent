using System.Net;
using System.Net.Http;
using OpenAgent.Channel.Telegram;
using Xunit;

namespace OpenAgent.Tests.Telegram;

public class TelegramRichSenderTests
{
    [Fact]
    public async Task SendRichMarkdownAsync_PostsToSendRichMessage_AndReturnsMessageId()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"ok\":true,\"result\":{\"message_id\":42}}");
        var sender = new TelegramBotClientSender(NewHttpClient(handler));

        var messageId = await sender.SendRichMarkdownAsync(12345L, "**bold** text", default);

        Assert.Equal(42, messageId);
        Assert.EndsWith("sendRichMessage", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("rich_message", handler.LastBody);
        Assert.Contains("markdown", handler.LastBody);
    }

    [Fact]
    public async Task SendRichMarkdownDraftAsync_OnRateLimit_ReturnsRetryAfter()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.TooManyRequests,
            "{\"ok\":false,\"description\":\"Too Many Requests\",\"parameters\":{\"retry_after\":3}}");
        var sender = new TelegramBotClientSender(NewHttpClient(handler));

        var result = await sender.SendRichMarkdownDraftAsync(12345L, 7L, "hi", default);

        Assert.False(result.Ok);
        Assert.Equal(3, result.RetryAfterSeconds);
        Assert.Equal("Too Many Requests", result.Description);
        Assert.EndsWith("sendRichMessageDraft", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task SendRichMarkdownDraftAsync_OnOk_ReturnsSuccess()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"ok\":true}");
        var sender = new TelegramBotClientSender(NewHttpClient(handler));

        var result = await sender.SendRichMarkdownDraftAsync(12345L, 7L, "hi", default);

        Assert.True(result.Ok);
    }

    private static HttpClient NewHttpClient(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://api.telegram.org/botTEST/") };

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public string LastBody = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
