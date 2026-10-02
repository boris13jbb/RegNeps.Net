using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Services;
using RegNeps.OfflineStore.Abstractions;
using RegNeps.OfflineStore.Entities;
using RegNeps.OfflineStore.Enums;
using RegNeps.OfflineStore.Models;
using RegNeps.OfflineStore.Sync.Contracts;

namespace RegNeps.OfflineStore.Services;

/// <summary>
/// Resolución explícita de Conflict (FASE 2D.7).
/// Sin LWW, sin Restore, sin reutilizar ClientOperationId conflictivo.
/// Keep Server = Cancel (una sola semántica). Keep Local / Edit&amp;Retry = nueva Outbox.
/// </summary>
public sealed class ConflictResolutionService
{
    public const string ResolutionMarkerKeepServer = "Resolved:KeepServer";
    public const string ResolutionMarkerKeepLocal = "Resolved:KeepLocal";
    public const string ResolutionMarkerEditRetry = "Resolved:EditAndRetry";
    public const string SnapshotJsonVersion = "v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly HashSet<string> SeesAllRoleCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Admin", "Supervisor", "SuperAdmin", "SuperAdministrador"
    };

    private readonly LocalSyncDbContext _db;
    private readonly OfflineSessionService _sessions;
    private readonly IDeviceIdStore _deviceIds;

    public ConflictResolutionService(
        LocalSyncDbContext db,
        OfflineSessionService sessions,
        IDeviceIdStore deviceIds)
    {
        _db = db;
        _sessions = sessions;
        _deviceIds = deviceIds;
    }

    public async Task<ConflictResolutionView?> GetViewAsync(
        Guid operationId,
        CancellationToken ct = default)
    {
        var session = await _sessions.GetValidSessionAsync(ct);
        if (session is null)
        {
            return null;
        }

        var op = await LoadConflictOpAsync(operationId, session, requireConflictStatus: false, ct);
        if (op is null)
        {
            return null;
        }

        if (op.Status != PendingOperationStatus.Conflict
            && !IsResolutionMarker(op.LastError))
        {
            return null;
        }

        var kind = ConflictKindClassifier.Classify(op);
        var localSnap = BuildLocalSnapshot(op.LocalNepRecord, op);
        var serverDeleted = ConflictKindClassifier.IsServerDeleted(op);
        ConflictFieldSnapshot? serverSnap = null;
        if (serverDeleted)
        {
            serverSnap = new ConflictFieldSnapshot
            {
                IsDeleted = true,
                ConcurrencyStamp = op.ConflictServerConcurrencyStamp
                                  ?? (ConflictKindClassifier.TryParseTombstone(
                                      op.ConflictServerSnapshotJson, out var t)
                                      ? t.LastConcurrencyStamp
                                      : null),
                UpdatedAtUtc = ConflictKindClassifier.TryParseTombstone(
                    op.ConflictServerSnapshotJson, out var tomb)
                    ? tomb.DeletedAtUtc
                    : null
            };
        }
        else if (ConflictKindClassifier.TryParseActiveSnapshot(
                     op.ConflictServerSnapshotJson, out var active))
        {
            serverSnap = FromActiveSnapshot(active);
        }

        var allowed = ComputeAllowedActions(kind, session, op);
        return new ConflictResolutionView
        {
            OperationId = op.Id,
            LocalNepRecordId = op.LocalNepRecordId,
            EntityId = op.TargetServerRecordId ?? op.LocalNepRecord?.ServerRecordId,
            Kind = kind,
            KindLabel = ConflictKindClassifier.KindLabel(kind),
            ReasonLabel = ConflictKindClassifier.ReasonLabel(op, kind),
            OperationType = op.OperationType,
            OriginalClientOperationId = op.ClientOperationId,
            LastServerErrorCode = op.LastServerErrorCode,
            ConflictedAtUtc = op.LastAttemptAtUtc ?? op.CreatedAtUtc,
            Local = localSnap,
            Server = serverSnap,
            ServerIsDeleted = serverDeleted,
            Differences = BuildDiffs(localSnap, serverSnap, serverDeleted, kind),
            AllowedActions = allowed,
            KeepServerButtonText = kind switch
            {
                OfflineConflictKind.UpdateDelete or OfflineConflictKind.DeleteDelete
                    => "Aceptar eliminación",
                OfflineConflictKind.DeleteUpdate => "Descartar mi eliminación",
                _ => "Aceptar versión del servidor"
            },
            KeepLocalButtonText = kind switch
            {
                OfflineConflictKind.UpdateUpdate => "Mantener mis cambios",
                OfflineConflictKind.DeleteUpdate => "Confirmar eliminación",
                _ => null
            },
            EditAndRetryHint = kind == OfflineConflictKind.UpdateUpdate
                ? "Edite los valores y genere una nueva actualización sobre la versión del servidor."
                : kind == OfflineConflictKind.UpdateDelete
                    ? "El registro ya no existe. Conservar estos datos requeriría una creación explícita futura (no disponible aquí). No hay Restore."
                    : null,
            BlockedKeepLocalReason = kind == OfflineConflictKind.UpdateDelete
                ? "No se puede reaplicar un Update sobre un EntityId eliminado (sin Restore)."
                : kind == OfflineConflictKind.DeleteDelete
                    ? "El servidor ya eliminó el registro; use Aceptar eliminación."
                    : null
        };
    }

    public Task<ConflictResolutionResult> KeepServerAsync(
        Guid operationId,
        CancellationToken ct = default) =>
        ResolveAsync(operationId, ConflictResolutionDecision.KeepServer, editFields: null, ct);

    public Task<ConflictResolutionResult> KeepLocalAsync(
        Guid operationId,
        CancellationToken ct = default) =>
        ResolveAsync(operationId, ConflictResolutionDecision.KeepLocal, editFields: null, ct);

    public Task<ConflictResolutionResult> EditAndRetryAsync(
        Guid operationId,
        ConflictEditFields fields,
        CancellationToken ct = default) =>
        ResolveAsync(operationId, ConflictResolutionDecision.EditAndRetry, fields, ct);

    private async Task<ConflictResolutionResult> ResolveAsync(
        Guid operationId,
        ConflictResolutionDecision decision,
        ConflictEditFields? editFields,
        CancellationToken ct)
    {
        var session = await _sessions.GetValidSessionAsync(ct);
        if (session is null)
        {
            return Fail(ConflictResolutionOutcome.Unauthorized, "No hay sesión offline válida.");
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var op = await _db.PendingOperations
                .Include(o => o.LocalNepRecord)
                .FirstOrDefaultAsync(o => o.Id == operationId, ct);

            if (op is null)
            {
                await tx.RollbackAsync(ct);
                return Fail(ConflictResolutionOutcome.NotFound, "Operación no encontrada.");
            }

            if (!CanAccessOperation(session, op))
            {
                await tx.RollbackAsync(ct);
                return Fail(ConflictResolutionOutcome.Unauthorized,
                    "No puedes resolver este conflicto (autor o SeesAll requerido).");
            }

            if (op.Status != PendingOperationStatus.Conflict)
            {
                if (IsResolutionMarker(op.LastError))
                {
                    await tx.RollbackAsync(ct);
                    return new ConflictResolutionResult
                    {
                        Outcome = ConflictResolutionOutcome.AlreadyResolved,
                        Message = "Este conflicto ya fue resuelto.",
                        ClosedOperationId = op.Id,
                        ClosedOperation = op,
                        Decision = decision
                    };
                }

                await tx.RollbackAsync(ct);
                return Fail(ConflictResolutionOutcome.InvalidState,
                    "La operación no está en estado Conflict.",
                    op);
            }

            // Bloqueo por EntityId: otra mutación Pending/Sending distinta.
            var entityId = op.TargetServerRecordId ?? op.LocalNepRecord?.ServerRecordId;
            var otherBlocking = await _db.PendingOperations
                .Where(o => o.Id != op.Id)
                .Where(o => o.LocalNepRecordId == op.LocalNepRecordId
                            || (entityId != null && o.TargetServerRecordId == entityId))
                .Where(o => o.Status == PendingOperationStatus.Pending
                            || o.Status == PendingOperationStatus.Sending)
                .AnyAsync(ct);
            if (otherBlocking)
            {
                await tx.RollbackAsync(ct);
                return Fail(ConflictResolutionOutcome.InvalidState,
                    "Ya existe otra operación pendiente para este registro.",
                    op);
            }

            var kind = ConflictKindClassifier.Classify(op);
            var allowed = ComputeAllowedActions(kind, session, op);
            var needed = decision switch
            {
                ConflictResolutionDecision.KeepServer => ConflictResolutionActions.KeepServer,
                ConflictResolutionDecision.KeepLocal => ConflictResolutionActions.KeepLocal,
                ConflictResolutionDecision.EditAndRetry => ConflictResolutionActions.EditAndRetry,
                _ => ConflictResolutionActions.None
            };

            if ((allowed & needed) == 0)
            {
                await tx.RollbackAsync(ct);

                // Permiso UX perdido tras el Conflict (el servidor revalidará al Push si llegara a encolarse).
                if (decision is ConflictResolutionDecision.KeepLocal
                        or ConflictResolutionDecision.EditAndRetry
                    && kind == OfflineConflictKind.UpdateUpdate
                    && !_sessions.HasPermission(session, OfflineStoreConstants.EditRecordsPermission))
                {
                    return Fail(ConflictResolutionOutcome.Unauthorized,
                        "Tu sesión no incluye permiso para editar. El conflicto se conserva.",
                        op, kind, decision);
                }

                if (decision == ConflictResolutionDecision.KeepLocal
                    && kind == OfflineConflictKind.DeleteUpdate
                    && !_sessions.HasPermission(session, OfflineStoreConstants.DeleteRecordsPermission))
                {
                    return Fail(ConflictResolutionOutcome.Unauthorized,
                        "Tu sesión no incluye permiso para eliminar. El conflicto se conserva.",
                        op, kind, decision);
                }

                var msg = decision == ConflictResolutionDecision.KeepLocal
                          && kind == OfflineConflictKind.UpdateDelete
                    ? "Keep Local no está permitido sobre un registro eliminado (sin Restore)."
                    : "Acción no permitida para este tipo de conflicto o permisos de sesión.";
                return Fail(ConflictResolutionOutcome.Rejected, msg, op, kind, decision);
            }

            var now = DateTime.UtcNow;
            ConflictResolutionResult result;
            if (decision == ConflictResolutionDecision.KeepServer)
            {
                result = await ApplyKeepServerAsync(op, kind, session, now, ct);
            }
            else
            {
                result = await ApplyReapplyAsync(op, kind, decision, editFields, session, now, ct);
            }

            if (!result.IsSuccess || result.Outcome == ConflictResolutionOutcome.AlreadyResolved)
            {
                await tx.RollbackAsync(ct);
                _db.ChangeTracker.Clear();
                return result;
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<ConflictResolutionResult> ApplyKeepServerAsync(
        PendingOperation op,
        OfflineConflictKind kind,
        LocalSession session,
        DateTime now,
        CancellationToken ct)
    {
        var record = op.LocalNepRecord
                     ?? (op.LocalNepRecordId is { } lid
                         ? await _db.LocalNepRecords.FirstOrDefaultAsync(r => r.Id == lid, ct)
                         : null);

        if (record is null)
        {
            return Fail(ConflictResolutionOutcome.InvalidState,
                "No hay registro local asociado al conflicto.", op, kind,
                ConflictResolutionDecision.KeepServer);
        }

        switch (kind)
        {
            case OfflineConflictKind.UpdateDelete:
            case OfflineConflictKind.DeleteDelete:
                record.IsDeleted = true;
                record.SyncStatus = LocalSyncStatus.Synced;
                record.UpdatedAtUtc = now;
                if (!string.IsNullOrWhiteSpace(op.ConflictServerConcurrencyStamp))
                {
                    record.ConcurrencyStamp = op.ConflictServerConcurrencyStamp;
                }
                else if (ConflictKindClassifier.TryParseTombstone(
                             op.ConflictServerSnapshotJson, out var tomb)
                         && !string.IsNullOrWhiteSpace(tomb.LastConcurrencyStamp))
                {
                    record.ConcurrencyStamp = tomb.LastConcurrencyStamp;
                }

                break;

            case OfflineConflictKind.UpdateUpdate:
            case OfflineConflictKind.DeleteUpdate:
                if (!ConflictKindClassifier.TryParseActiveSnapshot(
                        op.ConflictServerSnapshotJson, out var snap))
                {
                    return Fail(ConflictResolutionOutcome.InvalidState,
                        "Falta el snapshot del servidor para aceptar su versión.",
                        op, kind, ConflictResolutionDecision.KeepServer);
                }

                ApplyActiveSnapshotToRecord(record, snap, now);
                record.IsDeleted = false;
                record.SyncStatus = LocalSyncStatus.Synced;
                break;

            default:
                return Fail(ConflictResolutionOutcome.InvalidState,
                    "Tipo de conflicto no soportado.", op, kind,
                    ConflictResolutionDecision.KeepServer);
        }

        op.Status = PendingOperationStatus.Cancelled;
        op.LastError =
            $"{ResolutionMarkerKeepServer};by={session.UserId};at={now:O};kind={kind}";
        op.LastAttemptAtUtc = now;

        var level = AlertEvaluator.GetLevel(record.Neps);
        return new ConflictResolutionResult
        {
            Outcome = ConflictResolutionOutcome.Success,
            Message = kind is OfflineConflictKind.UpdateDelete or OfflineConflictKind.DeleteDelete
                ? "Eliminación del servidor aceptada. Tombstone local conservado."
                : "Versión del servidor aceptada. Operación conflictiva cerrada sin Push.",
            Kind = kind,
            Decision = ConflictResolutionDecision.KeepServer,
            ClosedOperationId = op.Id,
            ClosedOperation = op,
            Record = record,
            QualityLabel = level.ToDisplayLabel()
        };
    }

    private async Task<ConflictResolutionResult> ApplyReapplyAsync(
        PendingOperation op,
        OfflineConflictKind kind,
        ConflictResolutionDecision decision,
        ConflictEditFields? editFields,
        LocalSession session,
        DateTime now,
        CancellationToken ct)
    {
        if (kind == OfflineConflictKind.UpdateDelete)
        {
            return Fail(ConflictResolutionOutcome.Rejected,
                "No se puede reaplicar Update sobre EntityId eliminado (sin Restore).",
                op, kind, decision);
        }

        var record = op.LocalNepRecord
                     ?? (op.LocalNepRecordId is { } lid
                         ? await _db.LocalNepRecords.FirstOrDefaultAsync(r => r.Id == lid, ct)
                         : null);
        if (record is null)
        {
            return Fail(ConflictResolutionOutcome.InvalidState,
                "No hay registro local asociado.", op, kind, decision);
        }

        var serverId = op.TargetServerRecordId ?? record.ServerRecordId;
        if (serverId is null || serverId == Guid.Empty)
        {
            return Fail(ConflictResolutionOutcome.InvalidState,
                "Falta EntityId del servidor.", op, kind, decision);
        }

        var expectedStamp = ResolveServerStamp(op);
        if (string.IsNullOrWhiteSpace(expectedStamp))
        {
            return Fail(ConflictResolutionOutcome.InvalidState,
                "Falta ConcurrencyStamp del servidor para reintentar.",
                op, kind, decision);
        }

        // Permisos locales UX (servidor revalida al Push).
        if (kind is OfflineConflictKind.UpdateUpdate)
        {
            if (!_sessions.HasPermission(session, OfflineStoreConstants.EditRecordsPermission))
            {
                return Fail(ConflictResolutionOutcome.Unauthorized,
                    "Tu sesión no incluye permiso para editar. El servidor lo revalidará; no se encola la resolución.",
                    op, kind, decision);
            }
        }
        else if (kind is OfflineConflictKind.DeleteUpdate)
        {
            if (!_sessions.HasPermission(session, OfflineStoreConstants.DeleteRecordsPermission))
            {
                return Fail(ConflictResolutionOutcome.Unauthorized,
                    "Tu sesión no incluye permiso para eliminar.",
                    op, kind, decision);
            }
        }

        var deviceId = await _deviceIds.GetOrCreateAsync(ct);
        var newClientOpId = Guid.NewGuid().ToString("N");
        PendingOperation newOp;

        if (kind == OfflineConflictKind.DeleteUpdate)
        {
            // Keep Local Delete: confirmar eliminación con stamp servidor actual.
            var deletePayload = new DeleteRecordPayload { EntityId = serverId.Value };
            newOp = BuildPending(
                OfflineOperationType.DeleteRecord,
                JsonSerializer.Serialize(deletePayload, JsonOptions),
                newClientOpId,
                session.UserId,
                deviceId,
                record,
                serverId.Value,
                expectedStamp,
                now);

            record.IsDeleted = true;
            record.UpdatedAtUtc = now;
            record.SyncStatus = LocalSyncStatus.PendingSync;
            // ConcurrencyStamp local se mantiene; ExpectedConcurrencyStamp = stamp servidor.
        }
        else
        {
            // UpdateUpdate: Keep Local (valores locales) o Edit&Retry (campos nuevos).
            ConflictEditFields fields;
            if (decision == ConflictResolutionDecision.EditAndRetry)
            {
                if (editFields is null)
                {
                    return Fail(ConflictResolutionOutcome.Rejected,
                        "Faltan campos editados para Edit & Retry.", op, kind, decision);
                }

                fields = editFields;
            }
            else
            {
                fields = new ConflictEditFields
                {
                    Telar = record.Telar,
                    Neps = record.Neps,
                    Tela = record.Tela,
                    LoteTrama = record.LoteTrama,
                    Turno = record.Turno,
                    Operario = record.Operario,
                    LineaProduccion = record.LineaProduccion,
                    Observacion = record.Observacion
                };
            }

            ValidateBusinessFields(fields);
            var lote = NormalizeLote(fields.LoteTrama);
            var updatePayload = new UpdateRecordPayload
            {
                EntityId = serverId.Value,
                Telar = fields.Telar.Trim(),
                Neps = fields.Neps,
                Tela = fields.Tela?.Trim() ?? string.Empty,
                LoteTrama = lote,
                Turno = fields.Turno?.Trim() ?? string.Empty,
                Operario = fields.Operario?.Trim() ?? string.Empty,
                LineaProduccion = fields.LineaProduccion?.Trim() ?? string.Empty,
                Observacion = fields.Observacion?.Trim() ?? string.Empty
            };

            newOp = BuildPending(
                OfflineOperationType.UpdateRecord,
                JsonSerializer.Serialize(updatePayload, JsonOptions),
                newClientOpId,
                session.UserId,
                deviceId,
                record,
                serverId.Value,
                expectedStamp,
                now);

            record.Telar = updatePayload.Telar;
            record.Neps = updatePayload.Neps;
            record.Tela = updatePayload.Tela;
            record.LoteTrama = updatePayload.LoteTrama;
            record.Turno = updatePayload.Turno;
            record.Operario = updatePayload.Operario;
            record.LineaProduccion = updatePayload.LineaProduccion;
            record.Observacion = updatePayload.Observacion;
            record.IsDeleted = false;
            record.UpdatedAtUtc = now;
            record.SyncStatus = LocalSyncStatus.PendingSync;
            // ConcurrencyStamp local se mantiene hasta Accepted (Expected = stamp servidor).
        }

        var marker = decision == ConflictResolutionDecision.EditAndRetry
            ? ResolutionMarkerEditRetry
            : ResolutionMarkerKeepLocal;
        op.Status = PendingOperationStatus.Cancelled;
        op.LastError =
            $"{marker};by={session.UserId};at={now:O};kind={kind};newClientOp={newClientOpId}";
        op.LastAttemptAtUtc = now;

        _db.PendingOperations.Add(newOp);

        var level = AlertEvaluator.GetLevel(record.Neps);
        return new ConflictResolutionResult
        {
            Outcome = ConflictResolutionOutcome.Success,
            Message = decision == ConflictResolutionDecision.EditAndRetry
                ? "Nueva actualización encolada (Edit & Retry). Pendiente de sincronización."
                : kind == OfflineConflictKind.DeleteUpdate
                    ? "Nueva eliminación encolada sobre la versión actual del servidor."
                    : "Nueva actualización encolada con tus cambios. Pendiente de sincronización.",
            Kind = kind,
            Decision = decision,
            ClosedOperationId = op.Id,
            ClosedOperation = op,
            NewOperationId = newOp.Id,
            NewOperation = newOp,
            NewClientOperationId = newClientOpId,
            ExpectedConcurrencyStamp = expectedStamp,
            Record = record,
            QualityLabel = level.ToDisplayLabel()
        };
    }

    private static PendingOperation BuildPending(
        OfflineOperationType type,
        string payloadJson,
        string clientOperationId,
        string userId,
        string deviceId,
        LocalNepRecord record,
        Guid serverId,
        string expectedStamp,
        DateTime now) =>
        new()
        {
            Id = Guid.NewGuid(),
            ClientOperationId = clientOperationId,
            OperationType = type,
            PayloadJson = payloadJson,
            ProtocolVersion = OfflineStoreConstants.ProtocolVersion,
            CreatedAtUtc = now,
            AttemptCount = 0,
            Status = PendingOperationStatus.Pending,
            UserId = userId,
            DeviceId = deviceId,
            CaptureSessionId = record.CaptureSessionId,
            LocalNepRecordId = record.Id,
            TargetServerRecordId = serverId,
            ExpectedConcurrencyStamp = expectedStamp,
            LastError = $"Resolution;fromConflict;snapshot={SnapshotJsonVersion}"
        };

    private ConflictResolutionActions ComputeAllowedActions(
        OfflineConflictKind kind,
        LocalSession session,
        PendingOperation op)
    {
        if (op.Status != PendingOperationStatus.Conflict || !CanAccessOperation(session, op))
        {
            return ConflictResolutionActions.None;
        }

        // Keep Server: autor o SeesAll (no eleva privilegios servidor).
        var actions = ConflictResolutionActions.KeepServer;

        switch (kind)
        {
            case OfflineConflictKind.UpdateUpdate:
                if (_sessions.HasPermission(session, OfflineStoreConstants.EditRecordsPermission))
                {
                    actions |= ConflictResolutionActions.KeepLocal | ConflictResolutionActions.EditAndRetry;
                }

                break;
            case OfflineConflictKind.DeleteUpdate:
                if (_sessions.HasPermission(session, OfflineStoreConstants.DeleteRecordsPermission))
                {
                    actions |= ConflictResolutionActions.KeepLocal;
                }

                break;
            case OfflineConflictKind.UpdateDelete:
            case OfflineConflictKind.DeleteDelete:
                // Solo Keep Server (aceptar eliminación). Sin Keep Local / Edit&Retry / Restore.
                break;
        }

        return actions;
    }

    private async Task<PendingOperation?> LoadConflictOpAsync(
        Guid operationId,
        LocalSession session,
        bool requireConflictStatus,
        CancellationToken ct)
    {
        var op = await _db.PendingOperations.AsNoTracking()
            .Include(o => o.LocalNepRecord)
            .FirstOrDefaultAsync(o => o.Id == operationId, ct);
        if (op is null || !CanAccessOperation(session, op))
        {
            return null;
        }

        if (requireConflictStatus && op.Status != PendingOperationStatus.Conflict)
        {
            return null;
        }

        return op;
    }

    private static bool CanAccessOperation(LocalSession session, PendingOperation op) =>
        string.Equals(op.UserId, session.UserId, StringComparison.OrdinalIgnoreCase)
        || SeesAll(session);

    private static bool SeesAll(LocalSession session) =>
        SeesAllRoleCodes.Contains(session.RoleCode ?? string.Empty);

    private static bool IsResolutionMarker(string? lastError) =>
        !string.IsNullOrWhiteSpace(lastError)
        && (lastError.StartsWith(ResolutionMarkerKeepServer, StringComparison.Ordinal)
            || lastError.StartsWith(ResolutionMarkerKeepLocal, StringComparison.Ordinal)
            || lastError.StartsWith(ResolutionMarkerEditRetry, StringComparison.Ordinal));

    private static string? ResolveServerStamp(PendingOperation op)
    {
        if (!string.IsNullOrWhiteSpace(op.ConflictServerConcurrencyStamp))
        {
            return op.ConflictServerConcurrencyStamp;
        }

        if (ConflictKindClassifier.TryParseActiveSnapshot(op.ConflictServerSnapshotJson, out var snap)
            && !string.IsNullOrWhiteSpace(snap.ConcurrencyStamp))
        {
            return snap.ConcurrencyStamp;
        }

        return null;
    }

    private static void ApplyActiveSnapshotToRecord(
        LocalNepRecord record,
        ClientNepRecordSnapshot snap,
        DateTime now)
    {
        record.Telar = snap.Telar ?? string.Empty;
        record.Neps = snap.Neps;
        record.Tela = snap.Tela ?? string.Empty;
        record.LoteTrama = string.IsNullOrWhiteSpace(snap.LoteTrama)
            ? NepsConstants.LoteTramaPrefix
            : snap.LoteTrama.Trim().ToUpperInvariant();
        record.Turno = snap.Turno ?? string.Empty;
        record.Operario = snap.Operario ?? string.Empty;
        record.LineaProduccion = snap.LineaProduccion ?? string.Empty;
        record.Observacion = snap.Observacion ?? string.Empty;
        record.ConcurrencyStamp = snap.ConcurrencyStamp;
        if (snap.Id != Guid.Empty)
        {
            record.ServerRecordId = snap.Id;
        }

        record.UpdatedAtUtc = snap.UpdatedAtUtc ?? now;
        // QualityLabel no se almacena; se deriva de Neps vía AlertEvaluator.
    }

    private static ConflictFieldSnapshot BuildLocalSnapshot(LocalNepRecord? record, PendingOperation op)
    {
        if (record is null)
        {
            return new ConflictFieldSnapshot
            {
                IsDeleted = op.OperationType == OfflineOperationType.DeleteRecord
            };
        }

        var level = AlertEvaluator.GetLevel(record.Neps);
        return new ConflictFieldSnapshot
        {
            Telar = record.Telar,
            Neps = record.Neps,
            Tela = record.Tela,
            LoteTrama = record.LoteTrama,
            Turno = record.Turno,
            Operario = record.Operario,
            LineaProduccion = record.LineaProduccion,
            Observacion = record.Observacion,
            QualityLabel = level.ToDisplayLabel(),
            IsDeleted = record.IsDeleted || op.OperationType == OfflineOperationType.DeleteRecord,
            ConcurrencyStamp = record.ConcurrencyStamp,
            UpdatedAtUtc = record.UpdatedAtUtc
        };
    }

    private static ConflictFieldSnapshot FromActiveSnapshot(ClientNepRecordSnapshot snap)
    {
        var level = AlertEvaluator.GetLevel(snap.Neps);
        return new ConflictFieldSnapshot
        {
            Telar = snap.Telar,
            Neps = snap.Neps,
            Tela = snap.Tela,
            LoteTrama = snap.LoteTrama,
            Turno = snap.Turno,
            Operario = snap.Operario,
            LineaProduccion = snap.LineaProduccion,
            Observacion = snap.Observacion,
            QualityLabel = level.ToDisplayLabel(),
            IsDeleted = false,
            ConcurrencyStamp = snap.ConcurrencyStamp,
            UpdatedAtUtc = snap.UpdatedAtUtc
        };
    }

    private static IReadOnlyList<ConflictFieldDiff> BuildDiffs(
        ConflictFieldSnapshot local,
        ConflictFieldSnapshot? server,
        bool serverDeleted,
        OfflineConflictKind kind)
    {
        if (serverDeleted)
        {
            return
            [
                new ConflictFieldDiff
                {
                    FieldName = "Estado",
                    LocalValue = local.IsDeleted ? "Eliminación pendiente / valores locales" : FormatLocal(local),
                    ServerValue = "Eliminado"
                }
            ];
        }

        if (server is null)
        {
            return Array.Empty<ConflictFieldDiff>();
        }

        if (kind == OfflineConflictKind.DeleteUpdate)
        {
            return
            [
                new ConflictFieldDiff
                {
                    FieldName = "Intención",
                    LocalValue = "Eliminar",
                    ServerValue = FormatLocal(server)
                }
            ];
        }

        var diffs = new List<ConflictFieldDiff>();
        void Add(string name, string l, string s)
        {
            if (!string.Equals(l, s, StringComparison.Ordinal))
            {
                diffs.Add(new ConflictFieldDiff { FieldName = name, LocalValue = l, ServerValue = s });
            }
        }

        Add("Telar", local.Telar, server.Telar);
        Add("Neps", local.Neps.ToString("G"), server.Neps.ToString("G"));
        Add("Calidad", local.QualityLabel, server.QualityLabel);
        Add("Tela", local.Tela, server.Tela);
        Add("Lote", local.LoteTrama, server.LoteTrama);
        Add("Turno", local.Turno, server.Turno);
        Add("Operario", local.Operario, server.Operario);
        Add("Línea", local.LineaProduccion, server.LineaProduccion);
        Add("Observación", local.Observacion, server.Observacion);
        return diffs;
    }

    private static string FormatLocal(ConflictFieldSnapshot s) =>
        $"Telar {s.Telar} · Neps {s.Neps} · {s.QualityLabel}";

    private static string NormalizeLote(string? loteTrama) =>
        string.IsNullOrWhiteSpace(loteTrama)
            ? NepsConstants.LoteTramaPrefix
            : loteTrama.Trim().ToUpperInvariant();

    private static void ValidateBusinessFields(ConflictEditFields fields)
    {
        if (string.IsNullOrWhiteSpace(fields.Telar))
        {
            throw new ArgumentException("El telar es obligatorio.", nameof(fields));
        }

        if (fields.Neps <= 0)
        {
            throw new ArgumentException("Neps debe ser mayor que cero.", nameof(fields));
        }
    }

    private static ConflictResolutionResult Fail(
        ConflictResolutionOutcome outcome,
        string message,
        PendingOperation? op = null,
        OfflineConflictKind kind = OfflineConflictKind.Unknown,
        ConflictResolutionDecision? decision = null) =>
        new()
        {
            Outcome = outcome,
            Message = message,
            Kind = kind,
            Decision = decision,
            ClosedOperationId = op?.Id,
            ClosedOperation = op
        };
}
