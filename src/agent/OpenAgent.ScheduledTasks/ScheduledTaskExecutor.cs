using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.ScheduledTasks.Models;
using System.Text;

namespace OpenAgent.ScheduledTasks;

/// <summary>
/// Executes a single scheduled task: creates/retrieves a conversation, runs an LLM completion,
/// and returns the assistant response text.
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
