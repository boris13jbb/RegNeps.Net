namespace RegNeps.Application.Common;

/// <summary>Límites defensivos de paginación UI de registros (FASE 2F).</summary>
public static class RecordPaging
{
    public const int DefaultPageSize = 50;
    public const int MinPageSize = 1;
    /// <summary>Máximo por página UI. El Take(500)/50k de <c>QueryAsync</c> es otro contrato (export/captura).</summary>
    public const int MaxPageSize = 100;

    public static int NormalizePageSize(int pageSize) =>
        Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, MinPageSize, MaxPageSize);

    public static int NormalizePageNumber(int pageNumber) =>
        pageNumber < 1 ? 1 : pageNumber;
}
