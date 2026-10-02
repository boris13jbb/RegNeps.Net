using System.Globalization;
using RegNeps.Application.Abstractions;
using RegNeps.Application.Analytics;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Filters;
using RegNeps.Domain.Services;

namespace RegNeps.Application.Reports;

/// <summary>
/// Agrupa registros filtrados en 1–2 dimensiones y calcula KPIs por grupo (testeable sin UI).
/// </summary>
public sealed class ReportBuilderService
{
    private readonly INepRecordRepository _records;
    private readonly IAlertConfigRepository _alertConfig;

    public ReportBuilderService(INepRecordRepository records, IAlertConfigRepository alertConfig)
    {
        _records = records;
        _alertConfig = alertConfig;
    }

    public async Task<ReportBuilderResult> BuildFromFiltersAsync(
        RecordFilters filters,
        string? viewerUserId,
        bool viewerSeesAll,
        ReportBuilderOptions options,
        CancellationToken ct = default)
    {
        filters ??= new RecordFilters();
        ReportDateRange.EnsureConsolidatedRange(filters);
        var config = await _alertConfig.GetAsync(ct);
        var records = await _records.QueryAsync(filters, viewerUserId, viewerSeesAll, 50_000, ct);
        return Build(records, config, options);
    }

    public static ReportBuilderResult Build(
        IReadOnlyList<NepRecord> records,
        AlertConfig config,
        ReportBuilderOptions options)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(config);
        options ??= new ReportBuilderOptions();

        if (options.Secondary is { } sec && sec == options.Primary)
        {
            throw new ArgumentException("La dimensión secundaria no puede ser igual a la primaria.");
        }

        var list = records as IList<NepRecord> ?? records.ToList();
        var total = list.Count;

        var overall = ComputeOverall(list, config, total);
        var groups = BuildGroups(list, config, options);
        var (best, worst) = BuildTelarRankings(list, config, options.TelarRankingTake);

