namespace RegNeps.Application.Records;

/// <summary>Resultado de borrado en lote de registros.</summary>
public sealed class BatchDeleteResult
{
    public int Deleted { get; init; }
    public int NotFoundOrFailed { get; init; }
    public IReadOnlyList<Guid> DeletedIds { get; init; } = [];
    public IReadOnlyList<Guid> FailedIds { get; init; } = [];

    public string SummaryText =>
        NotFoundOrFailed == 0
            ? $"{Deleted} eliminados."
            : $"{Deleted} eliminados, {NotFoundOrFailed} no encontrados/fallidos.";
}
