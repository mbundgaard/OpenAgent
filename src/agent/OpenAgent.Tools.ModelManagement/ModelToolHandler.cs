using OpenAgent.Contracts;

namespace OpenAgent.Tools.ModelManagement;

/// <summary>
/// Groups model management tools (get_available_models, get_current_model, set_model)
/// under a single handler for DI registration.
/// </summary>
public sealed class ModelToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public ModelToolHandler(IConversationStore store, IEnumerable<ILlmTextProvider> providers)
    {
        var providerList = providers.ToList();
        Tools =
        [
            new GetAvailableModelsTool(providerList),
            new GetCurrentModelTool(store),
            new SetModelTool(store, providerList)
        ];
    }
}