        return new ReportBuilderResult
        {
            PrimaryDimension = options.Primary,
            SecondaryDimension = options.Secondary,
            TotalRecords = total,
            SumNeps = overall.SumNeps,
            AverageNeps = overall.AverageNeps,
            TotalMts = overall.TotalMts,
            MinNeps = overall.MinNeps,
            MaxNeps = overall.MaxNeps,
            NormalCount = overall.NormalCount,
            WarningCount = overall.WarningCount,
            CriticalCount = overall.CriticalCount,
            NormalPercent = overall.NormalPercent,
            WarningPercent = overall.WarningPercent,
            CriticalPercent = overall.CriticalPercent,
            QualityIndex = overall.QualityIndex,
            Groups = groups,
            BestTelars = best,
            WorstTelars = worst
        };
    }

    private static (double SumNeps, double AverageNeps, double TotalMts, double MinNeps, double MaxNeps,
        int NormalCount, int WarningCount, int CriticalCount,
        double NormalPercent, double WarningPercent, double CriticalPercent, double QualityIndex)
        ComputeOverall(IList<NepRecord> list, AlertConfig config, int total)
    {
        if (total == 0)
        {
            return (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100);
        }

        var sumNeps = list.Sum(r => r.Neps);
        var avg = sumNeps / total;
        var normal = 0;
        var warning = 0;
        var critical = 0;
        foreach (var r in list)
        {
            switch (AlertEvaluator.GetLevel(r.Neps, config))
            {
                case AlertLevel.Critico:
                    critical++;
                    break;
                case AlertLevel.Advertencia:
                    warning++;
                    break;
                default:
                    normal++;
                    break;
            }
        }

        var quality = 100 - critical * 100.0 / total;
        return (
            sumNeps,
            avg,
            list.Sum(r => r.MtsCalculados),
            list.Min(r => r.Neps),
            list.Max(r => r.Neps),
            normal,
            warning,
            critical,
            normal * 100.0 / total,
            warning * 100.0 / total,
            critical * 100.0 / total,
            quality);
    }

    private static List<ReportBuilderGroupRow> BuildGroups(
        IList<NepRecord> list,
        AlertConfig config,
        ReportBuilderOptions options)
    {
        if (list.Count == 0)
        {
            return [];
        }

        var grouped = list.GroupBy(r => (
            Primary: DimensionKey(r, options.Primary),
            Secondary: options.Secondary is { } s ? DimensionKey(r, s) : null));

        var rows = grouped
            .Select(g => ToGroupRow(g.Key.Primary, g.Key.Secondary, g, config))
            .ToList();

        return SortGroups(rows, options.Primary, options.Secondary);
    }

    private static ReportBuilderGroupRow ToGroupRow(
        string primary,
        string? secondary,
        IEnumerable<NepRecord> group,
        AlertConfig config)
    {
        var items = group as IList<NepRecord> ?? group.ToList();
        var count = items.Count;
        var sumNeps = items.Sum(x => x.Neps);
        var avg = count == 0 ? 0 : sumNeps / count;
        var normal = 0;
        var warning = 0;
        var critical = 0;
        foreach (var r in items)
        {
            switch (AlertEvaluator.GetLevel(r.Neps, config))
            {
                case AlertLevel.Critico:
                    critical++;
                    break;
                case AlertLevel.Advertencia:
                    warning++;
                    break;
                default:
                    normal++;
                    break;
            }
        }

        var display = secondary is null ? primary : $"{primary} · {secondary}";
        var quality = count == 0 ? 100 : 100 - critical * 100.0 / count;

        return new ReportBuilderGroupRow
        {
            PrimaryKey = primary,
            SecondaryKey = secondary,
            DisplayLabel = display,
            Count = count,
            SumNeps = sumNeps,
            AverageNeps = avg,
            TotalMts = items.Sum(x => x.MtsCalculados),
            MinNeps = count == 0 ? 0 : items.Min(x => x.Neps),
            MaxNeps = count == 0 ? 0 : items.Max(x => x.Neps),
            NormalCount = normal,
            WarningCount = warning,
            CriticalCount = critical,
            NormalPercent = count == 0 ? 0 : normal * 100.0 / count,
            WarningPercent = count == 0 ? 0 : warning * 100.0 / count,
            CriticalPercent = count == 0 ? 0 : critical * 100.0 / count,
            QualityIndex = quality,
            NepsPerM2 = avg / NepsConstants.TestLengthM
        };
    }

    private static (IReadOnlyList<ReportBuilderTelarRank> Best, IReadOnlyList<ReportBuilderTelarRank> Worst)
        BuildTelarRankings(IList<NepRecord> list, AlertConfig config, int take)
    {
        _ = config;
        if (list.Count == 0 || take <= 0)
        {
            return ([], []);
        }

        var byTelar = list
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Telar) ? "(sin telar)" : r.Telar.Trim())
            .Select(g =>
            {
                var cnt = g.Count();
                var avg = g.Average(x => x.Neps);
                return new ReportBuilderTelarRank
                {
                    Telar = g.Key,
                    AverageNeps = avg,
                    NepsPerM2 = avg / NepsConstants.TestLengthM,
                    RecordCount = cnt,
                    TotalMts = g.Sum(x => x.MtsCalculados)
                };
            })
            .Where(t => !string.Equals(t.Telar, "(sin telar)", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var best = byTelar
            .OrderBy(t => t.NepsPerM2)
            .ThenBy(t => t.Telar, TelarKeyComparer.Instance)
            .Take(take)
            .ToList();

        var worst = byTelar
            .OrderByDescending(t => t.NepsPerM2)
            .ThenBy(t => t.Telar, TelarKeyComparer.Instance)
            .Take(take)
            .ToList();

        return (best, worst);
    }

    private static string DimensionKey(NepRecord r, ReportBuilderDimension dim) => dim switch
    {
        ReportBuilderDimension.Telar => string.IsNullOrWhiteSpace(r.Telar) ? "(sin telar)" : r.Telar.Trim(),
        ReportBuilderDimension.Tela => string.IsNullOrWhiteSpace(r.Tela) ? "(sin tela)" : r.Tela.Trim(),
        ReportBuilderDimension.Lote => string.IsNullOrWhiteSpace(r.LoteTrama) ? "(sin lote)" : r.LoteTrama.Trim(),
        ReportBuilderDimension.Turno => string.IsNullOrWhiteSpace(r.Turno) ? "(sin turno)" : r.Turno.Trim(),
        ReportBuilderDimension.Operario => string.IsNullOrWhiteSpace(r.Operario) ? "(sin operario)" : r.Operario.Trim(),
        ReportBuilderDimension.Day => r.CreatedAt.ToLocalTime().Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ReportBuilderDimension.Week => WeekLabel(r.CreatedAt.ToLocalTime()),
        ReportBuilderDimension.Month => r.CreatedAt.ToLocalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture),
        _ => "?"
    };

    private static string WeekLabel(DateTime local)
    {
        var start = StartOfWeek(local.Date);
        return $"Sem {ISOWeek.GetWeekOfYear(local)}/{local:yy} ({start:dd/MM})";
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        var diff = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-diff);
    }

    private static List<ReportBuilderGroupRow> SortGroups(
        List<ReportBuilderGroupRow> rows,
        ReportBuilderDimension primary,
        ReportBuilderDimension? secondary)
    {
        if (rows.Count <= 1)
        {
            return rows;
        }

        IOrderedEnumerable<ReportBuilderGroupRow> ordered = primary switch
        {
            ReportBuilderDimension.Telar => rows.OrderBy(r => r.PrimaryKey, TelarKeyComparer.Instance),
            ReportBuilderDimension.Day or ReportBuilderDimension.Month =>
                rows.OrderBy(r => r.PrimaryKey, StringComparer.Ordinal),
            ReportBuilderDimension.Week =>
                rows.OrderBy(r => r.PrimaryKey, StringComparer.Ordinal),
            _ => rows.OrderByDescending(r => r.Count).ThenBy(r => r.PrimaryKey, StringComparer.OrdinalIgnoreCase)
        };

        if (secondary is not null)
        {
            ordered = secondary switch
            {
                ReportBuilderDimension.Telar => ordered.ThenBy(
                    r => r.SecondaryKey ?? string.Empty,
                    (IComparer<string>)TelarKeyComparer.Instance),
                ReportBuilderDimension.Day or ReportBuilderDimension.Month or ReportBuilderDimension.Week =>
                    ordered.ThenBy(r => r.SecondaryKey ?? string.Empty, StringComparer.Ordinal),
                _ => ordered.ThenBy(r => r.SecondaryKey ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            };
        }

        return ordered.ToList();
    }

    public static string DimensionDisplayName(ReportBuilderDimension dim) => dim switch
    {
        ReportBuilderDimension.Telar => "Telar",
        ReportBuilderDimension.Tela => "Tela",
        ReportBuilderDimension.Lote => "Lote trama",
        ReportBuilderDimension.Turno => "Turno",
        ReportBuilderDimension.Operario => "Operario",
        ReportBuilderDimension.Day => "Día",
        ReportBuilderDimension.Week => "Semana",
        ReportBuilderDimension.Month => "Mes",
        _ => dim.ToString()
    };
}
