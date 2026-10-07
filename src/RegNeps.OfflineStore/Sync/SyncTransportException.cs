namespace RegNeps.OfflineStore.Sync;

public enum SyncTransportFailureKind
{
    Network,
    Timeout,
    Unauthorized,
    Forbidden,
    ServerError,
    InvalidResponse
}

/// <summary>Fallo de transporte/autenticación al llamar /api/sync.</summary>
public sealed class SyncTransportException : Exception
{
    public SyncTransportFailureKind Kind { get; }

    public int? HttpStatus { get; }

    public SyncTransportException(
        SyncTransportFailureKind kind,
        string message,
        int? httpStatus = null,
        Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        HttpStatus = httpStatus;
    }
}
