using Microsoft.EntityFrameworkCore;
using RegNeps.OfflineStore.Abstractions;
using RegNeps.OfflineStore.Entities;

namespace RegNeps.OfflineStore.Services;

/// <summary>
/// Gestiona el snapshot UX de sesión offline (expirable). No autoriza en servidor.
/// </summary>
public sealed class OfflineSessionService
{
    private readonly LocalSyncDbContext _db;
    private readonly ISecureAuthMaterialStore _secureStore;

    public OfflineSessionService(LocalSyncDbContext db, ISecureAuthMaterialStore secureStore)
    {
        _db = db;
        _secureStore = secureStore;
    }

    public async Task<LocalSession?> GetValidSessionAsync(CancellationToken ct = default)
    {
        var session = await _db.LocalSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (session is null)
        {
            return null;
        }

        if (session.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return null;
        }

        return session;
    }

    public async Task UpsertUxSnapshotAsync(
        string userId,
        string username,
        string roleCode,
        IEnumerable<string> permissions,
        string? serverBaseUrl,
        TimeSpan? ttl = null,
        string? secureAuthMaterial = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // Defensa: nunca aceptar un campo password.
        if (permissions?.Any(p => string.Equals(p, "password", StringComparison.OrdinalIgnoreCase)) == true)
        {
            throw new ArgumentException("El snapshot de sesión no admite contraseñas.");
        }

        var now = DateTime.UtcNow;
        var expires = now.Add(ttl ?? OfflineStoreConstants.DefaultSessionTtl);
        var csv = string.Join(',',
            (permissions ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));

        var hasSecure = false;
        if (!string.IsNullOrEmpty(secureAuthMaterial))
        {
            await _secureStore.SetAuthMaterialAsync(userId, secureAuthMaterial, ct);
            hasSecure = true;
        }
        else
        {
            var existing = await _secureStore.GetAuthMaterialAsync(userId, ct);
            hasSecure = !string.IsNullOrEmpty(existing);
        }

        var session = await _db.LocalSessions.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (session is null)
        {
            session = new LocalSession { Id = 1 };
            _db.LocalSessions.Add(session);
        }

        session.UserId = userId.Trim();
        session.Username = username?.Trim() ?? string.Empty;
        session.RoleCode = roleCode?.Trim() ?? string.Empty;
        session.PermissionsCsv = csv;
        session.CapturedAtUtc = now;
        session.ExpiresAtUtc = expires;
        session.ServerBaseUrl = serverBaseUrl;
        session.HasSecureAuthMaterial = hasSecure;

        await _db.SaveChangesAsync(ct);
    }

    public bool HasPermission(LocalSession session, string permission)
    {
        if (string.IsNullOrWhiteSpace(permission) || string.IsNullOrWhiteSpace(session.PermissionsCsv))
        {
            return false;
        }

        return session.PermissionsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Inspección de esquema: confirma que LocalSession no define columnas de password.</summary>
    public static IReadOnlyList<string> LocalSessionPropertyNames() =>
        typeof(LocalSession).GetProperties().Select(p => p.Name).ToArray();
}
