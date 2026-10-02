using System.Text.Json.Serialization;

namespace RegNeps.OfflineStore.Bridge;

public sealed class OfflineSessionViewDto
{
    [JsonPropertyName("hasSession")]
    public bool HasSession { get; set; }

    [JsonPropertyName("isValid")]
    public bool IsValid { get; set; }

    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("roleCode")]
    public string? RoleCode { get; set; }

    [JsonPropertyName("permissions")]
    public IReadOnlyList<string> Permissions { get; set; } = Array.Empty<string>();

    [JsonPropertyName("expiresAtUtc")]
    public DateTime? ExpiresAtUtc { get; set; }

    [JsonPropertyName("serverBaseUrl")]
    public string? ServerBaseUrl { get; set; }

    [JsonPropertyName("hasSecureAuthMaterial")]
    public bool HasSecureAuthMaterial { get; set; }
}
