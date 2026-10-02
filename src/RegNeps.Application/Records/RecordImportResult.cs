namespace RegNeps.Application.Records;

public sealed class RecordImportRowResult
{
    public int RowNumber { get; init; }
    public bool Success { get; init; }
    public string? Reason { get; init; }
}

public sealed class RecordImportResult
{
    public int Imported { get; init; }
    public IReadOnlyList<RecordImportRowResult> Rows { get; init; } = [];

    public IReadOnlyList<string> Errors =>
        Rows.Where(r => !r.Success)
            .Select(r => string.IsNullOrWhiteSpace(r.Reason)
                ? $"Fila {r.RowNumber}: error"
                : $"Fila {r.RowNumber}: {r.Reason}")
            .ToList();
}
