using ConversationStore.InMemory;
using OpenAgent.Contracts;
using OpenAgent.Conversations;
using OpenAgent.WebSockets;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IConversationStore, InMemoryConversationStore>();

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
