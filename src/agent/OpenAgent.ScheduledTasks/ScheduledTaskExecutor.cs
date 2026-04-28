using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.ScheduledTasks.Models;
using System.Text;

namespace OpenAgent.ScheduledTasks;

/// <summary>
/// Runs ONE task's LLM turn. Kept deliberately thin: resolve the conversation,
/// inject the prompt as a user message, stream the completion, collect the final text.
/// Has no knowledge of scheduling, state updates, or delivery — those are the service's job.
/// This separation makes the execution path trivially testable (just pass a fake provider)
/// and keeps the service focused on timing and persistence.
///
/// Conversation resolution: a task's ConversationId is null on create and gets assigned a
/// fresh GUID on first run. Subsequent runs reuse the same conversation so history accumulates
/// across runs. A task's ConversationId can also be explicitly set to an existing conversation
/// (e.g. a Telegram chat) so the task runs inside that chat and replies flow naturally.
/// The prompt is prefixed with "[Scheduled task: name]" so the LLM can distinguish it from
/// real user messages — important in shared conversations.
///
/// The promptOverride parameter is used by webhook triggers to inject event context without
/// mutating the stored task prompt.
/// </summary>
internal sealed class ScheduledTaskExecutor(
    IConversationStore conversationStore,
    Func<string, ILlmTextProvider> textProviderResolver,
    AgentConfig agentConfig,
    ILogger<ScheduledTaskExecutor> logger)
{
    /// <summary>
    /// Result of executing a task: the conversation it ran in (needed for delivery routing)
    /// and the assistant response text.
    /// </summary>
    public readonly record struct ExecutionResult(Conversation Conversation, string Response);

    /// <summary>
    /// Executes the task's prompt against its conversation (auto-creating one on first run).
    /// Mutates task.ConversationId on first run — caller is responsible for persisting the task.
    /// Returns the conversation and the collected assistant response text.
    /// </summary>
    public async Task<ExecutionResult> ExecuteAsync(ScheduledTask task, string? promptOverride, CancellationToken ct)
    {
        var rawPrompt = promptOverride ?? task.Prompt;

        // Prefix the prompt so the LLM can distinguish task-triggered turns from real user messages.
        var prompt = $"[Scheduled task: {task.Name}]\n{rawPrompt}";

        // First run: generate a fresh GUID and write it back to the task.
        // Subsequent runs: reuse the existing ConversationId.
        if (string.IsNullOrEmpty(task.ConversationId))
        {
            task.ConversationId = Guid.NewGuid().ToString();
            logger.LogInformation("First run of task '{Name}' ({Id}) — assigned conversation {ConversationId}",
                task.Name, task.Id, task.ConversationId);
        }

        // Find or create the conversation — GetOrCreate is idempotent on the ID
        var conversation = conversationStore.GetOrCreate(
            task.ConversationId,
            "scheduledtask",
            agentConfig.TextProvider,
            agentConfig.TextModel,
            agentConfig.VoiceProvider,
            agentConfig.VoiceModel);

        // Build the user message
        var userMessage = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversation.Id,
            Role = "user",
            Content = prompt
        };

        // Resolve the text provider and run completion
        var provider = textProviderResolver(conversation.TextProvider);
        var responseBuilder = new StringBuilder();

        await foreach (var evt in provider.CompleteAsync(conversation, userMessage, ct))
        {
            if (evt is TextDelta delta)
                responseBuilder.Append(delta.Content);
        }

        var response = responseBuilder.ToString();
        logger.LogInformation("Scheduled task '{Name}' ({Id}) completed. Response length: {Length}",
            task.Name, task.Id, response.Length);

        return new ExecutionResult(conversation, response);
    }
}
