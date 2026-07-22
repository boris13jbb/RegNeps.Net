using RegNeps.Application.Abstractions;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Filters;
using RegNeps.Domain.Services;

namespace RegNeps.Application.Analytics;

public sealed class AnalyticsService
{
    private readonly INepRecordRepository _records;
    private readonly IAlertConfigRepository _alertConfig;

    public AnalyticsService(INepRecordRepository records, IAlertConfigRepository alertConfig)
    {
        _records = records;
        _alertConfig = alertConfig;
    }

    public async Task<AnalyticsSummary> BuildAsync(
        DateTime? fromUtc,
        DateTime? toUtc,
        string? viewerUserId,
        bool viewerSeesAll,
        string? telar = null,
        string? tela = null,
        CancellationToken ct = default)
    {
        var config = await _alertConfig.GetAsync(ct);
        var filters = new RecordFilters
        {
            Telar = string.IsNullOrWhiteSpace(telar) ? null : telar.Trim(),
            Tela = string.IsNullOrWhiteSpace(tela) ? null : tela.Trim()
        };
        if (fromUtc is not null && toUtc is not null)
        {
            ReportDateRange.FromLocalCalendarDates(
                fromUtc.Value.ToLocalTime().Date,
                toUtc.Value.ToLocalTime().Date).ApplyTo(filters);
        }
        else
        {
            filters.FromUtc = fromUtc;
            filters.ToUtc = toUtc;
        }
        var records = await _records.QueryAsync(filters, viewerUserId, viewerSeesAll, 50_000, ct);

        var total = records.Count;
        var avg = total == 0 ? 0 : records.Average(r => r.Neps);
        var criticos = records.Count(r => AlertEvaluator.GetLevel(r.Neps, config) == AlertLevel.Critico);
        var advertencias = records.Count(r => AlertEvaluator.GetLevel(r.Neps, config) == AlertLevel.Advertencia);

        var byDay = records
            .GroupBy(r => r.CreatedAt.ToLocalTime().Date)
            .OrderBy(g => g.Key)
            .Select(g => new TimeSeriesPoint(g.Key, g.Average(x => x.Neps), g.Count()))
            .ToList();

        var byTelar = records
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Telar) ? "(sin telar)" : r.Telar)
            .Select(g => new GroupSummary(
                g.Key,
                g.Sum(x => x.Neps),
                g.Sum(x => x.MtsCalculados),
                g.Count(),
                g.Average(x => x.Neps),
                g.Count(x => AlertEvaluator.GetLevel(x.Neps, config) == AlertLevel.Critico),
                g.Count(x => AlertEvaluator.GetLevel(x.Neps, config) == AlertLevel.Advertencia)))
            .OrderByDescending(x => x.AverageNeps)
            .ToList();

        var byTela = records
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Tela) ? "(sin tela)" : r.Tela)
            .Select(g => new GroupSummary(
                g.Key,
                g.Sum(x => x.Neps),
                g.Sum(x => x.MtsCalculados),
                g.Count(),
                g.Average(x => x.Neps),
                g.Count(x => AlertEvaluator.GetLevel(x.Neps, config) == AlertLevel.Critico),
                g.Count(x => AlertEvaluator.GetLevel(x.Neps, config) == AlertLevel.Advertencia)))
            .OrderByDescending(x => x.RecordCount)
            .ToList();

        return new AnalyticsSummary
        {
            TotalRecords = total,
            AverageNeps = avg,
            TotalMts = records.Sum(r => r.MtsCalculados),
            CriticalCount = criticos,
            WarningCount = advertencias,
            QualityIndex = total == 0 ? 100 : 100 - (criticos * 100.0 / total),
            ByDay = byDay,
            ByTelar = byTelar,
            ByTela = byTela,
            WorstTelars = byTelar.Take(5).ToList(),
            BestTelars = byTelar.OrderBy(x => x.AverageNeps).Take(5).ToList()
        };
    }
}

public sealed class AnalyticsSummary
{
    public int TotalRecords { get; init; }
    public double AverageNeps { get; init; }
    public double TotalMts { get; init; }
    public int CriticalCount { get; init; }
    public int WarningCount { get; init; }
    public double QualityIndex { get; init; }
    public IReadOnlyList<TimeSeriesPoint> ByDay { get; init; } = [];
    public IReadOnlyList<GroupSummary> ByTelar { get; init; } = [];
    public IReadOnlyList<GroupSummary> ByTela { get; init; } = [];
    public IReadOnlyList<GroupSummary> WorstTelars { get; init; } = [];
    public IReadOnlyList<GroupSummary> BestTelars { get; init; } = [];
}

public sealed record TimeSeriesPoint(DateTime Date, double AverageNeps, int Count);

public sealed record GroupSummary(
    string Key,
    double TotalNeps,
    double TotalMts,
    int RecordCount,
    double AverageNeps,
    int CriticalCount,
    int WarningCount);
