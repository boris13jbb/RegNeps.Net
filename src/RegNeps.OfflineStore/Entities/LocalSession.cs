namespace RegNeps.OfflineStore.Entities;

/// <summary>
/// Snapshot UX de sesión offline. No es credencial de autoridad.
/// No almacena contraseñas ni tokens en claro.
/// </summary>
public sealed class LocalSession
{
    public int Id { get; set; } = 1;

    public string UserId { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string RoleCode { get; set; } = string.Empty;

    /// <summary>Lista de permisos (nombres AppPermission) separados por coma.</summary>
    public string PermissionsCsv { get; set; } = string.Empty;

    public DateTime CapturedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public string? ServerBaseUrl { get; set; }

    /// <summary>
    /// Indica si hay material de auth en almacenamiento seguro del SO (no en esta tabla).
    /// </summary>
    public bool HasSecureAuthMaterial { get; set; }
}
