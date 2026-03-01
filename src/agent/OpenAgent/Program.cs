using OpenAgent;
using OpenAgent.Api.Endpoints;
using OpenAgent.ConfigStore.File;
using OpenAgent.Contracts;
using OpenAgent.ConversationStore.InMemory;
using OpenAgent.LlmText.OpenAIAzure;
using OpenAgent.LlmVoice.OpenAIAzure;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

var loggingConfig = new LoggingConfig();

builder.Host.UseSerilog((context, serilog) =>
{
    serilog
        .MinimumLevel.ControlledBy(loggingConfig.DefaultLevel)
        .WriteTo.Console()
        .WriteTo.File(new CompactJsonFormatter(), "logs/log-.jsonl", rollingInterval: RollingInterval.Day);

    foreach (var (module, levelSwitch) in loggingConfig.Overrides)
        serilog.MinimumLevel.Override(module, levelSwitch);
});

builder.Services.AddSingleton<IAgentLogic, AgentLogic>();
builder.Services.AddSingleton<IConversationStore, InMemoryConversationStoreProvider>();
builder.Services.AddSingleton<ILlmVoiceProvider, AzureOpenAiRealtimeVoiceProvider>();
builder.Services.AddSingleton<ILlmTextProvider, AzureOpenAiTextProvider>();
builder.Services.AddSingleton<IVoiceSessionManager, VoiceSessionManager>();
builder.Services.AddSingleton<IConfigStore, FileConfigStore>(_ => new FileConfigStore(builder.Environment.ContentRootPath));

builder.Services.AddSingleton<IConfigurable>(loggingConfig);
builder.Services.AddSingleton<IConfigurable>(sp => sp.GetRequiredService<IConversationStore>());
builder.Services.AddSingleton<IConfigurable>(sp => sp.GetRequiredService<ILlmTextProvider>());
builder.Services.AddSingleton<IConfigurable>(sp => sp.GetRequiredService<ILlmVoiceProvider>());

var app = builder.Build();

// Load persisted provider configs (providers stay unconfigured if no config exists)
var configStore = app.Services.GetRequiredService<IConfigStore>();
foreach (var configurable in app.Services.GetServices<IConfigurable>())
{
    var config = configStore.Load(configurable.Key);
    if (config.HasValue)
        configurable.Configure(config.Value);
}

app.UseWebSockets();

app.MapGet("/health", () => Results.Ok());
app.MapConversationEndpoints();
app.MapChatEndpoints();
app.MapWebSocketVoiceEndpoints();
app.MapWebSocketTextEndpoints();
app.MapAdminEndpoints();

app.Run();

// Make Program accessible to integration tests
namespace OpenAgent
{
    public partial class Program;
}
