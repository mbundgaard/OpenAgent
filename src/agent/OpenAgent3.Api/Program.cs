using OpenAgent3.Api.Conversations;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ConversationStore>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok());
app.MapConversationEndpoints();

app.Run();

// Make Program accessible to integration tests
public partial class Program;
