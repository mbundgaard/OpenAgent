using OpenAgent.Models.Conversations;
using OpenAgent.Models.Text;

namespace OpenAgent.Contracts;

/// <summary>
/// Stateless text completion provider. Sends conversation history to an LLM and returns the response.
/// The provider calls IAgentLogic for system prompt, tools, message history, and tool execution.
/// </summary>
public interface ILlmTextProvider : IConfigurable
{
    Task<TextResponse> CompleteAsync(Conversation conversation, string userInput, CancellationToken ct = default);

    IAsyncEnumerable<string> StreamAsync(Conversation conversation, string userInput, CancellationToken ct = default);
}
