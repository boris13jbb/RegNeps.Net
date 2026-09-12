using System.Globalization;
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
    private readonly IReportSnapshotService _snapshots;

    public ReportExportAppService(
        INepRecordRepository records,
        IAlertConfigRepository alertConfig,
        IExportFileService export,
        ISavedReportRepository saved,
        IReportSnapshotService snapshots)
    {
        _records = records;
        _alertConfig = alertConfig;
        _export = export;
        _saved = saved;
        _snapshots = snapshots;
    }

    public async Task<(byte[] Bytes, string FileName, string ContentType)> ExportAsync(
        string format,
        RecordFilters filters,
        string? viewerUserId,
        bool viewerSeesAll,
        string style = "completo",
        IReadOnlyList<string>? columns = null,
        CancellationToken ct = default)
    {
        filters ??= new RecordFilters();
        ReportDateRange.EnsureConsolidatedRange(filters);

        var records = await _records.QueryAsync(filters, viewerUserId, viewerSeesAll, 50_000, ct);
        if (records.Count == 0)
        {
            throw new InvalidOperationException(
                "No hay registros en el rango de fechas seleccionado. " +
                "Ajuste el periodo e intente de nuevo.");
        }

        return await BuildExportFileAsync(format, records, filters, style, ct, columns);
    }

    /// <summary>
    /// Exporta exclusivamente los Ids indicados (deduplicados), con el mismo aislamiento que QueryAsync.
    /// No usa CaptureSessionId, fechas ni otros filtros de expansión.
    /// </summary>
    public async Task<(byte[] Bytes, string FileName, string ContentType)> ExportByIdsAsync(
        string format,
        IReadOnlyCollection<Guid> ids,
        string? viewerUserId,
        bool viewerSeesAll,
        string style = "completo",
        IReadOnlyList<string>? columns = null,
        CancellationToken ct = default)
    {
        var unique = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var id in ids ?? Array.Empty<Guid>())
        {
            if (id == Guid.Empty || !seen.Add(id))
            {
                continue;
            }

            unique.Add(id);
        }

        if (unique.Count == 0)
        {
            throw new ArgumentException("Indique al menos un registro para exportar.");
        }

        var records = await _records.GetByIdsAsync(unique, viewerUserId, viewerSeesAll, ct);
        if (records.Count == 0)
        {
            throw new InvalidOperationException(
                "No hay registros accesibles para exportar con los Ids indicados.");
        }

        // Conservar el orden de solicitud (ya deduplicado).
        var byId = records.ToDictionary(r => r.Id);
        var ordered = unique
            .Where(id => byId.ContainsKey(id))
            .Select(id => byId[id])
            .ToList();

        var filters = new RecordFilters();
        var file = await BuildExportFileAsync(format, ordered, filters, style, ct, columns);

        // Nombres más claros para compartir desde Captura.
        var stamp = Stamp();
        var baseName = ordered.Count == 1
            ? $"regneps_registro_{SanitizeFilePart(ordered[0].Telar)}_{stamp}"
            : $"regneps_seleccion_{ordered.Count}_{stamp}";

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = format.Trim().ToLowerInvariant() switch
            {
                "pdf" => ".pdf",
                "xlsx" or "excel" => ".xlsx",
                _ => ".csv"
            };
        }

        return (file.Bytes, baseName + ext, file.ContentType);
    }

    private static string SanitizeFilePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "registro";
        }

        var chars = value.Trim().Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray();
        var cleaned = new string(chars);
        return string.IsNullOrWhiteSpace(cleaned) ? "registro" : cleaned;
    }

    private async Task<(byte[] Bytes, string FileName, string ContentType)> BuildExportFileAsync(
        string format,
        IReadOnlyList<NepRecord> records,
        RecordFilters filters,
        string style,
        CancellationToken ct,
        IReadOnlyList<string>? columns = null)
    {
        var config = await _alertConfig.GetAsync(ct);
        var ordered = records
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Telar, StringComparer.OrdinalIgnoreCase)
            .ToList();

        format = format.Trim().ToLowerInvariant();
        style = string.IsNullOrWhiteSpace(style) ? "completo" : style.Trim().ToLowerInvariant();
        var selectedColumns = ReportColumnIds.Normalize(columns);
        var filtersDescription = DescribeFilters(filters);
        var styleSuffix = style is "clasico" or "classic" ? "_clasico" : "";
        var periodSuffix = PeriodFileSuffix(filters);

        return format switch
        {
            "csv" => (_export.BuildCsv(ordered, config, style, selectedColumns),
                $"reporte_neps{styleSuffix}{periodSuffix}_{Stamp()}.csv", "text/csv"),
            "xlsx" or "excel" => (_export.BuildExcel(ordered, config, style: style, columns: selectedColumns),
                $"reporte_neps{styleSuffix}{periodSuffix}_{Stamp()}.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            "pdf" => (_export.BuildPdf(ordered, config, filtersDescription: filtersDescription, style: style, columns: selectedColumns),
                $"reporte_neps{styleSuffix}{periodSuffix}_{Stamp()}.pdf", "application/pdf"),
            _ => throw new ArgumentException("Formato no soportado. Use csv, xlsx o pdf.")
        };
    }

    /// <summary>
    /// Copia de las filas del informe en el mismo formato que el export de Firestore,
    /// para poder reabrirlas aunque se vacíe la tabla viva.
    /// </summary>
    private static string BuildSnapshotJson(IReadOnlyList<NepRecord> records)
    {
        var rows = records.Select(r => new
        {
            Id = r.Id.ToString("N"),
            r.Telar,
            r.Neps,
            r.Tela,
            r.LoteTrama,
            CreatedAt = r.CreatedAt.ToUniversalTime().ToString("o"),
            r.Turno,
            r.Operario,
            r.LineaProduccion,
            r.Observacion,
            r.RevisadoPorSupervisor,
            r.AccionCorrectiva,
            r.ResponsableRevision,
            FechaRevision = r.FechaRevision?.ToUniversalTime().ToString("o"),
            r.CreatedByUserId,
            r.CreatedByEmail,
            r.CreatedByRole,
            HistorialAcciones = r.HistorialAcciones.Select(h => new
            {
                Fecha = h.Fecha.ToUniversalTime().ToString("o"),
                h.Responsable,
                Accion = h.Accion
            }).ToList()
        });
        return JsonSerializer.Serialize(rows);
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
            SnapshotJson = BuildSnapshotJson(records),
            SummaryText =
                $"{records.Count} registros del {fromLabel} al {toLabel}, " +
                $"promedio {avg:0.##} neps, {criticos} críticos."
        };
        return await _saved.AddAsync(report, ct);
    }

    /// <summary>Actualiza nombre y filtros de un informe existente. Permite 0 registros vivos (históricos).</summary>
    public async Task<SavedReport> UpdateReportAsync(
        Guid id,
        string name,
        RecordFilters filters,
        string? userId,
        bool viewerSeesAll,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El nombre del informe es obligatorio.");
        }

        _ = await _saved.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("Informe no encontrado.");

        filters ??= new RecordFilters();
        ReportDateRange.EnsureConsolidatedRange(filters);

        var config = await _alertConfig.GetAsync(ct);
        var records = await _records.QueryAsync(filters, userId, viewerSeesAll, 50_000, ct);
        var criticos = records.Count(r => AlertEvaluator.GetLevel(r.Neps, config) == AlertLevel.Critico);
        var avg = records.Count > 0 ? records.Average(r => r.Neps) : 0d;

        var fromLabel = filters.FromUtc?.ToLocalTime().ToString("dd/MM/yyyy") ?? "?";
        var toLabel = filters.ToExclusiveUtc?.ToLocalTime().AddTicks(-1).ToString("dd/MM/yyyy")
                      ?? filters.ToUtc?.ToLocalTime().ToString("dd/MM/yyyy")
                      ?? "?";

        var updated = new SavedReport
        {
            Id = id,
            Name = name.Trim(),
            RecordCount = records.Count,
            FiltersJson = JsonSerializer.Serialize(filters),
            SnapshotJson = records.Count > 0 ? BuildSnapshotJson(records) : null,
            SummaryText = records.Count == 0
                ? $"Sin registros vivos del {fromLabel} al {toLabel} (filtros actualizados)."
                : $"{records.Count} registros del {fromLabel} al {toLabel}, " +
                  $"promedio {avg:0.##} neps, {criticos} críticos."
        };
        await _saved.UpdateAsync(updated, ct);
        return updated;
    }

    /// <summary>Cuenta registros vivos que coinciden con los filtros del informe.</summary>
    public async Task<int> CountMatchingAsync(
        RecordFilters filters,
        string? userId,
        bool viewerSeesAll,
        CancellationToken ct = default)
    {
        var records = await QueryMatchingAsync(filters, userId, viewerSeesAll, 50_000, ct);
        return records.Count;
    }

    /// <summary>Lista registros vivos del periodo (para vista en pantalla).</summary>
    public async Task<IReadOnlyList<NepRecord>> QueryMatchingAsync(
        RecordFilters filters,
        string? userId,
        bool viewerSeesAll,
        int take = 500,
        CancellationToken ct = default)
    {
        filters ??= new RecordFilters();
        ReportDateRange.EnsureConsolidatedRange(filters);
        var records = await _records.QueryAsync(filters, userId, viewerSeesAll, take, ct);
        return records
            .OrderByDescending(r => r.CreatedAt)
            .ThenBy(r => r.Telar, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

        var filters = ResolveSavedFilters(report).Filters;
        var snapshot = await _snapshots.LoadSnapshotRecordsAsync(savedReportId, ct);
        var file = snapshot.Count > 0
            ? await BuildExportFileAsync(format, snapshot, filters, "completo", ct)
            : await ExportAsync(format, filters, viewerUserId, viewerSeesAll, "completo", ct: ct);
        var safeName = SanitizeFileName(report.Name);
        var ext = Path.GetExtension(file.FileName);
        return (file.Bytes, $"{safeName}_{Stamp()}{ext}", file.ContentType);
    }

    /// <summary>
    /// Obtiene filtros del informe. Si el JSON está vacío (p. ej. migrados desde Firestore),
    /// infiere el día desde el nombre (<c>Informe yyyyMMdd_HHmmss</c>) o desde CreatedAt.
    /// </summary>
    public RecordFilters GetSavedFilters(SavedReport report) => ResolveSavedFilters(report).Filters;

    public ResolvedSavedFilters ResolveSavedFilters(SavedReport report)
    {
        var filters = ParseFilters(report.FiltersJson);
        if (filters.FromUtc is not null)
        {
            return new ResolvedSavedFilters(filters, Inferred: false, InferenceSource: null);
        }

        if (TryParseStampDayFromName(report.Name, out var stampDay))
        {
            ReportDateRange.FromLocalCalendarDates(stampDay, stampDay).ApplyTo(filters);
            return new ResolvedSavedFilters(filters, Inferred: true, InferenceSource: "nombre");
        }

        var createdDay = report.CreatedAt.ToLocalTime().Date;
        ReportDateRange.FromLocalCalendarDates(createdDay, createdDay).ApplyTo(filters);
        return new ResolvedSavedFilters(filters, Inferred: true, InferenceSource: "fecha de creación");
    }

    public async Task<SavedReport?> GetSavedAsync(Guid id, CancellationToken ct = default) =>
        await _saved.GetByIdAsync(id, ct);

    private static readonly JsonSerializerOptions FilterJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static RecordFilters ParseFilters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() is "{}" or "null")
            return new RecordFilters();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new RecordFilters();

            // Formato .NET actual
            var filters = JsonSerializer.Deserialize<RecordFilters>(json, FilterJsonOptions) ?? new RecordFilters();

            // Formato Flutter / legacy (camelCase u otras claves)
            if (filters.FromUtc is null)
            {
                filters.FromUtc = ReadDate(root, "fromUtc", "from", "start", "fechaInicio", "startDate", "desde");
            }

            if (filters.ToUtc is null && filters.ToExclusiveUtc is null)
            {
                filters.ToUtc = ReadDate(root, "toUtc", "to", "end", "fechaFin", "endDate", "hasta");
                filters.ToExclusiveUtc = ReadDate(root, "toExclusiveUtc", "toExclusive");
            }

            filters.Telar ??= ReadString(root, "telar", "Telar", "loom");
            filters.Tela ??= ReadString(root, "tela", "Tela", "fabric");

            ReportDateRange.EnsureConsolidatedRange(filters);
            return filters;
        }
        catch (JsonException)
        {
            return new RecordFilters();
        }
    }

    private static bool TryParseStampDayFromName(string? name, out DateTime localDay)
    {
        localDay = default;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // "Informe 20260710_131111" o cualquier yyyyMMdd en el nombre
        var match = System.Text.RegularExpressions.Regex.Match(name, @"(20\d{2})(\d{2})(\d{2})");
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups[1].Value, out var y) ||
            !int.TryParse(match.Groups[2].Value, out var m) ||
            !int.TryParse(match.Groups[3].Value, out var d))
            return false;

        try
        {
            localDay = new DateTime(y, m, d);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DateTime? ReadDate(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var el))
                continue;

            if (el.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(el.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                return dt.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                    : dt.ToUniversalTime();

            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var ms))
            {
                // epoch millis (Firestore a veces)
                if (ms > 1_000_000_000_000)
                    return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                if (ms > 1_000_000_000)
                    return DateTimeOffset.FromUnixTimeSeconds(ms).UtcDateTime;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s.Trim();
            }
        }

        return null;
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

        return ExportAsync(format, filters, viewerUserId, viewerSeesAll, "completo", ct: ct);
    }

    private static string Stamp() => DateTime.Now.ToString("yyyyMMdd_HHmm");
}

/// <summary>Filtros de un informe guardado, con indicación si se infirieron por falta de JSON.</summary>
public sealed record ResolvedSavedFilters(
    RecordFilters Filters,
    bool Inferred,
    string? InferenceSource);

public sealed class ProfessionalReportPreview
{
    public required AnalyticsSummary Summary { get; init; }
    public required AlertConfig Config { get; init; }
    public required IReadOnlyList<NepRecord> Records { get; init; }
    public required IReadOnlyList<string> Conclusions { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
}
