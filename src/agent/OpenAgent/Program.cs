using OpenAgent;
using OpenAgent.Api.Endpoints;
using OpenAgent.Contracts;
using OpenAgent.ConversationStore.InMemory;
using OpenAgent.LlmText.OpenAIAzure;
using OpenAgent.LlmVoice.OpenAIAzure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IAgentLogic, AgentLogic>();
builder.Services.AddSingleton<IConversationStore, InMemoryConversationStoreProvider>();
builder.Services.AddSingleton<ILlmVoiceProvider, AzureOpenAiRealtimeVoiceProvider>();
builder.Services.AddSingleton<ILlmTextProvider, AzureOpenAiTextProvider>();
builder.Services.AddSingleton<IVoiceSessionManager, VoiceSessionManager>();

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/health", () => Results.Ok());
app.MapConversationEndpoints();
app.MapChatEndpoints();
app.MapWebSocketVoiceEndpoints();
app.MapWebSocketTextEndpoints();

app.Run();

// Make Program accessible to integration tests
namespace OpenAgent
{
    public partial class Program;
}
