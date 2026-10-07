using Microsoft.Extensions.Logging;
using RegNeps.Mobile.Local;
using RegNeps.OfflineStore;
using RegNeps.OfflineStore.Services;
using RegNeps.OfflineStore.Sync;

namespace RegNeps.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        var dataDir = FileSystem.AppDataDirectory;
        var dbPath = Path.Combine(dataDir, OfflineStoreConstants.DatabaseFileName);
        var deviceDir = Path.Combine(dataDir, "device");

        builder.Services.AddOfflineStore(
            dbPath,
            deviceDir,
            new MauiSecureAuthMaterialStore());

        builder.Services.AddSingleton(_ =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            return http;
        });
        builder.Services.AddSingleton<ISyncApiClient, HttpSyncApiClient>();
        builder.Services.AddSingleton<ISyncAuthCookieProvider, MauiWebViewCookieProvider>();
        builder.Services.AddSingleton<MauiSyncHubRecoveryService>();

        builder.Services.AddTransient<OfflineCapturePage>();
        builder.Services.AddTransient<OfflineOperationsPage>();
        builder.Services.AddTransient<OfflineOperationDetailPage>();
        builder.Services.AddTransient<OfflineEditRecordPage>();
        builder.Services.AddTransient<OfflineConflictResolvePage>();
        // MainPage se resuelve desde App; registrar para DI explícito.
        builder.Services.AddTransient<MainPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();

        // Migraciones locales al arranque (aditivas; no borra Outbox).
        using (var scope = app.Services.CreateScope())
        {
            var initializer = scope.ServiceProvider.GetRequiredService<LocalStoreInitializer>();
            var devices = scope.ServiceProvider.GetRequiredService<RegNeps.OfflineStore.Abstractions.IDeviceIdStore>();
            initializer.InitializeAsync().GetAwaiter().GetResult();
            var deviceId = devices.GetOrCreateAsync().GetAwaiter().GetResult();
            initializer.EnsureSyncStateAsync(deviceId).GetAwaiter().GetResult();
        }

        return app;
    }
}
