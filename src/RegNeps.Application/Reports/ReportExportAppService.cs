using System.Text.Json;
using RegNeps.Application.Abstractions;
using RegNeps.Application.Analytics;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Filters;
using RegNeps.Domain.Services;

namespace RegNeps.Application.Reports;

public sealed class ReportExportAppService
{
    private readonly INepRecordRepository _records;
    private readonly IAlertConfigRepository _alertConfig;
    private readonly IExportFileService _export;
    private readonly ISavedReportRepository _saved;

    public ReportExportAppService(
        INepRecordRepository records,
        IAlertConfigRepository alertConfig,
        IExportFileService export,
        ISavedReportRepository saved)
    {
        _records = records;
        _alertConfig = alertConfig;
        _export = export;
        _saved = saved;
    }

    public async Task<(byte[] Bytes, string FileName, string ContentType)> ExportAsync(
        string format,
        RecordFilters filters,
        string? viewerUserId,
        bool viewerSeesAll,
        string style = "completo",
        CancellationToken ct = default)
    {
        filters ??= new RecordFilters();
        ReportDateRange.EnsureConsolidatedRange(filters);

        var config = await _alertConfig.GetAsync(ct);
        // Límite alto: el informe consolidado debe incluir todo el periodo, no un solo día.
        var records = await _records.QueryAsync(filters, viewerUserId, viewerSeesAll, 50_000, ct);
        if (records.Count == 0)
        {
            throw new InvalidOperationException(
                "No hay registros en el rango de fechas seleccionado. " +
                "Ajuste el periodo e intente de nuevo.");
        }

        // Orden cronológico ascendente (consolidado del periodo).
        records = records
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Telar, StringComparer.OrdinalIgnoreCase)
            .ToList();

        format = format.Trim().ToLowerInvariant();
        style = string.IsNullOrWhiteSpace(style) ? "completo" : style.Trim().ToLowerInvariant();
        var filtersDescription = DescribeFilters(filters);
        var styleSuffix = style is "clasico" or "classic" ? "_clasico" : "";
        var periodSuffix = PeriodFileSuffix(filters);

