using OpenAgent.Models.Conversations;

namespace OpenAgent.Contracts;

public interface IConversationStore : IConfigurable
{
    Conversation Create();
    Conversation? Get(string id);
    bool Delete(string id);
}
