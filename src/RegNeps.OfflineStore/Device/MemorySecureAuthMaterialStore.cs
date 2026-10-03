using System.Collections.Concurrent;
using RegNeps.OfflineStore.Abstractions;

namespace RegNeps.OfflineStore.Device;

/// <summary>Store en memoria para tests. No usar en producción Android.</summary>
public sealed class MemorySecureAuthMaterialStore : ISecureAuthMaterialStore
{
    private readonly ConcurrentDictionary<string, string> _items = new(StringComparer.Ordinal);

    public Task SetAuthMaterialAsync(string userId, string material, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        // Rechazar claves que parezcan contraseñas enviadas por error de API.
        if (string.Equals(userId, "password", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("No se permiten contraseñas en el almacén seguro.", nameof(userId));
        }

        SecureAuthMaterialGuard.EnsureNotAuthCookieOrBearer(material);
        _items[userId] = material ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task<string?> GetAuthMaterialAsync(string userId, CancellationToken ct = default)
    {
        _items.TryGetValue(userId, out var value);
        return Task.FromResult<string?>(value);
    }

    public Task ClearAsync(string userId, CancellationToken ct = default)
    {
        _items.TryRemove(userId, out _);
        return Task.CompletedTask;
    }
}
