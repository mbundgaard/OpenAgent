using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.ScheduledTasks.Models;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.ScheduledTasks;

/// <summary>
/// Routes completed task output to the configured delivery target.
/// </summary>
internal sealed class DeliveryRouter(
    IConnectionManager connectionManager,
    IHttpClientFactory httpClientFactory,
    ILogger<DeliveryRouter> logger)
{
    /// <summary>
    /// Delivers the task response based on the task's delivery configuration.
    /// </summary>
    public async Task DeliverAsync(ScheduledTask task, string response, CancellationToken ct)
    {
        var delivery = task.Delivery;
        if (delivery is null || delivery.Mode == DeliveryMode.Silent)
            return;

        switch (delivery.Mode)
        {
            case DeliveryMode.Channel:
                await DeliverToChannelAsync(task, delivery, response, ct);
                break;

            case DeliveryMode.Webhook:
                await DeliverToWebhookAsync(task, delivery, response, ct);
                break;
        }
    }

    private async Task DeliverToChannelAsync(
        ScheduledTask task, DeliveryConfig delivery, string response, CancellationToken ct)
    {
        if (delivery.ConnectionId is null || delivery.ChatId is null)
        {
            logger.LogWarning("Task '{Name}' ({Id}) has channel delivery but missing connectionId or chatId",
                task.Name, task.Id);
            return;
        }

        var provider = connectionManager.GetProvider(delivery.ConnectionId);
        if (provider is not IOutboundSender sender)
        {
            logger.LogWarning(
                "Task '{Name}' ({Id}): connection '{ConnectionId}' does not support outbound messaging. Falling back to silent.",
                task.Name, task.Id, delivery.ConnectionId);
            return;
        }

        await sender.SendMessageAsync(delivery.ChatId, response, ct);
        logger.LogInformation("Task '{Name}' ({Id}) delivered to channel {ConnectionId}:{ChatId}",
            task.Name, task.Id, delivery.ConnectionId, delivery.ChatId);
    }

    private async Task DeliverToWebhookAsync(
        ScheduledTask task, DeliveryConfig delivery, string response, CancellationToken ct)
    {
        if (delivery.WebhookUrl is null)
        {
            logger.LogWarning("Task '{Name}' ({Id}) has webhook delivery but missing webhookUrl", task.Name, task.Id);
            return;
        }

        var client = httpClientFactory.CreateClient("ScheduledTaskWebhook");
        client.Timeout = TimeSpan.FromSeconds(10);

        var payload = new WebhookPayload
        {
            TaskId = task.Id,
            TaskName = task.Name,
            Status = "success",
            Response = response,
            ExecutedAt = DateTimeOffset.UtcNow
        };

        await client.PostAsJsonAsync(delivery.WebhookUrl, payload, ct);
        logger.LogInformation("Task '{Name}' ({Id}) delivered to webhook {Url}", task.Name, task.Id, delivery.WebhookUrl);
    }

    internal sealed class WebhookPayload
    {
        [JsonPropertyName("taskId")]
        public required string TaskId { get; init; }

        [JsonPropertyName("taskName")]
        public required string TaskName { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }

        [JsonPropertyName("response")]
        public required string Response { get; init; }

        [JsonPropertyName("executedAt")]
        public required DateTimeOffset ExecutedAt { get; init; }
    }
}
