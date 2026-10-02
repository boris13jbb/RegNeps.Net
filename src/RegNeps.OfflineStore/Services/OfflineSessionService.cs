using Microsoft.EntityFrameworkCore;
using RegNeps.OfflineStore.Abstractions;
using RegNeps.OfflineStore.Bridge;
using RegNeps.OfflineStore.Entities;
using RegNeps.OfflineStore.Enums;

namespace RegNeps.OfflineStore.Services;

/// <summary>
/// Gestiona el snapshot UX de sesión offline (expirable). No autoriza en servidor.
/// No almacena contraseñas ni cookies de autenticación.
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

    /// <summary>Sesión usable para captura offline (TTL vigente).</summary>
    public async Task<LocalSession?> GetValidSessionAsync(CancellationToken ct = default)
    {
        var session = await GetRawSessionAsync(ct);
        if (session is null)
        {
            return null;
        }

        return IsWithinTtl(session) ? session : null;
    }

    /// <summary>Lee el snapshot aunque esté expirado (solo diagnóstico/UX).</summary>
    public Task<LocalSession?> GetRawSessionAsync(CancellationToken ct = default) =>
        _db.LocalSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct);

    public static bool IsWithinTtl(LocalSession session) =>
        session.ExpiresAtUtc > DateTime.UtcNow;

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

        // Defensa: nunca aceptar un campo password en permisos ni material con nombre de secreto.
        if (permissions?.Any(p => OfflineBridgeCodec.IsForbiddenFieldName(p)) == true)
        {
            throw new ArgumentException("El snapshot de sesión no admite contraseñas ni secretos.");
        }

        // Fase 2D.1: no persistir cookies/tokens vía este API desde el bridge.
        // El parámetro secureAuthMaterial queda solo para tests/compat; nunca es obligatorio.
        if (!string.IsNullOrEmpty(secureAuthMaterial) &&
            LooksLikeHttpCookieOrBearer(secureAuthMaterial))
        {
            throw new ArgumentException(
                "No se permite almacenar cookies ni tokens de autenticación en el material seguro.");
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
        session.ServerBaseUrl = string.IsNullOrWhiteSpace(serverBaseUrl) ? null : serverBaseUrl.Trim();
        session.HasSecureAuthMaterial = hasSecure;

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Logout local: borra snapshot UX y material seguro.
    /// Conserva PendingOperations/LocalNepRecords (no destruir Outbox sin decisión explícita de sync).
    /// </summary>
    public async Task<ClearLocalSessionResult> ClearUxSnapshotAsync(CancellationToken ct = default)
    {
        var session = await _db.LocalSessions.FirstOrDefaultAsync(x => x.Id == 1, ct);
        var previousUserId = session?.UserId;
        var pending = await _db.PendingOperations.CountAsync(
            o => o.Status == PendingOperationStatus.Pending, ct);

        if (session is not null)
        {
            _db.LocalSessions.Remove(session);
            await _db.SaveChangesAsync(ct);
        }

        if (!string.IsNullOrWhiteSpace(previousUserId))
        {
            await _secureStore.ClearAsync(previousUserId, ct);
        }

        return new ClearLocalSessionResult
        {
            Cleared = true,
            PreviousUserId = previousUserId,
            PendingOperationsRetained = pending
        };
    }

    public OfflineSessionViewDto ToView(LocalSession? session, bool treatExpiredAsInvalid = true)
    {
        if (session is null)
        {
            return new OfflineSessionViewDto { HasSession = false, IsValid = false };
        }

        var valid = IsWithinTtl(session);
        return new OfflineSessionViewDto
        {
            HasSession = true,
            IsValid = treatExpiredAsInvalid ? valid : valid,
            UserId = session.UserId,
            Username = session.Username,
            RoleCode = session.RoleCode,
            Permissions = string.IsNullOrWhiteSpace(session.PermissionsCsv)
                ? Array.Empty<string>()
                : session.PermissionsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ExpiresAtUtc = session.ExpiresAtUtc,
            ServerBaseUrl = session.ServerBaseUrl,
            HasSecureAuthMaterial = session.HasSecureAuthMaterial
        };
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

    private static bool LooksLikeHttpCookieOrBearer(string material)
    {
        var m = material.Trim();
        if (m.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Cookie típica de ASP.NET: nombre=valor; ...
        if (m.Contains("RegNeps.Auth", StringComparison.OrdinalIgnoreCase) ||
            m.Contains(".AspNetCore.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
