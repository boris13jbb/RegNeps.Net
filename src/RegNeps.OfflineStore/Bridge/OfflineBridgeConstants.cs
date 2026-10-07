namespace RegNeps.OfflineStore.Bridge;

public static class OfflineBridgeConstants
{
    /// <summary>Versión del protocolo de mensajes (independiente del payload Outbox).</summary>
    public const int ProtocolVersion = 1;

    public const string Scheme = "regneps-bridge";
    public const string InvokeHost = "invoke";

    public const string MessageTypeRequest = "request";
    public const string MessageTypeResponse = "response";

    /// <summary>Límite de URL completa del deep-link (solo JSON pequeño; sin secretos).</summary>
    public const int MaxUrlLength = 8000;

    public static readonly string[] ForbiddenPayloadFieldNames =
    [
        "password",
        "passwd",
        "pwd",
        "cookie",
        "cookies",
        "token",
        "accessToken",
        "refreshToken",
        "authToken",
        "authorization",
        "secureAuthMaterial",
        "authMaterial"
    ];
}
