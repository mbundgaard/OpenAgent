using Microsoft.Extensions.Logging;
using OpenAgent.App.Core.Api;
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

#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
