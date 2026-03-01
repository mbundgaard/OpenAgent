using OpenAgent;
using OpenAgent.Api.Conversations;
using OpenAgent.Api.WebSockets;
using OpenAgent.Contracts;
using OpenAgent.ConversationStore.InMemory;
using OpenAgent.LlmVoice.OpenAIAzure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IAgentLogic, AgentLogic>();
builder.Services.AddSingleton<IConversationStore, InMemoryConversationStoreProvider>();
builder.Services.AddSingleton<ILlmVoiceProvider, AzureOpenAiRealtimeVoiceProvider>();

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/health", () => Results.Ok());
app.MapConversationEndpoints();
app.MapWebSocketEndpoints();

app.Run();

// Make Program accessible to integration tests
namespace OpenAgent
{
    public partial class Program;
}
