using OpenAgent3.Api.Conversations;
using OpenAgent3.Api.WebSockets;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ConversationStore>();

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/health", () => Results.Ok());
app.MapConversationEndpoints();
app.MapWebSocketEndpoints();

app.Run();

// Make Program accessible to integration tests
public partial class Program;
