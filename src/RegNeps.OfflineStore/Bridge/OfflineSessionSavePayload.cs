using System.Text.Json.Serialization;

namespace RegNeps.OfflineStore.Bridge;

/// <summary>
/// Snapshot UX mínimo para LocalSession. Sin contraseña, cookie ni tokens.
/// </summary>
public sealed class OfflineSessionSavePayload
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("roleCode")]
    public string RoleCode { get; set; } = string.Empty;

    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = [];

    [JsonPropertyName("serverBaseUrl")]
    public string? ServerBaseUrl { get; set; }

    /// <summary>Campos desconocidos: se usan para rechazar secretos infiltrados.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }
}
