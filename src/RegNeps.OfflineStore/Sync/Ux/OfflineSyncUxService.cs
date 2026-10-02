using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RegNeps.OfflineStore.Entities;
using RegNeps.OfflineStore.Enums;
using RegNeps.OfflineStore.Sync.Contracts;

namespace RegNeps.OfflineStore.Sync.Ux;

/// <summary>
/// Agrega contadores Outbox y mensajes UX a partir del store local y <see cref="SyncRunResult"/>.
/// No ejecuta sincronización: eso sigue siendo responsabilidad de <see cref="ISyncEngine"/>.
/// </summary>
public sealed class OfflineSyncUxService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly LocalSyncDbContext _db;

    public OfflineSyncUxService(LocalSyncDbContext db)
    {
        _db = db;
    }

    public async Task<OfflineOutboxCounters> GetCountersAsync(string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var rows = await _db.PendingOperations.AsNoTracking()
            .Where(o => o.UserId == userId)
            .GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int CountOf(PendingOperationStatus status) =>
            rows.FirstOrDefault(r => r.Status == status)?.Count ?? 0;

        return new OfflineOutboxCounters
        {
            Pending = CountOf(PendingOperationStatus.Pending) + CountOf(PendingOperationStatus.Sending),
            Synced = CountOf(PendingOperationStatus.Synced),
            SyncError = CountOf(PendingOperationStatus.SyncError),
            Conflict = CountOf(PendingOperationStatus.Conflict)
        };
    }

    public async Task<OfflineSyncSummary> GetSummaryAsync(string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var counters = await GetCountersAsync(userId, ct);
        var state = await _db.SyncStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);

        return new OfflineSyncSummary
        {
            Counters = counters,
            LastSyncAtUtc = state?.LastSuccessfulSyncUtc ?? state?.UpdatedAtUtc,
            LastConnectivityStatus = state?.LastConnectivityStatus,
            LastSyncErrorSummary = SanitizeError(state?.LastError),
            LastPulledSequence = state?.LastPulledSequence ?? 0
        };
    }

    public async Task<IReadOnlyList<ConflictReviewItem>> GetConflictsAsync(
        string userId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var ops = await _db.PendingOperations.AsNoTracking()
            .Where(o => o.UserId == userId && o.Status == PendingOperationStatus.Conflict)
            .OrderByDescending(o => o.LastAttemptAtUtc ?? o.CreatedAtUtc)
            .ToListAsync(ct);

        if (ops.Count == 0)
        {
            return Array.Empty<ConflictReviewItem>();
        }

        var localIds = ops
            .Where(o => o.LocalNepRecordId.HasValue)
            .Select(o => o.LocalNepRecordId!.Value)
            .Distinct()
            .ToList();

        var locals = localIds.Count == 0
            ? new Dictionary<Guid, LocalNepRecord>()
            : await _db.LocalNepRecords.AsNoTracking()
                .Where(r => r.UserId == userId && localIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, ct);

        var items = new List<ConflictReviewItem>(ops.Count);
        foreach (var op in ops)
        {
            LocalNepRecord? local = null;
            if (op.LocalNepRecordId is { } lid)
            {
                locals.TryGetValue(lid, out local);
            }

            items.Add(BuildConflictItem(op, local));
        }

        return items;
    }

    /// <summary>
    /// Traduce el resultado del motor a mensajes comprensibles (sin secretos ni payloads).
    /// </summary>
    public static SyncRunUxFeedback MapRunResult(SyncRunResult result, OfflineOutboxCounters? afterCounters = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var accepted = result.PushedAccepted + result.PushedDuplicate;
        var conflicts = result.PushedConflict;
        var errors = result.PushedSyncError;
        var stillPending = afterCounters?.Pending ?? result.PushedTransient;

        if (result.AuthRequired)
        {
            return new SyncRunUxFeedback
            {
                Success = false,
                RequiresLogin = true,
                DidNotStart = !result.Started,
                Title = "Requiere inicio de sesión",
                Message = SyncResultUserMessages.RequiresLogin,
                AcceptedOrDuplicate = accepted,
                ConflictCount = conflicts,
                ErrorCount = errors,
                StillPending = stillPending
            };
        }

        if (!result.Started)
        {
            return new SyncRunUxFeedback
            {
                Success = false,
                DidNotStart = true,
                Title = "Sincronización no iniciada",
                Message = SanitizeError(result.Message)
                          ?? (result.SessionMissingOrExpired
                              ? "No hay sesión local válida. Inicia sesión online primero."
                              : "No se pudo iniciar la sincronización."),
                AcceptedOrDuplicate = 0,
                ConflictCount = 0,
                ErrorCount = 0,
                StillPending = afterCounters?.Pending ?? 0
            };
        }

        var pendingAfter = afterCounters?.Pending ?? stillPending;
        var conflictAfter = afterCounters?.Conflict ?? conflicts;
        var errorAfter = afterCounters?.SyncError ?? errors;
        var hasTransportError = !string.IsNullOrWhiteSpace(result.Message)
                                && !result.PullCompleted;

        if (hasTransportError && accepted == 0 && conflicts == 0 && errors == 0)
        {
            return new SyncRunUxFeedback
            {
                Success = false,
                Partial = false,
                Title = "Sincronización no completada",
                Message = SanitizeError(result.Message) ?? SyncResultUserMessages.Transient,
                AcceptedOrDuplicate = 0,
                ConflictCount = conflictAfter,
                ErrorCount = errorAfter,
                StillPending = pendingAfter
            };
        }

        if (conflictAfter > 0 || errorAfter > 0 || pendingAfter > 0 || hasTransportError)
        {
            return new SyncRunUxFeedback
            {
                Success = result.PullCompleted || accepted > 0,
                Partial = true,
                Title = "Sincronización parcial",
                Message = BuildPartialMessage(accepted, conflictAfter, errorAfter, pendingAfter, result.Message),
                AcceptedOrDuplicate = accepted,
                ConflictCount = conflictAfter,
                ErrorCount = errorAfter,
                StillPending = pendingAfter
            };
        }

        return new SyncRunUxFeedback
        {
            Success = true,
            Title = "Sincronización completada",
            Message = SyncResultUserMessages.SyncedOk,
            AcceptedOrDuplicate = accepted,
            ConflictCount = 0,
            ErrorCount = 0,
            StillPending = 0
        };
    }

    public static string DescribePushResult(string? resultCode) => resultCode switch
    {
        ClientSyncResultNames.Accepted or ClientSyncResultNames.Duplicate => SyncResultUserMessages.SyncedOk,
        ClientSyncResultNames.TransientError => SyncResultUserMessages.Transient,
        ClientSyncResultNames.Forbidden => SyncResultUserMessages.Forbidden,
        ClientSyncResultNames.Invalid => SyncResultUserMessages.Invalid,
        ClientSyncResultNames.Conflict => SyncResultUserMessages.Conflict,
        _ => "Resultado de sincronización desconocido."
    };

    public static string DescribeSyncErrorKind(PendingOperation op)
    {
        ArgumentNullException.ThrowIfNull(op);
        var code = op.LastServerErrorCode ?? string.Empty;

        if (code.Contains("FORBIDDEN", StringComparison.OrdinalIgnoreCase)
            || code.Contains("403", StringComparison.OrdinalIgnoreCase))
        {
            return SyncResultUserMessages.Forbidden;
        }

        if (code.Contains("UNAUTHORIZED", StringComparison.OrdinalIgnoreCase)
            || code.Contains("401", StringComparison.OrdinalIgnoreCase))
        {
            return SyncResultUserMessages.RequiresLogin;
        }

        if (op.Status == PendingOperationStatus.Conflict)
        {
            return SyncResultUserMessages.Conflict;
        }

        if (code.Contains("INVALID", StringComparison.OrdinalIgnoreCase)
            || code.Contains("CLIENT_OPERATION_REUSED", StringComparison.OrdinalIgnoreCase))
        {
            return SyncResultUserMessages.Invalid;
        }

        // SyncError permanente vs Transient (que permanece Pending).
        if (op.Status == PendingOperationStatus.Pending)
        {
            return SyncResultUserMessages.Transient;
        }

        return SanitizeError(op.LastError) ?? SyncResultUserMessages.Invalid;
    }

    public static SyncConnectivityUxKind ResolveConnectivity(
        bool hasLocalSession,
        bool localSessionExpired,
        bool hasAuthCookie,
        bool noNetwork,
        bool serverUnreachable,
        bool lastRunRequiresLogin)
    {
        if (lastRunRequiresLogin)
        {
            return SyncConnectivityUxKind.RequiresLogin;
        }

        if (!hasLocalSession)
        {
            return localSessionExpired
                ? SyncConnectivityUxKind.LocalSessionExpired
                : SyncConnectivityUxKind.NoLocalSession;
        }

        if (localSessionExpired)
        {
            return SyncConnectivityUxKind.LocalSessionExpired;
        }

        if (noNetwork)
        {
            return SyncConnectivityUxKind.OfflineNoNetwork;
        }

        if (!hasAuthCookie)
        {
            return SyncConnectivityUxKind.LocalSessionWithoutOnlineAuth;
        }

        if (serverUnreachable)
        {
            return SyncConnectivityUxKind.ServerUnreachable;
        }

        return SyncConnectivityUxKind.OnlineReady;
    }

    public static string ConnectivityBanner(SyncConnectivityUxKind kind) => kind switch
    {
        SyncConnectivityUxKind.OnlineReady => "Conectado — puedes sincronizar.",
        SyncConnectivityUxKind.OfflineNoNetwork => "Sin conexión — puedes seguir capturando offline.",
        SyncConnectivityUxKind.ServerUnreachable => "Sin acceso al servidor — la captura offline sigue disponible.",
        SyncConnectivityUxKind.LocalSessionWithoutOnlineAuth => SyncResultUserMessages.LocalOnlyCapture,
        SyncConnectivityUxKind.LocalSessionExpired => "Tu sesión local expiró. Inicia sesión online para continuar.",
        SyncConnectivityUxKind.NoLocalSession => "No hay sesión local. Inicia sesión online para capturar offline.",
        SyncConnectivityUxKind.RequiresLogin => SyncResultUserMessages.RequiresLogin,
        _ => "Estado de conexión desconocido."
    };

    /// <summary>Colores hex del banner (Bg, Fg) — la UI MAUI los interpreta.</summary>
    public static (string BgHex, string FgHex) BannerColorHex(SyncConnectivityUxKind kind) => kind switch
    {
        SyncConnectivityUxKind.OnlineReady => ("#DCFCE7", "#14532D"),
        SyncConnectivityUxKind.OfflineNoNetwork or SyncConnectivityUxKind.ServerUnreachable
            => ("#FFEDD5", "#7C2D12"),
        SyncConnectivityUxKind.RequiresLogin
            or SyncConnectivityUxKind.LocalSessionWithoutOnlineAuth
            or SyncConnectivityUxKind.LocalSessionExpired
            or SyncConnectivityUxKind.NoLocalSession
            => ("#FEF3C7", "#92400E"),
        _ => ("#E5E7EB", "#374151")
    };

    private static ConflictReviewItem BuildConflictItem(PendingOperation op, LocalNepRecord? local)
    {
        var localSummary = local is null
            ? DescribePayload(op.PayloadJson, op.OperationType)
            : FormatRecordLine(local.Telar, local.Neps, local.MtsCalculados, local.GetQualityLabel(), local.IsDeleted);

        var serverSummary = DescribeServerSnapshot(op.ConflictServerSnapshotJson, op.OperationType);
        var entityLabel = local is not null
            ? $"Telar {local.Telar}"
            : ExtractTelarFromPayload(op.PayloadJson) is { Length: > 0 } t
                ? $"Telar {t}"
                : $"Registro {(op.LocalNepRecordId ?? op.Id).ToString("N")[..8]}…";

        return new ConflictReviewItem
        {
            OperationId = op.Id,
            EntityId = op.LocalNepRecordId ?? op.Id,
            OperationTypeLabel = op.OperationType switch
            {
                OfflineOperationType.CreateRecord => "Crear",
                OfflineOperationType.UpdateRecord => "Actualizar",
                OfflineOperationType.DeleteRecord => "Eliminar",
                OfflineOperationType.ApplyCorrective => "Correctiva",
                _ => op.OperationType.ToString()
            },
            EntityLabel = entityLabel,
            LocalSummary = localSummary,
            ServerSummary = serverSummary,
            RelevantAtUtc = op.LastAttemptAtUtc ?? op.CreatedAtUtc,
            Reason = string.IsNullOrWhiteSpace(op.LastError)
                ? SyncResultUserMessages.Conflict
                : SanitizeError(op.LastError) ?? SyncResultUserMessages.Conflict,
            ActionHint = "Requiere revisión"
        };
    }

    private static string DescribeServerSnapshot(string? json, OfflineOperationType operationType)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "Sin datos del servidor disponibles.";
        }

        try
        {
            if (operationType == OfflineOperationType.DeleteRecord
                || json.Contains("deletedAtUtc", StringComparison.OrdinalIgnoreCase)
                || json.Contains("DeletedAtUtc", StringComparison.OrdinalIgnoreCase))
            {
                var del = JsonSerializer.Deserialize<ClientNepRecordDeletedSnapshot>(json, JsonOpts);
                if (del is not null && del.DeletedAtUtc != default)
                {
                    return $"Eliminado en servidor · {del.DeletedAtUtc:yyyy-MM-dd HH:mm} UTC";
                }
            }

            var snap = JsonSerializer.Deserialize<ClientNepRecordSnapshot>(json, JsonOpts);
            if (snap is null)
            {
                return "Datos del servidor no legibles.";
            }

            var quality = string.IsNullOrWhiteSpace(snap.QualityLabel) ? "—" : snap.QualityLabel;
            var mts = snap.MtsCalculados > 0
                ? snap.MtsCalculados
                : (snap.Neps > 0 ? snap.Neps / 0.09 : 0);
            return FormatRecordLine(snap.Telar, snap.Neps, mts, quality, isDeleted: false);
        }
        catch (JsonException)
        {
            return "Datos del servidor no legibles.";
        }
    }

    private static string DescribePayload(string? payloadJson, OfflineOperationType operationType)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return operationType == OfflineOperationType.DeleteRecord
                ? "Eliminación local pendiente."
                : "Sin detalle local.";
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var telar = GetString(root, "Telar") ?? GetString(root, "telar") ?? "?";
            var neps = GetDouble(root, "Neps") ?? GetDouble(root, "neps") ?? 0;
            var mts = GetDouble(root, "MtsCalculados") ?? GetDouble(root, "mtsCalculados")
                      ?? (neps > 0 ? neps / 0.09 : 0);
            var quality = GetString(root, "QualityLabel") ?? GetString(root, "qualityLabel") ?? "—";
            return FormatRecordLine(telar, neps, mts, quality, isDeleted: false);
        }
        catch (JsonException)
        {
            return "Datos locales no legibles.";
        }
    }

    private static string? ExtractTelarFromPayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return GetString(doc.RootElement, "Telar") ?? GetString(doc.RootElement, "telar");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FormatRecordLine(
        string telar,
        double neps,
        double mts,
        string quality,
        bool isDeleted)
    {
        var q = string.IsNullOrWhiteSpace(quality) ? "—" : quality;
        var baseLine = $"Telar {telar} · NEPS {neps:0.##} · NEPS/m {mts:0.##} · {q}";
        return isDeleted ? $"{baseLine} (eliminado localmente)" : baseLine;
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
        {
            return p.GetString();
        }

        return null;
    }

    private static double? GetDouble(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var p) && p.TryGetDouble(out var d))
        {
            return d;
        }

        return null;
    }

    private static string BuildPartialMessage(
        int accepted,
        int conflicts,
        int errors,
        int pending,
        string? transportMessage)
    {
        var parts = new List<string>();
        if (accepted > 0)
        {
            parts.Add($"{accepted} sincronizada(s)");
        }

        if (conflicts > 0)
        {
            parts.Add($"{conflicts} en conflicto (requieren revisión)");
        }

        if (errors > 0)
        {
            parts.Add($"{errors} con error");
        }

        if (pending > 0)
        {
            parts.Add($"{pending} aún pendiente(s)");
        }

        var core = parts.Count == 0
            ? "La sincronización terminó con resultados mixtos."
            : string.Join(" · ", parts) + ".";

        var transport = SanitizeError(transportMessage);
        return string.IsNullOrWhiteSpace(transport) ? core : $"{core} {transport}";
    }

    /// <summary>Evita filtrar cookies, tokens, connection strings o stacks al usuario.</summary>
    public static string? SanitizeError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var t = raw.Trim();
        if (t.Contains("cookie", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Bearer", StringComparison.OrdinalIgnoreCase)
            || t.Contains("password", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Connection String", StringComparison.OrdinalIgnoreCase)
            || t.Contains("at RegNeps.", StringComparison.OrdinalIgnoreCase)
            || t.Length > 280)
        {
            if (t.Contains("cookie", StringComparison.OrdinalIgnoreCase)
                || t.Contains("401", StringComparison.OrdinalIgnoreCase)
                || t.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
                || t.Contains("iniciar sesión", StringComparison.OrdinalIgnoreCase))
            {
                return SyncResultUserMessages.RequiresLogin;
            }

            if (t.Contains("403", StringComparison.OrdinalIgnoreCase)
                || t.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
            {
                return SyncResultUserMessages.Forbidden;
            }

            return "Ocurrió un error al sincronizar. Intenta de nuevo más tarde.";
        }

        return t;
    }
}
