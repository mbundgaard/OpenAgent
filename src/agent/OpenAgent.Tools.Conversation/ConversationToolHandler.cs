using OpenAgent.Contracts;

namespace OpenAgent.Tools.Conversation;

/// <summary>
/// Tools that read or mutate per-conversation state — the active model/provider
/// and the conversation intention. Includes get_available_models for discovering
/// what set_model accepts.
/// </summary>
public sealed class ConversationToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public ConversationToolHandler(IConversationStore store, Func<IEnumerable<ILlmTextProvider>> resolveProviders)
    {
        Tools =
        [
            new GetAvailableModelsTool(resolveProviders),
            new GetCurrentModelTool(store),
            new SetModelTool(store, resolveProviders),
            new SetIntentionTool(store),
            new ClearIntentionTool(store)
        ];
    }
}
