using OpenAgent;
using OpenAgent.Api.Endpoints;
using OpenAgent.Channel.Telegram;
using OpenAgent.Channel.WhatsApp;
using OpenAgent.Compaction;
using OpenAgent.ContextPruning;
using OpenAgent.ConfigStore.File;
using OpenAgent.Contracts;
using OpenAgent.ConversationStore.Sqlite;
using OpenAgent.LlmText.AnthropicSubscription;
using OpenAgent.LlmText.OpenAIAzure;
using OpenAgent.LlmVoice.GeminiLive;
using OpenAgent.LlmVoice.GrokRealtime;
using OpenAgent.LlmVoice.OpenAIAzure;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Security.ApiKey;
using OpenAgent.Embedding.OnnxBge;
using OpenAgent.Embedding.OnnxMultilingualE5;
using OpenAgent.MemoryIndex;
using OpenAgent.Tools.Expand;
using OpenAgent.Tools.FileSystem;
using OpenAgent.Terminal;
using OpenAgent.ScheduledTasks;
using OpenAgent.Skills;
using OpenAgent.Tools.Conversation;
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

// Bootstrap — ensure required folders and default files exist
DataDirectoryBootstrap.Run(environment.DataPath);

builder.Services.AddSingleton(environment);

// Skill catalog — discover skills from {dataPath}/skills/
builder.Services.AddSingleton<SkillCatalog>(sp =>
    new SkillCatalog(
        Path.Combine(environment.DataPath, "skills"),
        sp.GetRequiredService<ILogger<SkillCatalog>>()));
// SkillToolHandler registered as IToolHandler — AgentLogic aggregates all IToolHandler
// registrations via IEnumerable<IToolHandler> (see AgentLogic.cs:14-18)
builder.Services.AddSingleton<IToolHandler>(sp =>
    new SkillToolHandler(
        sp.GetRequiredService<SkillCatalog>(),
        sp.GetRequiredService<IConversationStore>(),
        sp.GetRequiredService<ILogger<SkillToolHandler>>()));

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
builder.Services.AddSingleton<IToolHandler>(sp =>
    new ConversationToolHandler(
        sp.GetRequiredService<IConversationStore>(),
        () => new ILlmTextProvider[]
        {
            sp.GetRequiredKeyedService<ILlmTextProvider>(AzureOpenAiTextProvider.ProviderKey),
            sp.GetRequiredKeyedService<ILlmTextProvider>(AnthropicSubscriptionTextProvider.ProviderKey)
        }));

builder.Services.AddScheduledTasks(environment.DataPath);

// Memory index — embedding providers, chunk store, hosted indexing loop, and tools.
// The provider is resolved lazily (first factory invocation is on first RunAsync), so a
// missing model file doesn't crash startup when the feature is disabled via empty
// AgentConfig.EmbeddingProvider. The specific model (small/base/large) comes from
// AgentConfig.EmbeddingModel and is loaded on first embedding call.
builder.Services.AddKeyedSingleton<IEmbeddingProvider>(OnnxMultilingualE5EmbeddingProvider.ProviderKey, (sp, _) =>
    new OnnxMultilingualE5EmbeddingProvider(
        sp.GetRequiredService<AgentEnvironment>(),
        sp.GetRequiredService<AgentConfig>(),
        sp.GetRequiredService<ILogger<OnnxMultilingualE5EmbeddingProvider>>()));
builder.Services.AddKeyedSingleton<IEmbeddingProvider>(OnnxBgeEmbeddingProvider.ProviderKey, (sp, _) =>
    new OnnxBgeEmbeddingProvider(
        sp.GetRequiredService<AgentEnvironment>(),
        sp.GetRequiredService<AgentConfig>(),
        sp.GetRequiredService<ILogger<OnnxBgeEmbeddingProvider>>()));
builder.Services.AddSingleton<Func<string, IEmbeddingProvider>>(sp =>
    key => sp.GetRequiredKeyedService<IEmbeddingProvider>(key));
builder.Services.AddMemoryIndex();
builder.Services.AddContextPruning();

builder.Services.AddSingleton(new CompactionConfig());

var agentConfig = new AgentConfig();
builder.Services.AddSingleton(agentConfig);
builder.Services.AddSingleton<IConfigurable>(new AgentConfigConfigurable(agentConfig));

builder.Services.AddSingleton<ICompactionSummarizer, CompactionSummarizer>();

