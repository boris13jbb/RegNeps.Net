namespace RegNeps.OfflineStore.Bridge;

public static class OfflineBridgeErrorCodes
{
    public const string InvalidEnvelope = "INVALID_ENVELOPE";
    public const string IncompatibleProtocol = "INCOMPATIBLE_PROTOCOL";
    public const string UnknownAction = "UNKNOWN_ACTION";
    public const string UnauthorizedAction = "UNAUTHORIZED_ACTION";
    public const string InvalidPayload = "INVALID_PAYLOAD";
    public const string ForbiddenField = "FORBIDDEN_FIELD";
    public const string HandlerError = "HANDLER_ERROR";
    public const string NotAvailable = "NOT_AVAILABLE";
}
