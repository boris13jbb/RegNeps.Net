using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RegNeps.Application.Abstractions;
using RegNeps.Application.Analytics;
using RegNeps.Application.Auth;
using RegNeps.Application.Records;
using RegNeps.Application.Reports;
using RegNeps.Infrastructure.Export;
using RegNeps.Infrastructure.Import;
using RegNeps.Infrastructure.Migration;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;

namespace RegNeps.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registra EF Core, repositorios y servicios de aplicación.
    /// Por defecto usa SQLite local (desarrollo). En intranet, configure SQL Server en ConnectionStrings:RegNeps.
    /// </summary>
    public static IServiceCollection AddRegNepsInfrastructure(
        this IServiceCollection services,
        string? connectionString,
        bool useSqlServer = false)
    {
        if (useSqlServer && !string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddDbContext<RegNepsDbContext>(options =>
                options.UseSqlServer(connectionString));
        }
        else
        {
            var sqlite = string.IsNullOrWhiteSpace(connectionString)
                ? "Data Source=regneps.db"
                : connectionString;

            services.AddDbContext<RegNepsDbContext>(options =>
                options.UseSqlite(sqlite));
        }

        services.AddScoped<INepRecordRepository, NepRecordRepository>();
        services.AddScoped<IAlertConfigRepository, AlertConfigRepository>();
        services.AddScoped<IFabricRepository, FabricRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ISavedReportRepository, SavedReportRepository>();
        services.AddScoped<ILoteTramaRepository, LoteTramaRepository>();
        services.AddScoped<IExportFileService, ExportFileService>();
        services.AddScoped<IRecordImportService, RecordImportService>();
        services.AddScoped<IFabricImportService, FabricImportService>();

        services.AddScoped<NepRecordService>();
        services.AddScoped<AuthService>();
        services.AddScoped<UserAdminService>();
        services.AddScoped<AnalyticsService>();
        services.AddScoped<ReportExportAppService>();
        services.AddScoped<HistoricalDataMigrationService>();

        return services;
    }

    public static async Task EnsureDatabaseCreatedAsync(this IServiceProvider services)
    {
        await DatabaseInitializer.InitializeAsync(services);
    }
}
