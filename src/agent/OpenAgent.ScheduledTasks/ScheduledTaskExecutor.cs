using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.ScheduledTasks.Models;
using System.Text;

namespace OpenAgent.ScheduledTasks;

/// <summary>
/// Runs ONE task's LLM turn. Kept deliberately thin: resolve the dedicated conversation,
/// inject the prompt as a user message, stream the completion, collect the final text.
/// Has no knowledge of scheduling, state updates, or delivery — those are the service's job.
/// This separation makes the execution path trivially testable (just pass a fake provider)
/// and keeps the service focused on timing and persistence.
///
/// Each task owns its own conversation (scheduledtask:{taskId}) so history accumulates across
/// runs. The conversation uses ConversationType.ScheduledTask which selects the right system
/// prompt template. Compaction handles long-running tasks naturally — no special pruning needed.
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
    /// Executes the task's prompt as a user message against a dedicated conversation.
    /// Returns the collected assistant response text.
    /// </summary>
    public async Task<string> ExecuteAsync(ScheduledTask task, string? promptOverride, CancellationToken ct)
    {
        var conversationId = $"scheduledtask:{task.Id}";
        var prompt = promptOverride ?? task.Prompt;

        // Get or create the dedicated conversation for this task
        var conversation = conversationStore.GetOrCreate(
            conversationId,
            "scheduledtask",
            ConversationType.ScheduledTask,
            agentConfig.TextProvider,
            agentConfig.TextModel);

        // Build the user message
        var userMessage = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversationId,
            Role = "user",
            Content = prompt
        };

        // Resolve the text provider and run completion
        var provider = textProviderResolver(conversation.Provider);
        var responseBuilder = new StringBuilder();

        await foreach (var evt in provider.CompleteAsync(conversation, userMessage, ct))
        {
            if (evt is TextDelta delta)
                responseBuilder.Append(delta.Content);
        }

        var response = responseBuilder.ToString();
        logger.LogInformation("Scheduled task '{Name}' ({Id}) completed. Response length: {Length}",
            task.Name, task.Id, response.Length);

        return response;
    }
}
