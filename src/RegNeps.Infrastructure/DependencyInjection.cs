using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RegNeps.Application.Abstractions;
using RegNeps.Application.Alerts;
using RegNeps.Application.Analytics;
using RegNeps.Application.Auth;
using RegNeps.Application.Permissions;
using RegNeps.Application.Records;
using RegNeps.Application.Reports;
using RegNeps.Application.Sync;
using RegNeps.Infrastructure.Export;
using RegNeps.Infrastructure.Import;
using RegNeps.Infrastructure.Migration;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;
using RegNeps.Infrastructure.Sync;

namespace RegNeps.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registra EF Core (factory por operación), repositorios y servicios de aplicación.
    /// Por defecto usa SQLite local (desarrollo). En intranet, configure SQL Server en ConnectionStrings:RegNeps.
    /// </summary>
    public static IServiceCollection AddRegNepsInfrastructure(
        this IServiceCollection services,
        string? connectionString,
        bool useSqlServer = false)
    {
        void Configure(DbContextOptionsBuilder options)
        {
            if (useSqlServer && !string.IsNullOrWhiteSpace(connectionString))
            {
                options.UseSqlServer(connectionString);
            }
            else
            {
                var sqlite = string.IsNullOrWhiteSpace(connectionString)
                    ? "Data Source=regneps.db"
                    : connectionString;
                options.UseSqlite(sqlite);
            }
        }

        // Factory: cada operación obtiene su propio DbContext (seguro ante concurrencia en un circuito Blazor).
        services.AddDbContextFactory<RegNepsDbContext>(Configure);
        // Compatibilidad para servicios que aún inyectan RegNepsDbContext scoped.
        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<RegNepsDbContext>>().CreateDbContext());

        services.AddScoped<INepRecordRepository, NepRecordRepository>();
        services.AddScoped<IAlertConfigRepository, AlertConfigRepository>();
        services.AddScoped<IFabricRepository, FabricRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRolePermissionRepository, RolePermissionRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<RoleAdminService>();
        services.AddSingleton<IPermissionMatrix, PermissionMatrix>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<ISavedReportRepository, SavedReportRepository>();
        services.AddScoped<ILoteTramaRepository, LoteTramaRepository>();
        services.AddScoped<IExportFileService, ExportFileService>();
        services.AddScoped<IRecordImportService, RecordImportService>();
        services.AddScoped<IFabricImportService, FabricImportService>();

        services.AddSingleton<IAlertRealtimeNotifier, NoOpAlertRealtimeNotifier>();
        services.AddScoped<IAlertCriticalPublisher, AlertCriticalPublisher>();
        services.AddScoped<IAtomicNepRecordCreateStore, AtomicNepRecordCreateStore>();
        services.AddScoped<NepRecordService>();
        services.AddScoped<ISyncPersistence, SyncPersistence>();
        services.AddScoped<SyncAppService>();
        services.AddScoped<AuthService>();
        services.AddScoped<UserAdminService>();
        services.AddScoped<AnalyticsService>();
        services.AddScoped<ReportBuilderService>();
        services.AddScoped<ReportExportAppService>();
        services.AddScoped<HistoricalDataMigrationService>();
        services.AddScoped<IReportSnapshotService>(sp =>
            sp.GetRequiredService<HistoricalDataMigrationService>());

        return services;
    }

    public static async Task EnsureDatabaseCreatedAsync(this IServiceProvider services)
    {
        await DatabaseInitializer.InitializeAsync(services);
    }
}
