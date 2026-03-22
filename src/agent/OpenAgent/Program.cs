using OpenAgent;
using OpenAgent.Api.Endpoints;
using OpenAgent.Channel.Telegram;
using OpenAgent.Compaction;
using OpenAgent.ConfigStore.File;
using OpenAgent.Contracts;
using OpenAgent.ConversationStore.Sqlite;
using OpenAgent.LlmText.OpenAIAzure;
using OpenAgent.LlmVoice.OpenAIAzure;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Security.ApiKey;
using OpenAgent.Tools.Expand;
using OpenAgent.Tools.FileSystem;
using OpenAgent.Terminal;
using OpenAgent.Tools.Shell;
using OpenAgent.Tools.WebFetch;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

var environment = new AgentEnvironment
{
    DataPath = Environment.GetEnvironmentVariable("DATA_DIR") ?? "/home/data"
};
Directory.CreateDirectory(environment.DataPath);

builder.Services.AddSingleton(environment);

var loggingConfig = new LoggingConfig();

builder.Host.UseSerilog((context, serilog) =>
{
    serilog
        .MinimumLevel.ControlledBy(loggingConfig.DefaultLevel)
        .WriteTo.Console()
        .WriteTo.File(new CompactJsonFormatter(),
            Path.Combine(environment.DataPath, "logs", "log-.jsonl"), rollingInterval: RollingInterval.Day);

    foreach (var (module, levelSwitch) in loggingConfig.Overrides)
        serilog.MinimumLevel.Override(module, levelSwitch);
});

builder.Services.AddSingleton<SystemPromptBuilder>();
builder.Services.AddSingleton<IAgentLogic, AgentLogic>();

builder.Services.AddSingleton<IToolHandler, FileSystemToolHandler>();
builder.Services.AddSingleton<IToolHandler, ShellToolHandler>();
builder.Services.AddSingleton<IToolHandler, WebFetchToolHandler>();
builder.Services.AddSingleton<IToolHandler, ExpandToolHandler>();

builder.Services.AddSingleton(new CompactionConfig());

var agentConfig = new AgentConfig();
builder.Services.AddSingleton(agentConfig);
builder.Services.AddSingleton<IConfigurable>(new AgentConfigConfigurable(agentConfig));

builder.Services.AddSingleton<ICompactionSummarizer, CompactionSummarizer>();

builder.Services.AddSingleton<IConversationStore, SqliteConversationStore>();
builder.Services.AddKeyedSingleton<ILlmTextProvider, AzureOpenAiTextProvider>(AzureOpenAiTextProvider.ProviderKey);
builder.Services.AddKeyedSingleton<ILlmVoiceProvider, AzureOpenAiRealtimeVoiceProvider>(AzureOpenAiRealtimeVoiceProvider.ProviderKey);
builder.Services.AddSingleton<Func<string, ILlmTextProvider>>(sp =>
    key => sp.GetRequiredKeyedService<ILlmTextProvider>(key));
// Non-keyed forwarding — endpoints and VoiceSessionManager resolve the default provider
builder.Services.AddSingleton<ILlmTextProvider>(sp =>
    sp.GetRequiredKeyedService<ILlmTextProvider>(AzureOpenAiTextProvider.ProviderKey));
builder.Services.AddSingleton<ILlmVoiceProvider>(sp =>
    sp.GetRequiredKeyedService<ILlmVoiceProvider>(AzureOpenAiRealtimeVoiceProvider.ProviderKey));
builder.Services.AddSingleton<IVoiceSessionManager, VoiceSessionManager>();
builder.Services.AddSingleton<ITerminalSessionManager, TerminalSessionManager>();
builder.Services.AddSingleton<IConfigStore, FileConfigStore>();

builder.Services.AddSingleton<IConfigurable>(loggingConfig);
builder.Services.AddSingleton<IConfigurable>(sp => sp.GetRequiredService<IConversationStore>());
builder.Services.AddSingleton<IConfigurable>(sp =>
    sp.GetRequiredKeyedService<ILlmTextProvider>(AzureOpenAiTextProvider.ProviderKey));
builder.Services.AddSingleton<IConfigurable>(sp =>
    sp.GetRequiredKeyedService<ILlmVoiceProvider>(AzureOpenAiRealtimeVoiceProvider.ProviderKey));

// Authentication — swap AddApiKeyAuth for AddEntraIdAuth when migrating to Entra ID
builder.Services.AddApiKeyAuth(builder.Configuration);

// Connections — channel providers created per-connection at runtime
builder.Services.AddSingleton<IConnectionStore, FileConnectionStore>();
builder.Services.AddSingleton<IChannelProviderFactory>(sp =>
{
    var cfg = sp.GetRequiredService<AgentConfig>();
    var textProvider = sp.GetRequiredKeyedService<ILlmTextProvider>(cfg.TextProvider);
    return new TelegramChannelProviderFactory(
        sp.GetRequiredService<IConversationStore>(),
        textProvider,
        cfg.TextProvider,
        cfg.TextModel,
        sp.GetRequiredService<ILoggerFactory>());
});
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<IConnectionManager>(sp => sp.GetRequiredService<ConnectionManager>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectionManager>());

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
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () =>
{
    var assembly = typeof(Program).Assembly;
    var version = assembly.GetName().Version?.ToString() ?? "unknown";
    var informational = (System.Reflection.AssemblyInformationalVersionAttribute?)
        System.Reflection.CustomAttributeExtensions.GetCustomAttribute(
            assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
    return Results.Ok(new { status = "ok", version = informational?.InformationalVersion ?? version });
}).AllowAnonymous();
app.MapConversationEndpoints();
app.MapChatEndpoints();
app.MapWebSocketVoiceEndpoints();
app.MapWebSocketTextEndpoints();
app.MapWebSocketTerminalEndpoints();
app.MapAdminEndpoints();
app.MapSystemPromptEndpoints();
app.MapConnectionEndpoints();
app.MapFileExplorerEndpoints();
app.MapTelegramWebhookEndpoints();

// SPA fallback — serve index.html for unmatched routes (client-side routing)
app.MapFallbackToFile("index.html");

app.Run();

// Make Program accessible to integration tests
namespace OpenAgent
{
    public partial class Program;
}