        return format switch
        {
            "csv" => (_export.BuildCsv(records, config, style),
                $"reporte_neps{styleSuffix}{periodSuffix}_{Stamp()}.csv", "text/csv"),
            "xlsx" or "excel" => (_export.BuildExcel(records, config, style: style),
                $"reporte_neps{styleSuffix}{periodSuffix}_{Stamp()}.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            "pdf" => (_export.BuildPdf(records, config, filtersDescription: filtersDescription, style: style),
                $"reporte_neps{styleSuffix}{periodSuffix}_{Stamp()}.pdf", "application/pdf"),
            _ => throw new ArgumentException("Formato no soportado. Use csv, xlsx o pdf.")
        };
    }

    private static string PeriodFileSuffix(RecordFilters filters)
    {
        if (filters.FromUtc is null || (filters.ToUtc is null && filters.ToExclusiveUtc is null))
        {
            return "";
        }

        var from = filters.FromUtc.Value.ToLocalTime();
        var to = filters.ToExclusiveUtc is not null
            ? filters.ToExclusiveUtc.Value.ToLocalTime().AddTicks(-1)
            : filters.ToUtc!.Value.ToLocalTime();
        return $"_{from:yyyyMMdd}-{to:yyyyMMdd}";
    }

    private static string DescribeFilters(RecordFilters filters)
    {
        var parts = new List<string>();
        if (filters.FromUtc is not null)
        {
            parts.Add($"Desde {filters.FromUtc.Value.ToLocalTime():dd/MM/yyyy}");
        }

        if (filters.ToExclusiveUtc is not null)
        {
            parts.Add($"Hasta {filters.ToExclusiveUtc.Value.ToLocalTime().AddTicks(-1):dd/MM/yyyy}");
        }
        else if (filters.ToUtc is not null)
        {
            parts.Add($"Hasta {filters.ToUtc.Value.ToLocalTime():dd/MM/yyyy}");
        }

        if (!string.IsNullOrWhiteSpace(filters.Telar))
        {
            parts.Add($"Telar={filters.Telar}");
        }

        if (!string.IsNullOrWhiteSpace(filters.Tela))
        {
            parts.Add($"Tela={filters.Tela}");
        }

        return parts.Count == 0
            ? "Sin filtros (todos los registros visibles)"
            : string.Join(" · ", parts);
    }

    public async Task<SavedReport> SaveReportAsync(
        string name,
        RecordFilters filters,
        string? userId,
        string? userName,
        bool viewerSeesAll,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El nombre del informe es obligatorio.");
        }

        filters ??= new RecordFilters();
        ReportDateRange.EnsureConsolidatedRange(filters);

        var config = await _alertConfig.GetAsync(ct);
        var records = await _records.QueryAsync(filters, userId, viewerSeesAll, 50_000, ct);
        if (records.Count == 0)
        {
            throw new InvalidOperationException(
                "No hay registros en el rango seleccionado. No se guardó el informe.");
        }

        var criticos = records.Count(r => AlertEvaluator.GetLevel(r.Neps, config) == AlertLevel.Critico);
        var avg = records.Average(r => r.Neps);

        var fromLabel = filters.FromUtc?.ToLocalTime().ToString("dd/MM/yyyy") ?? "?";
        var toLabel = filters.ToExclusiveUtc?.ToLocalTime().AddTicks(-1).ToString("dd/MM/yyyy")
                      ?? filters.ToUtc?.ToLocalTime().ToString("dd/MM/yyyy")
                      ?? "?";

        var report = new SavedReport
        {
            Name = name.Trim(),
            CreatedByUserId = userId,
            CreatedByName = userName,
            RecordCount = records.Count,
            FiltersJson = JsonSerializer.Serialize(filters),
            SummaryText =
                $"{records.Count} registros del {fromLabel} al {toLabel}, " +
                $"promedio {avg:0.##} neps, {criticos} críticos."
        };
        return await _saved.AddAsync(report, ct);
    }

    public Task<IReadOnlyList<SavedReport>> ListSavedAsync(CancellationToken ct = default) =>
        _saved.ListAsync(ct);

    public Task DeleteSavedAsync(Guid id, CancellationToken ct = default) =>
        _saved.DeleteAsync(id, ct);

    public async Task<(byte[] Bytes, string FileName, string ContentType)> ExportSavedAsync(
        Guid savedReportId,
        string format,
        string? viewerUserId,
        bool viewerSeesAll,
        CancellationToken ct = default)
    {
        var report = await _saved.GetByIdAsync(savedReportId, ct)
            ?? throw new InvalidOperationException("Informe no encontrado.");

        var filters = ParseFilters(report.FiltersJson);
        var file = await ExportAsync(format, filters, viewerUserId, viewerSeesAll, "completo", ct);
        var safeName = SanitizeFileName(report.Name);
        var ext = Path.GetExtension(file.FileName);
        return (file.Bytes, $"{safeName}_{Stamp()}{ext}", file.ContentType);
    }

    public RecordFilters GetSavedFilters(SavedReport report) => ParseFilters(report.FiltersJson);

    public async Task<SavedReport?> GetSavedAsync(Guid id, CancellationToken ct = default) =>
        await _saved.GetByIdAsync(id, ct);

    private static RecordFilters ParseFilters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new RecordFilters();

        try
        {
            var filters = JsonSerializer.Deserialize<RecordFilters>(json) ?? new RecordFilters();
            ReportDateRange.EnsureConsolidatedRange(filters);
            return filters;
        }
        catch (JsonException)
        {
            return new RecordFilters();
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "informe" : cleaned;
    }

    public async Task<ProfessionalReportPreview> BuildProfessionalPreviewAsync(
        DateTime? fromUtc,
        DateTime? toUtc,
        string? viewerUserId,
        bool viewerSeesAll,
        string? telar = null,
        string? tela = null,
        CancellationToken ct = default)
    {
        var filters = new RecordFilters
        {
            Telar = string.IsNullOrWhiteSpace(telar) ? null : telar.Trim(),
            Tela = string.IsNullOrWhiteSpace(tela) ? null : tela.Trim()
        };

        DateTime? rangeFromUtc = fromUtc;
        DateTime? rangeToInclusiveUtc = toUtc;
        if (fromUtc is not null && toUtc is not null)
        {
            var fromDay = fromUtc.Value.ToLocalTime().Date;
            var toDay = toUtc.Value.ToLocalTime().Date;
            var range = ReportDateRange.FromLocalCalendarDates(fromDay, toDay);
            range.ApplyTo(filters);
            rangeFromUtc = range.FromUtc;
            rangeToInclusiveUtc = range.ToInclusiveUtc;
        }

        var analytics = new AnalyticsService(_records, _alertConfig);
        var summary = await analytics.BuildAsync(
            rangeFromUtc, rangeToInclusiveUtc, viewerUserId, viewerSeesAll, telar, tela, ct);
        var config = await _alertConfig.GetAsync(ct);

        var records = await _records.QueryAsync(filters, viewerUserId, viewerSeesAll, 50_000, ct);
        records = records.OrderBy(r => r.CreatedAt).ToList();

        var conclusions = new List<string>();
        if (records.Count == 0)
        {
            conclusions.Add("No hay registros en el rango de fechas seleccionado.");
        }
        else
        {
            conclusions.Add(
                $"Informe consolidado: {records.Count} registros " +
                $"del {rangeFromUtc?.ToLocalTime():dd/MM/yyyy} al {rangeToInclusiveUtc?.ToLocalTime():dd/MM/yyyy}.");
        }

        if (summary.CriticalCount > 0)
        {
            conclusions.Add($"Se detectaron {summary.CriticalCount} mediciones críticas en el periodo.");
        }

        if (summary.WarningCount > 0)
        {
            conclusions.Add($"Hay {summary.WarningCount} advertencias que requieren seguimiento.");
        }

        if (summary.WorstTelars.Count > 0)
        {
            conclusions.Add($"Telar con peor promedio: {summary.WorstTelars[0].Key} ({summary.WorstTelars[0].AverageNeps:0.##} neps).");
        }

        if (records.Count > 0 && summary.QualityIndex >= 90)
        {
            conclusions.Add("Índice de calidad alto (≥ 90). Mantener controles rutinarios.");
        }
        else if (records.Count > 0 && summary.QualityIndex < 70)
        {
            conclusions.Add("Índice de calidad bajo (< 70). Priorizar acciones correctivas.");
        }

        return new ProfessionalReportPreview
        {
            Summary = summary,
            Config = config,
            Records = records,
            Conclusions = conclusions,
            FromUtc = rangeFromUtc,
            ToUtc = rangeToInclusiveUtc
        };
    }

    public Task<(byte[] Bytes, string FileName, string ContentType)> ExportProfessionalAsync(
        string format,
        DateTime? fromUtc,
        DateTime? toUtc,
        string? viewerUserId,
        bool viewerSeesAll,
        CancellationToken ct = default)
    {
        var filters = new RecordFilters();
        if (fromUtc is not null && toUtc is not null)
        {
            ReportDateRange.FromLocalCalendarDates(
                fromUtc.Value.ToLocalTime().Date,
                toUtc.Value.ToLocalTime().Date).ApplyTo(filters);
        }

        return ExportAsync(format, filters, viewerUserId, viewerSeesAll, "completo", ct);
    }

    private static string Stamp() => DateTime.Now.ToString("yyyyMMdd_HHmm");
}

public sealed class ProfessionalReportPreview
{
    public required AnalyticsSummary Summary { get; init; }
    public required AlertConfig Config { get; init; }
    public required IReadOnlyList<NepRecord> Records { get; init; }
    public required IReadOnlyList<string> Conclusions { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
}
