using RegNeps.Application.Analytics;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Filters;

namespace RegNeps.Application.Abstractions;

public interface INepRecordRepository
{
    Task<IReadOnlyList<NepRecord>> GetRecentAsync(int take = 100, CancellationToken ct = default);
    Task<IReadOnlyList<NepRecord>> QueryAsync(RecordFilters filters, string? viewerUserId, bool viewerSeesAll, int take = 500, CancellationToken ct = default);
    Task<NepRecord?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<NepRecord> AddAsync(NepRecord record, CancellationToken ct = default);
    Task UpdateAsync(NepRecord record, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task ClearAllAsync(CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
}

public interface IAlertConfigRepository
{
    Task<AlertConfig> GetAsync(CancellationToken ct = default);
    Task SaveAsync(AlertConfig config, CancellationToken ct = default);
}

public interface IFabricRepository
{
    Task<IReadOnlyList<Fabric>> GetActiveAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Fabric>> GetAllAsync(CancellationToken ct = default);
    Task<Fabric> AddAsync(Fabric fabric, CancellationToken ct = default);
    Task UpdateAsync(Fabric fabric, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IUserRepository
{
    Task<AppUser?> FindByUsernameAsync(string username, CancellationToken ct = default);
    Task<AppUser?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<AppUser>> ListAsync(bool includeDeleted = false, CancellationToken ct = default);
    Task<AppUser> AddAsync(AppUser user, CancellationToken ct = default);
    Task UpdateAsync(AppUser user, CancellationToken ct = default);
    Task<int> CountSuperAdminsAsync(CancellationToken ct = default);
}

public interface ISavedReportRepository
{
    Task<IReadOnlyList<SavedReport>> ListAsync(CancellationToken ct = default);
    Task<SavedReport?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<SavedReport> AddAsync(SavedReport report, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface ILoteTramaRepository
{
    Task<IReadOnlyList<LoteTramaItem>> GetActiveAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LoteTramaItem>> GetAllAsync(CancellationToken ct = default);
    Task<LoteTramaItem?> FindByCodeAsync(string code, CancellationToken ct = default);
    Task<LoteTramaItem> AddAsync(LoteTramaItem item, CancellationToken ct = default);
    Task UpdateAsync(LoteTramaItem item, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IExportFileService
{
    byte[] BuildCsv(IReadOnlyList<NepRecord> records, AlertConfig config, string style = "completo");
    byte[] BuildExcel(IReadOnlyList<NepRecord> records, AlertConfig config, string title = "Informe Neps VICUNHA", string style = "completo");
    byte[] BuildPdf(
        IReadOnlyList<NepRecord> records,
        AlertConfig config,
        string title = "Reporte de Control de Calidad — Neps VICUNHA",
        string? filtersDescription = null,
        string style = "completo");
    byte[] BuildFabricsCsv(IReadOnlyList<Fabric> fabrics);
    byte[] BuildFabricsExcel(IReadOnlyList<Fabric> fabrics);
    byte[] BuildLotesCsv(IReadOnlyList<LoteTramaItem> lotes);
    byte[] BuildLotesExcel(IReadOnlyList<LoteTramaItem> lotes);
    byte[] BuildImportTemplate();
    byte[] BuildAnalyticsCsv(AnalyticsSummary summary, string? periodDescription = null);
    byte[] BuildAnalyticsExcel(AnalyticsSummary summary, string? periodDescription = null);
    byte[] BuildAnalyticsPdf(
        AnalyticsSummary summary,
        string? periodDescription = null,
        IReadOnlyList<AnalyticsChartImage>? chartImages = null);
}

/// <summary>Imagen PNG capturada de una gráfica Chart.js para incrustar en PDF.</summary>
public sealed record AnalyticsChartImage(string Title, byte[] PngBytes);

public interface IRecordImportService
{
    Task<(int Imported, IReadOnlyList<string> Errors)> ImportExcelAsync(Stream stream, string? createdByUserId, string? createdByEmail, string? createdByRole, CancellationToken ct = default);
}

public interface IFabricImportService
{
    /// <summary>Importa telas desde Excel (columnas Nombre/Tela y opcional Codigo). Fusiona sin borrar existentes.</summary>
    Task<FabricImportResult> ImportExcelAsync(Stream stream, CancellationToken ct = default);
}

public sealed class FabricImportResult
{
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Skipped { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
}
