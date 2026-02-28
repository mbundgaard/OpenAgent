using OpenAgent.Contracts;
using OpenAgent.Conversations;
using OpenAgent.ConversationStore.InMemory;
using OpenAgent.LlmVoice.OpenAI;
using OpenAgent.WebSockets;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IConversationStore, InMemoryConversationStoreProvider>();
builder.Services.AddSingleton<ILlmVoiceProvider, OpenAiRealtimeVoiceProvider>();

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
