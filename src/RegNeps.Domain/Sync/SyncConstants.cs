namespace RegNeps.Domain.Sync;

/// <summary>Constantes del protocolo de sincronización (servidor).</summary>
public static class SyncConstants
{
    public const int ProtocolVersion = 1;

    public const string EntityNepRecord = "NepRecord";
    /// <summary>Reservado para fases posteriores (catálogos).</summary>
    public const string EntityCatalogItem = "CatalogItem";

    public const string ChangeRecordUpserted = "RecordUpserted";
    public const string ChangeRecordDeleted = "RecordDeleted";

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
