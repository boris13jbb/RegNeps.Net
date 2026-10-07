using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RegNeps.OfflineStore.Abstractions;
using RegNeps.OfflineStore.Device;
using RegNeps.OfflineStore.Services;
using RegNeps.OfflineStore.Sync;
using RegNeps.OfflineStore.Sync.Recovery;
using RegNeps.OfflineStore.Sync.Ux;

namespace RegNeps.OfflineStore;

public static class OfflineStoreBootstrap
{
    /// <summary>
    /// Registra el store local SQLite. <paramref name="databasePath"/> debe ser una ruta de archivo.
    /// El host debe registrar <see cref="ISyncApiClient"/> e <see cref="ISyncAuthCookieProvider"/>.
    /// </summary>
    public static IServiceCollection AddOfflineStore(
        this IServiceCollection services,
        string databasePath,
        string deviceIdDirectory,
        ISecureAuthMaterialStore? secureStore = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceIdDirectory);

        services.AddDbContext<LocalSyncDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath}"));

        services.AddSingleton<IDeviceIdStore>(_ => new FileDeviceIdStore(deviceIdDirectory));
        if (secureStore is null)
        {
            services.AddSingleton<ISecureAuthMaterialStore, MemorySecureAuthMaterialStore>();
        }
        else
        {
            services.AddSingleton(secureStore);
        }

        services.AddScoped<LocalStoreInitializer>();
        services.AddScoped<OfflineSessionService>();
        services.AddScoped<OfflineCaptureService>();
        services.AddScoped<OfflineCatalogService>();
        services.AddScoped<ConflictResolutionService>();
        services.AddScoped<IOfflineOutboxQuery, OfflineOutboxQuery>();
        services.AddScoped<ISyncEngine, SyncEngine>();
        services.AddScoped<OfflineSyncUxService>();
        services.AddScoped<OfflineOperationsUxService>();
        services.AddSingleton<ManualSyncGate>();
        // FASE 2E: coalescing SignalR/conectividad → Pull (singleton; un Pull activo).
        services.AddSingleton<ISyncRecoveryCoordinator, SyncRecoveryCoordinator>();
        return services;
    }
}
