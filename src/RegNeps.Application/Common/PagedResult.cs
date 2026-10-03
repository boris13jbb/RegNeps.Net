namespace RegNeps.Application.Common;

/// <summary>
/// Resultado de consulta paginada (FASE 2F). El servidor aplica filtros/autorización
/// antes de materializar <see cref="Items"/>.
/// </summary>
public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

    public int PageNumber { get; init; } = 1;

    public int PageSize { get; init; } = RecordPaging.DefaultPageSize;

    /// <summary>Total de filas que cumplen filtros/autorización (COUNT), no solo la página.</summary>
    public int TotalCount { get; init; }

    public int TotalPages =>
        TotalCount <= 0
            ? 1
            : (int)Math.Ceiling(TotalCount / (double)Math.Max(1, PageSize));

    public bool HasPreviousPage => PageNumber > 1;

    public bool HasNextPage => PageNumber < TotalPages;
}
