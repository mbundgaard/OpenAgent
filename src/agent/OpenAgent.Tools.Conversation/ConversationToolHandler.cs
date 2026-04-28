using OpenAgent.Contracts;

namespace OpenAgent.Tools.Conversation;

/// <summary>
/// Tools that read or mutate per-conversation state — the active text/voice
/// model + provider pairs, the conversation intention, and the mention filter.
/// Includes get_available_models for discovering what set_model accepts.
/// </summary>
public sealed class ConversationToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public ConversationToolHandler(IConversationStore store, Func<IEnumerable<IConfigurable>> resolveAllConfigurables)
    {
        Tools =
        [
            new GetAvailableModelsTool(resolveAllConfigurables),
            new GetCurrentModelTool(store),
            new SetModelTool(store, resolveAllConfigurables),
            new SetIntentionTool(store),
            new ClearIntentionTool(store),
            new SetMentionFilterTool(store),
            new ClearMentionFilterTool(store)
        ];
    }
}
