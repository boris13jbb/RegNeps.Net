using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RegNeps.OfflineStore.Entities;
using RegNeps.OfflineStore.Enums;
using RegNeps.OfflineStore.Sync.Contracts;

namespace RegNeps.OfflineStore.Sync.Ux;

/// <summary>
/// Inventario y detalle de operaciones Outbox (FASE 2D.4).
/// No ejecuta Push propio: el reintento usa <see cref="ISyncEngine"/> vía <see cref="ManualSyncRunner"/>.
/// Aislado por UserId; no expone secretos.
/// </summary>
public sealed class OfflineOperationsUxService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly LocalSyncDbContext _db;

    public OfflineOperationsUxService(LocalSyncDbContext db) => _db = db;

    public async Task<IReadOnlyList<OfflineOperationListItem>> ListAsync(
        string userId,
        OfflineOperationListFilter filter = OfflineOperationListFilter.All,
        int take = OfflineOperationStatusLabels.DefaultListLimit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        take = Math.Clamp(take, 1, OfflineOperationStatusLabels.DefaultListLimit);

        var query = _db.PendingOperations.AsNoTracking()
            .Where(o => o.UserId == userId);

        query = filter switch
        {
            OfflineOperationListFilter.Pending => query.Where(o =>
                o.Status == PendingOperationStatus.Pending
                || o.Status == PendingOperationStatus.Sending),
            OfflineOperationListFilter.Synced => query.Where(o => o.Status == PendingOperationStatus.Synced),
            OfflineOperationListFilter.SyncError => query.Where(o => o.Status == PendingOperationStatus.SyncError),
            OfflineOperationListFilter.Conflict => query.Where(o => o.Status == PendingOperationStatus.Conflict),
            _ => query.Where(o => o.Status != PendingOperationStatus.Cancelled)
        };

        var ops = await query
            .OrderByDescending(o => o.CreatedAtUtc)
            .Take(take)
            .ToListAsync(ct);

        if (ops.Count == 0)
        {
            return Array.Empty<OfflineOperationListItem>();
        }

        var localIds = ops.Where(o => o.LocalNepRecordId.HasValue)
            .Select(o => o.LocalNepRecordId!.Value)
            .Distinct()
            .ToList();

        var locals = localIds.Count == 0
            ? new Dictionary<Guid, LocalNepRecord>()
            : await _db.LocalNepRecords.AsNoTracking()
                .Where(r => r.UserId == userId && localIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, ct);

        return ops.Select(op =>
        {
            locals.TryGetValue(op.LocalNepRecordId ?? Guid.Empty, out var local);
            var action = ClassifyAction(op);
            return new OfflineOperationListItem
            {
                OperationId = op.Id,
                CreatedAtUtc = op.CreatedAtUtc,
                OperationTypeLabel = OfflineOperationStatusLabels.OperationTypeLabel(op.OperationType),
                Status = op.Status == PendingOperationStatus.Sending
                    ? PendingOperationStatus.Pending
                    : op.Status,
                StatusLabel = OfflineOperationStatusLabels.StatusLabel(op.Status),
                RecordSummary = BuildRecordSummary(op, local),
                SyncResultLabel = BuildSyncResultLabel(op),
                ErrorSummary = string.IsNullOrWhiteSpace(op.LastError)
                    ? null
                    : OfflineSyncUxService.SanitizeError(op.LastError),
                AttemptCount = op.AttemptCount,
                RequiresReview = op.Status == PendingOperationStatus.Conflict,
                PrimaryAction = action
            };
        }).ToList();
    }

    public async Task<OfflineOperationDetail?> GetDetailAsync(
        string userId,
        Guid operationId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var op = await _db.PendingOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == operationId && o.UserId == userId, ct);
        if (op is null)
        {
            return null;
        }

        LocalNepRecord? local = null;
        if (op.LocalNepRecordId is { } lid)
        {
            local = await _db.LocalNepRecords.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == lid && r.UserId == userId, ct);
        }

        var action = ClassifyAction(op);
        var serverId = local?.ServerRecordId ?? op.TargetServerRecordId;
        var serverIdDisplay = serverId is { } sid
            ? sid.ToString("D")
            : op.Status is PendingOperationStatus.Pending or PendingOperationStatus.Sending
                ? "Servidor aún no asignado."
                : "—";

        return new OfflineOperationDetail
        {
            OperationId = op.Id,
            OperationTypeLabel = OfflineOperationStatusLabels.OperationTypeLabel(op.OperationType),
            Status = op.Status == PendingOperationStatus.Sending
                ? PendingOperationStatus.Pending
                : op.Status,
            StatusLabel = OfflineOperationStatusLabels.StatusLabel(op.Status),
            CreatedAtUtc = op.CreatedAtUtc,
            LastAttemptAtUtc = op.LastAttemptAtUtc,
            AttemptCount = op.AttemptCount,
            UserFacingSummary = BuildUserFacingSummary(op),
            ErrorFriendly = string.IsNullOrWhiteSpace(op.LastError)
                ? null
                : OfflineSyncUxService.DescribeSyncErrorKind(op),
            LocalRecordSummary = BuildRecordSummary(op, local),
            ServerRecordSummary = DescribeServerSide(op),
            ServerIdDisplay = serverIdDisplay,
            LocalIdDisplay = (op.LocalNepRecordId ?? op.Id).ToString("D"),
            ConflictStampDisplay = string.IsNullOrWhiteSpace(op.ConflictServerConcurrencyStamp)
                ? null
                : op.ConflictServerConcurrencyStamp,
            RequiresReview = op.Status == PendingOperationStatus.Conflict,
            ClientOperationId = op.ClientOperationId,
            LastServerErrorCode = RedactErrorCode(op.LastServerErrorCode),
            CaptureSessionId = op.CaptureSessionId,
            LocalNepRecordId = op.LocalNepRecordId,
            ServerRecordId = local?.ServerRecordId,
            TargetServerRecordId = op.TargetServerRecordId,
            PrimaryAction = action,
            ActionHint = ActionHint(action)
        };
    }

    /// <summary>
    /// Prepára un SyncError recuperable para reintento: vuelve a Pending sin cambiar ClientOperationId.
    /// No crea una segunda operación. Conflict/Forbidden/Invalid no se reabren.
    /// </summary>
    public async Task<(bool Prepared, string Message)> TryPrepareRetryAsync(
        string userId,
        Guid operationId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var op = await _db.PendingOperations
            .FirstOrDefaultAsync(o => o.Id == operationId && o.UserId == userId, ct);
        if (op is null)
        {
            return (false, "Operación no encontrada.");
        }

        if (op.UserId != userId)
        {
            return (false, "No puedes modificar operaciones de otro usuario.");
        }

        var action = ClassifyAction(op);
        if (action == OfflineOperationUxAction.RetrySync)
        {
            op.Status = PendingOperationStatus.Pending;
            // Conservar ClientOperationId, AttemptCount e historial de error para diagnóstico.
            await _db.SaveChangesAsync(ct);
            return (true, "La operación volverá a intentarse con el mismo identificador.");
        }

        if (action == OfflineOperationUxAction.SyncNow)
        {
            return (true, "La operación ya está pendiente y se incluirá en la sincronización.");
        }

        return (false, ActionHint(action));
    }

    public static OfflineOperationUxAction ClassifyAction(PendingOperation op)
    {
        ArgumentNullException.ThrowIfNull(op);

        if (op.Status is PendingOperationStatus.Pending or PendingOperationStatus.Sending)
        {
            return OfflineOperationUxAction.SyncNow;
        }

        if (op.Status == PendingOperationStatus.Conflict)
        {
            return OfflineOperationUxAction.RequiresReview;
        }

        if (op.Status == PendingOperationStatus.Synced)
        {
            return OfflineOperationUxAction.None;
        }

        if (op.Status != PendingOperationStatus.SyncError)
        {
            return OfflineOperationUxAction.None;
        }

        var code = op.LastServerErrorCode ?? string.Empty;
        if (IsAuthRequiredCode(code) || LooksLikeUnauthorized(op.LastError))
        {
            return OfflineOperationUxAction.Relogin;
        }

        if (IsPermanentNoRetry(code))
        {
            return OfflineOperationUxAction.NoRetryPermanent;
        }

        // MISSING_RESULT u otros SyncError no permanentes: reabrir a Pending + SyncEngine.
        return OfflineOperationUxAction.RetrySync;
    }

    public static string ActionHint(OfflineOperationUxAction action) => action switch
    {
        OfflineOperationUxAction.SyncNow => "Usa «Sincronizar ahora» para enviar esta operación pendiente.",
        OfflineOperationUxAction.RetrySync => "Puedes reintentar la sincronización con el mismo identificador.",
        OfflineOperationUxAction.Relogin => SyncResultUserMessages.RequiresLogin,
        OfflineOperationUxAction.RequiresReview => "Requiere revisión. No se aplicará automáticamente ningún cambio.",
        OfflineOperationUxAction.NoRetryPermanent =>
            "Este error no se resuelve reintentando. Revisa permisos o vuelve a capturar si aplica.",
        _ => string.Empty
    };

    private static bool IsAuthRequiredCode(string code) =>
        code.Contains("UNAUTHORIZED", StringComparison.OrdinalIgnoreCase)
        || code.Contains("401", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeUnauthorized(string? error) =>
        !string.IsNullOrWhiteSpace(error)
        && (error.Contains("401", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
            || error.Contains("iniciar sesión", StringComparison.OrdinalIgnoreCase)
            || error.Contains("cookie", StringComparison.OrdinalIgnoreCase));

    private static bool IsPermanentNoRetry(string code) =>
        code.Contains("FORBIDDEN", StringComparison.OrdinalIgnoreCase)
        || code.Contains("403", StringComparison.OrdinalIgnoreCase)
        || code.Contains("INVALID", StringComparison.OrdinalIgnoreCase)
        || code.Contains("CLIENT_OPERATION_REUSED", StringComparison.OrdinalIgnoreCase)
        || code.Contains("ENTITY_DELETED", StringComparison.OrdinalIgnoreCase)
        || code.Contains("ENTITY_NOT_FOUND", StringComparison.OrdinalIgnoreCase);

    private static string? RedactErrorCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        // Códigos de dominio, no secretos.
        return code.Length > 80 ? code[..80] : code;
    }

    private static string BuildSyncResultLabel(PendingOperation op) => op.Status switch
    {
        PendingOperationStatus.Synced => SyncResultUserMessages.SyncedOk,
        PendingOperationStatus.Conflict => SyncResultUserMessages.Conflict,
        PendingOperationStatus.SyncError => OfflineSyncUxService.DescribeSyncErrorKind(op),
        PendingOperationStatus.Pending or PendingOperationStatus.Sending => "Aún no confirmado por el servidor.",
        _ => OfflineOperationStatusLabels.StatusLabel(op.Status)
    };

    private static string BuildUserFacingSummary(PendingOperation op) =>
        $"{OfflineOperationStatusLabels.OperationTypeLabel(op.OperationType)} · {OfflineOperationStatusLabels.StatusLabel(op.Status)}";

    private static string BuildRecordSummary(PendingOperation op, LocalNepRecord? local)
    {
        if (local is not null)
        {
            var serverPart = local.ServerRecordId is { } sid
                ? $" · Servidor {sid.ToString("N")[..8]}…"
                : " · servidor aún no asignado";
            return $"Telar {local.Telar} · NEPS {local.Neps:0.##} · {local.GetQualityLabel()}{serverPart}";
        }

        return DescribePayloadBrief(op.PayloadJson, op.OperationType);
    }

    private static string DescribeServerSide(PendingOperation op)
    {
        if (op.Status != PendingOperationStatus.Conflict
            || string.IsNullOrWhiteSpace(op.ConflictServerSnapshotJson))
        {
            return op.Status == PendingOperationStatus.Conflict
                ? "Sin datos del servidor disponibles."
                : "—";
        }

        try
        {
            var snap = JsonSerializer.Deserialize<ClientNepRecordSnapshot>(op.ConflictServerSnapshotJson, JsonOpts);
            if (snap is null)
            {
                return "Datos del servidor no legibles.";
            }

            var quality = string.IsNullOrWhiteSpace(snap.QualityLabel) ? "—" : snap.QualityLabel;
            var mts = snap.MtsCalculados > 0 ? snap.MtsCalculados : snap.Neps / 0.09;
            return $"Telar {snap.Telar} · NEPS {snap.Neps:0.##} · NEPS/m {mts:0.##} · {quality}";
        }
        catch (JsonException)
        {
            return "Datos del servidor no legibles.";
        }
    }

    private static string DescribePayloadBrief(string? payloadJson, OfflineOperationType type)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return OfflineOperationStatusLabels.OperationTypeLabel(type);
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var telar = GetString(root, "Telar") ?? GetString(root, "telar") ?? "?";
            var neps = GetDouble(root, "Neps") ?? GetDouble(root, "neps") ?? 0;
            return $"Telar {telar} · NEPS {neps:0.##}";
        }
        catch (JsonException)
        {
            return OfflineOperationStatusLabels.OperationTypeLabel(type);
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static double? GetDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.TryGetDouble(out var d) ? d : null;
}
