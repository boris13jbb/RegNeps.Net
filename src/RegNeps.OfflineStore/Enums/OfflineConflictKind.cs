namespace RegNeps.OfflineStore.Enums;

/// <summary>
/// Clasificación derivada del Conflict (FASE 2D.7).
/// No se persiste como columna si OperationType + error + snapshot bastan.
/// </summary>
public enum OfflineConflictKind
{
    Unknown = 0,
    UpdateUpdate = 1,
    UpdateDelete = 2,
    DeleteUpdate = 3,
    DeleteDelete = 4
}
