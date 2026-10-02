namespace RegNeps.OfflineStore.Abstractions;

/// <summary>
/// Material de autenticación en almacenamiento seguro del SO (p. ej. Android Keystore / SecureStorage).
/// Nunca escribe en SQLite. No almacena contraseñas.
/// </summary>
public interface ISecureAuthMaterialStore
{
    Task SetAuthMaterialAsync(string userId, string material, CancellationToken ct = default);

    Task<string?> GetAuthMaterialAsync(string userId, CancellationToken ct = default);

    Task ClearAsync(string userId, CancellationToken ct = default);
}
