namespace RegNeps.Domain.Sync;

/// <summary>Constantes del protocolo de sincronización (servidor).</summary>
public static class SyncConstants
{
    public const int ProtocolVersion = 1;

    public const string EntityNepRecord = "NepRecord";
    /// <summary>Catálogo de referencia (Tela/Lote) — Pull server→client únicamente.</summary>
    public const string EntityCatalogItem = "CatalogItem";

    public const string ChangeRecordUpserted = "RecordUpserted";
    public const string ChangeRecordDeleted = "RecordDeleted";

    /// <summary>Alta o actualización de elemento de catálogo (incluye IsActive).</summary>
    public const string ChangeCatalogUpserted = "CatalogUpserted";

    /// <summary>
    /// Catálogo eliminado o retirado en servidor.
    /// Cliente marca IsActive=false; no resurrección vía Push.
    /// </summary>
    public const string ChangeCatalogDeleted = "CatalogDeleted";

    public const string CatalogKindFabric = "Fabric";
    public const string CatalogKindLote = "Lote";

    /// <summary>OwnerUserId sintético: catálogos son visibles a todo usuario autenticado en Pull.</summary>
    public const string CatalogOwnerUserId = "catalog";

    public const string OperationCreateRecord = "CreateRecord";
    public const string OperationUpdateRecord = "UpdateRecord";
    public const string OperationApplyCorrective = "ApplyCorrective";
    public const string OperationDeleteRecord = "DeleteRecord";

    public const int DefaultPageSize = 100;
    public const int MinPageSize = 1;
    public const int MaxPageSize = 500;

    public const int MaxDeviceIdLength = 64;
    public const int MaxClientOperationIdLength = 64;
    public const int MaxCaptureSessionIdLength = 64;
}
