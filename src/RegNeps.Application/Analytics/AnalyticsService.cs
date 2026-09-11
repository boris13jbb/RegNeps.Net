using System.Globalization;
using RegNeps.Application.Abstractions;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Filters;
using RegNeps.Domain.Services;

namespace RegNeps.Application.Analytics;

public enum ChartTimeGrouping
{
    Day,
    Week,
    Month,
    Year
}

public sealed class AnalyticsService
{
    private readonly INepRecordRepository _records;
    private readonly IAlertConfigRepository _alertConfig;

    public AnalyticsService(INepRecordRepository records, IAlertConfigRepository alertConfig)
    {
        _records = records;
        _alertConfig = alertConfig;
    }

    /// <summary>Compatibilidad: periodo + telar/tela opcionales.</summary>
    public Task<AnalyticsSummary> BuildAsync(
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
        ApplyDateBounds(filters, fromUtc, toUtc);
        return BuildAsync(filters, viewerUserId, viewerSeesAll, ChartTimeGrouping.Day, ct);
    }

    public async Task<AnalyticsSummary> BuildAsync(
        RecordFilters filters,
        string? viewerUserId,
        bool viewerSeesAll,
        ChartTimeGrouping grouping = ChartTimeGrouping.Day,
        CancellationToken ct = default)
    {
        filters ??= new RecordFilters();
        ReportDateRange.EnsureConsolidatedRange(filters);

        var config = await _alertConfig.GetAsync(ct);
        var records = await _records.QueryAsync(filters, viewerUserId, viewerSeesAll, 50_000, ct);

        var total = records.Count;
        var avg = total == 0 ? 0 : records.Average(r => r.Neps);
        var criticos = records.Count(r => AlertEvaluator.GetLevel(r.Neps, config) == AlertLevel.Critico);
        var advertencias = records.Count(r => AlertEvaluator.GetLevel(r.Neps, config) == AlertLevel.Advertencia);
        var normales = Math.Max(0, total - criticos - advertencias);

        var byPeriod = BuildTimeSeries(records, grouping);

        var byTelarGroups = records
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Telar) ? "(sin telar)" : r.Telar.Trim())
            .Select(g => ToGroup(g.Key, g, config))
            .ToList();

        // Tabla "Por telar": orden natural del código (003, 004, 103…).
        var byTelar = byTelarGroups
            .OrderBy(x => x.Key, TelarKeyComparer.Instance)
            .ToList();

        var byTela = records
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Tela) ? "(sin tela)" : r.Tela)
            .Select(g => ToGroup(g.Key, g, config))
            .OrderByDescending(x => x.RecordCount)
            .ToList();

        var byLote = records
            .GroupBy(r => string.IsNullOrWhiteSpace(r.LoteTrama) ? "(sin lote)" : r.LoteTrama)
            .Select(g => ToGroup(g.Key, g, config))
            .OrderByDescending(x => x.TotalNeps)
            .ToList();

        var byAverageDesc = byTelarGroups
            .OrderByDescending(x => x.AverageNeps)
            .ThenBy(x => x.Key, TelarKeyComparer.Instance)
            .ToList();

        return new AnalyticsSummary
        {
            TotalRecords = total,
            AverageNeps = avg,
            MinNeps = total == 0 ? 0 : records.Min(r => r.Neps),
            MaxNeps = total == 0 ? 0 : records.Max(r => r.Neps),
            TotalNeps = records.Sum(r => r.Neps),
            TotalMts = records.Sum(r => r.MtsCalculados),
            CriticalCount = criticos,
            WarningCount = advertencias,
            NormalCount = normales,
            QualityIndex = total == 0 ? 100 : 100 - (criticos * 100.0 / total),
            TimeGrouping = grouping,
            ByDay = byPeriod,
            ByTelar = byTelar,
            ByTela = byTela,
            ByLote = byLote,
            WorstTelars = byAverageDesc.Take(5).ToList(),
            BestTelars = byTelarGroups.OrderBy(x => x.AverageNeps).ThenBy(x => x.Key, TelarKeyComparer.Instance).Take(5).ToList()
        };
    }

    private static void ApplyDateBounds(RecordFilters filters, DateTime? fromUtc, DateTime? toUtc)
    {
        if (fromUtc is not null && toUtc is not null)
        {
            ReportDateRange.FromLocalCalendarDates(
                fromUtc.Value.ToLocalTime().Date,
                toUtc.Value.ToLocalTime().Date).ApplyTo(filters);
            return;
        }

        filters.FromUtc = fromUtc;
        filters.ToUtc = toUtc;
    }

    private static GroupSummary ToGroup(string key, IEnumerable<NepRecord> group, AlertConfig config)
    {
        var list = group as IList<NepRecord> ?? group.ToList();
        return new GroupSummary(
            key,
            list.Sum(x => x.Neps),
            list.Sum(x => x.MtsCalculados),
            list.Count,
            list.Average(x => x.Neps),
            list.Count(x => AlertEvaluator.GetLevel(x.Neps, config) == AlertLevel.Critico),
            list.Count(x => AlertEvaluator.GetLevel(x.Neps, config) == AlertLevel.Advertencia));
    }

    private static List<TimeSeriesPoint> BuildTimeSeries(
        IReadOnlyList<NepRecord> records,
        ChartTimeGrouping grouping)
    {
        return records
            .GroupBy(r => PeriodKey(r.CreatedAt.ToLocalTime(), grouping))
            .OrderBy(g => g.Key.Start)
            .Select(g => new TimeSeriesPoint(
                g.Key.Start,
                g.Average(x => x.Neps),
                g.Count(),
                g.Key.Label))
            .ToList();
    }

    private static (DateTime Start, string Label) PeriodKey(DateTime local, ChartTimeGrouping grouping)
    {
        return grouping switch
        {
            ChartTimeGrouping.Week =>
            (
                StartOfWeek(local.Date),
                $"Sem {ISOWeek.GetWeekOfYear(local)}/{local:yy}"
            ),
            ChartTimeGrouping.Month =>
            (
                new DateTime(local.Year, local.Month, 1),
                local.ToString("MMM yyyy", CultureInfo.GetCultureInfo("es-ES"))
            ),
            ChartTimeGrouping.Year =>
            (
                new DateTime(local.Year, 1, 1),
                local.Year.ToString(CultureInfo.InvariantCulture)
            ),
            _ => (local.Date, local.ToString("dd/MM"))
        };
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        // Semana ISO: lunes como inicio.
        var diff = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-diff);
    }
}

