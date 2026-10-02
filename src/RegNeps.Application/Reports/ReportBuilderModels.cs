namespace RegNeps.Application.Reports;

public sealed class ReportBuilderOptions
{
    public ReportBuilderDimension Primary { get; init; } = ReportBuilderDimension.Telar;

    public ReportBuilderDimension? Secondary { get; init; }

    public int TelarRankingTake { get; init; } = 10;
}

public sealed class ReportBuilderGroupRow
{
    public required string PrimaryKey { get; init; }
    public string? SecondaryKey { get; init; }
    public required string DisplayLabel { get; init; }
    public int Count { get; init; }
    public double SumNeps { get; init; }
    public double AverageNeps { get; init; }
    public double TotalMts { get; init; }
    public double MinNeps { get; init; }
    public double MaxNeps { get; init; }
    public int NormalCount { get; init; }
    public int WarningCount { get; init; }
    public int CriticalCount { get; init; }
    public double NormalPercent { get; init; }
    public double WarningPercent { get; init; }
    public double CriticalPercent { get; init; }
    public double QualityIndex { get; init; }
    /// <summary>Promedio neps/m² (Neps / TestLengthM por registro, agregado como promedio de neps / TestLengthM).</summary>
    public double NepsPerM2 { get; init; }
}

public sealed class ReportBuilderTelarRank
{
    public required string Telar { get; init; }
    public double AverageNeps { get; init; }
    public double NepsPerM2 { get; init; }
    public int RecordCount { get; init; }
    public double TotalMts { get; init; }
}

public sealed class ReportBuilderResult
{
    public ReportBuilderDimension PrimaryDimension { get; init; }
    public ReportBuilderDimension? SecondaryDimension { get; init; }
    public int TotalRecords { get; init; }
    public double SumNeps { get; init; }
    public double AverageNeps { get; init; }
    public double TotalMts { get; init; }
    public double MinNeps { get; init; }
    public double MaxNeps { get; init; }
    public int NormalCount { get; init; }
    public int WarningCount { get; init; }
    public int CriticalCount { get; init; }
    public double NormalPercent { get; init; }
    public double WarningPercent { get; init; }
    public double CriticalPercent { get; init; }
    public double QualityIndex { get; init; }
    public IReadOnlyList<ReportBuilderGroupRow> Groups { get; init; } = [];
    public IReadOnlyList<ReportBuilderTelarRank> BestTelars { get; init; } = [];
    public IReadOnlyList<ReportBuilderTelarRank> WorstTelars { get; init; } = [];
}