builder.Services.AddSingleton<IConversationStore, SqliteConversationStore>();
builder.Services.AddKeyedSingleton<ILlmTextProvider, AzureOpenAiTextProvider>(AzureOpenAiTextProvider.ProviderKey);
builder.Services.AddKeyedSingleton<ILlmTextProvider, AnthropicSubscriptionTextProvider>(AnthropicSubscriptionTextProvider.ProviderKey);
builder.Services.AddKeyedSingleton<ILlmVoiceProvider, AzureOpenAiRealtimeVoiceProvider>(AzureOpenAiRealtimeVoiceProvider.ProviderKey);
builder.Services.AddKeyedSingleton<ILlmVoiceProvider, GrokRealtimeVoiceProvider>(GrokRealtimeVoiceProvider.ProviderKey);
builder.Services.AddKeyedSingleton<ILlmVoiceProvider, GeminiLiveVoiceProvider>(GeminiLiveVoiceProvider.ProviderKey);
builder.Services.AddSingleton<Func<string, ILlmTextProvider>>(sp =>
    key => sp.GetRequiredKeyedService<ILlmTextProvider>(key));
builder.Services.AddSingleton<Func<string, ILlmVoiceProvider>>(sp =>
    key => sp.GetRequiredKeyedService<ILlmVoiceProvider>(key));
// Non-keyed forwarding — kept for backward compatibility with tests
builder.Services.AddSingleton<ILlmTextProvider>(sp =>
    sp.GetRequiredKeyedService<ILlmTextProvider>(AzureOpenAiTextProvider.ProviderKey));
builder.Services.AddSingleton<ILlmVoiceProvider>(sp =>
    sp.GetRequiredKeyedService<ILlmVoiceProvider>(AzureOpenAiRealtimeVoiceProvider.ProviderKey));
builder.Services.AddSingleton<IVoiceSessionManager, VoiceSessionManager>();
builder.Services.AddSingleton<IWebSocketRegistry, WebSocketRegistry>();
builder.Services.AddSingleton<ITerminalSessionManager, TerminalSessionManager>();
builder.Services.AddSingleton<IConfigStore, FileConfigStore>();

builder.Services.AddSingleton<IConfigurable>(loggingConfig);
builder.Services.AddSingleton<IConfigurable>(sp => sp.GetRequiredService<IConversationStore>());
builder.Services.AddSingleton<IConfigurable>(sp =>
    sp.GetRequiredKeyedService<ILlmTextProvider>(AzureOpenAiTextProvider.ProviderKey));
builder.Services.AddSingleton<IConfigurable>(sp =>
    sp.GetRequiredKeyedService<ILlmTextProvider>(AnthropicSubscriptionTextProvider.ProviderKey));
builder.Services.AddSingleton<IConfigurable>(sp =>
    sp.GetRequiredKeyedService<ILlmVoiceProvider>(AzureOpenAiRealtimeVoiceProvider.ProviderKey));
builder.Services.AddSingleton<IConfigurable>(sp =>
    sp.GetRequiredKeyedService<ILlmVoiceProvider>(GrokRealtimeVoiceProvider.ProviderKey));
builder.Services.AddSingleton<IConfigurable>(sp =>
    sp.GetRequiredKeyedService<ILlmVoiceProvider>(GeminiLiveVoiceProvider.ProviderKey));

// Authentication — swap AddApiKeyAuth for AddEntraIdAuth when migrating to Entra ID
builder.Services.AddApiKeyAuth(builder.Configuration);

// Connections — channel providers created per-connection at runtime
builder.Services.AddSingleton<IConnectionStore, FileConnectionStore>();
builder.Services.AddSingleton<IChannelProviderFactory>(sp =>
    new TelegramChannelProviderFactory(
        sp.GetRequiredService<IConversationStore>(),
        sp.GetRequiredService<IConnectionStore>(),
        sp.GetRequiredService<Func<string, ILlmTextProvider>>(),
        sp.GetRequiredService<AgentConfig>(),
        sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IChannelProviderFactory>(sp =>
    new WhatsAppChannelProviderFactory(
        sp.GetRequiredService<IConversationStore>(),
        sp.GetRequiredService<IConnectionStore>(),
        sp.GetRequiredService<Func<string, ILlmTextProvider>>(),
        sp.GetRequiredService<AgentConfig>(),
        sp.GetRequiredService<AgentEnvironment>(),
        sp.GetRequiredService<ILoggerFactory>()));
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
app.MapLogEndpoints();
app.MapTelegramWebhookEndpoints();
app.MapWhatsAppEndpoints();
app.MapScheduledTaskEndpoints();
app.MapToolEndpoints();
app.MapMemoryIndexEndpoints();

// SPA fallback — serve index.html for unmatched routes (client-side routing)
app.MapFallbackToFile("index.html");

app.Run();

// Make Program accessible to integration tests
namespace OpenAgent
{
    public partial class Program;
}