/// <summary>
/// Orden natural de códigos de telar: 003, 004, 103, 104 (no lexicográfico 103, 104, 003).
/// </summary>
public sealed class TelarKeyComparer : IComparer<string>
{
    public static TelarKeyComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        if (int.TryParse(x, out var nx) && int.TryParse(y, out var ny))
        {
            return nx.CompareTo(ny);
        }

        return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class AnalyticsSummary
{
    public int TotalRecords { get; init; }
    public double AverageNeps { get; init; }
    public double MinNeps { get; init; }
    public double MaxNeps { get; init; }
    public double TotalNeps { get; init; }
    public double TotalMts { get; init; }
    public int CriticalCount { get; init; }
    public int WarningCount { get; init; }
    public int NormalCount { get; init; }
    public double QualityIndex { get; init; }
    public ChartTimeGrouping TimeGrouping { get; init; } = ChartTimeGrouping.Day;
    public IReadOnlyList<TimeSeriesPoint> ByDay { get; init; } = [];
    /// <summary>Agrupación por telar ordenada por código (orden natural).</summary>
    public IReadOnlyList<GroupSummary> ByTelar { get; init; } = [];
    public IReadOnlyList<GroupSummary> ByTela { get; init; } = [];
    public IReadOnlyList<GroupSummary> ByLote { get; init; } = [];
    public IReadOnlyList<GroupSummary> WorstTelars { get; init; } = [];
    public IReadOnlyList<GroupSummary> BestTelars { get; init; } = [];
}

public sealed record TimeSeriesPoint(DateTime Date, double AverageNeps, int Count, string? Label = null);

public sealed record GroupSummary(
    string Key,
    double TotalNeps,
    double TotalMts,
    int RecordCount,
    double AverageNeps,
    int CriticalCount,
    int WarningCount);
