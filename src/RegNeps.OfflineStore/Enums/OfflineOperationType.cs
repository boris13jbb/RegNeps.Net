namespace RegNeps.OfflineStore.Enums;

/// <summary>
/// Tipos de operación Outbox. v1 solo implementa <see cref="CreateRecord"/>;
/// el resto queda reservado para fases posteriores.
/// </summary>
public enum OfflineOperationType
{
    CreateRecord = 0,
    UpdateRecord = 1,
    ApplyCorrective = 2,
    DeleteRecord = 3
}
