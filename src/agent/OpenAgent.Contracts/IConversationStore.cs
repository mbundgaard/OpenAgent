using OpenAgent.Models;

namespace OpenAgent.Contracts;

public interface IConversationStore
{
    Conversation Create();
    Conversation? Get(string id);
    bool Delete(string id);
}
