using Microsoft.Extensions.Logging;
using OpenAgent.App.Core.Api;
using OpenAgent.App.Core.Logging;
using OpenAgent.App.Core.Services;
using OpenAgent.App.Core.Voice;
using ZXing.Net.Maui.Controls;

namespace OpenAgent.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>().UseBarcodeReader();

        // Core — credential store and HTTP client are singletons; the WS client owns one ClientWebSocket
        // per call so it must be transient (one fresh instance per CallViewModel resolution).
        builder.Services.AddSingleton<ICredentialStore, IosKeychainCredentialStore>();
        builder.Services.AddHttpClient<IApiClient, ApiClient>();
        builder.Services.AddTransient<IVoiceWebSocketClient, VoiceWebSocketClient>();
        builder.Services.AddSingleton(sp => new ConversationCache(FileSystem.AppDataDirectory));

#if IOS
        builder.Services.AddTransient<ICallAudio, IosCallAudio>();
#endif

        // ViewModels + Pages
        builder.Services.AddTransient<Pages.OnboardingPage>();
        builder.Services.AddTransient<ViewModels.OnboardingViewModel>();
        builder.Services.AddTransient<Pages.ManualEntryPage>();
        builder.Services.AddTransient<ViewModels.ManualEntryViewModel>();
        builder.Services.AddTransient<Pages.ConversationsPage>();
        builder.Services.AddTransient<ViewModels.ConversationsViewModel>();
        builder.Services.AddTransient<Pages.CallPage>();
        builder.Services.AddTransient<ViewModels.CallViewModel>();
        builder.Services.AddTransient<Pages.SettingsPage>();
        builder.Services.AddTransient<ViewModels.SettingsViewModel>();

        // Ship logs to the agent backend so we can diagnose live-device issues without
        // attaching a debugger. The provider buffers locally and flushes every 5s; it
        // resolves IApiClient lazily on each flush via the closure below, so it's safe
        // to register before MauiApp.Build() — DI doesn't have to be ready yet.
        AgentLoggerProvider? agentLoggerProvider = null;
        builder.Logging.AddProvider(new LazyLoggerProviderShim(() =>
            agentLoggerProvider ??= new AgentLoggerProvider(() => _serviceProvider?.GetService<IApiClient>())));

#if DEBUG
        builder.Logging.AddDebug();
#endif
        var app = builder.Build();
        _serviceProvider = app.Services;
        return app;
    }

    private static IServiceProvider? _serviceProvider;
}

// Bridges Logging.AddProvider() (called pre-Build) to a provider that needs the built service container.
internal sealed class LazyLoggerProviderShim : ILoggerProvider
{
    private readonly Func<ILoggerProvider> _factory;
    private ILoggerProvider? _inner;
    public LazyLoggerProviderShim(Func<ILoggerProvider> factory) => _factory = factory;
    public ILogger CreateLogger(string categoryName) => (_inner ??= _factory()).CreateLogger(categoryName);
    public void Dispose() => _inner?.Dispose();
}
