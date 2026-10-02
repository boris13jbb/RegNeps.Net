using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RegNeps.Domain.Sync;
using RegNeps.OfflineStore.Abstractions;
using RegNeps.OfflineStore.Entities;
using RegNeps.OfflineStore.Enums;
using RegNeps.OfflineStore.Models;
using RegNeps.OfflineStore.Services;
using RegNeps.OfflineStore.Sync.Contracts;

namespace RegNeps.OfflineStore.Sync;

/// <summary>
/// Motor Push/Pull offline-first. Determinista, reanudable, sin Last-Write-Wins.
/// Autorización real: cookie de servidor; PermissionsCsv solo UX.
/// </summary>
public sealed class SyncEngine : ISyncEngine
{
    public const int PushBatchSize = 50;

    private readonly LocalSyncDbContext _db;
    private readonly OfflineSessionService _sessions;
    private readonly IDeviceIdStore _deviceIds;
    private readonly ISyncApiClient _api;
    private readonly ISyncAuthCookieProvider _cookies;

    public SyncEngine(
        LocalSyncDbContext db,
        OfflineSessionService sessions,
        IDeviceIdStore deviceIds,
        ISyncApiClient api,
        ISyncAuthCookieProvider cookies)
    {
        _db = db;
        _sessions = sessions;
        _deviceIds = deviceIds;
        _api = api;
        _cookies = cookies;
    }

    public async Task<SyncRunResult> SyncAsync(CancellationToken ct = default)
    {
        var result = new SyncRunResult();
        var session = await _sessions.GetValidSessionAsync(ct);
        if (session is null)
        {
            result.SessionMissingOrExpired = true;
            result.Message = "No hay LocalSession válida. Inicie sesión online primero.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(session.ServerBaseUrl))
        {
            result.SessionMissingOrExpired = true;
            result.Message = "LocalSession sin ServerBaseUrl.";
            return result;
        }

        var cookie = await _cookies.GetCookieHeaderAsync(session.ServerBaseUrl!, ct);
        if (string.IsNullOrWhiteSpace(cookie))
        {
            result.AuthRequired = true;
            result.Message = "No hay cookie de autenticación del WebView. Vuelva a iniciar sesión online.";
            return result;
        }

        result.Started = true;
        var deviceId = await _deviceIds.GetOrCreateAsync(ct);

        try
        {
            await PushOutboxAsync(session, deviceId, cookie!, result, ct);
        }
        catch (SyncTransportException ex) when (ex.Kind == SyncTransportFailureKind.Unauthorized)
        {
            result.AuthRequired = true;
            result.Message = ex.Message;
            await TouchSyncStateErrorAsync(deviceId, ex.Message, "Unauthorized", ct);
            return result;
        }
        catch (SyncTransportException ex)
        {
            result.Message = ex.Message;
            await TouchSyncStateErrorAsync(deviceId, ex.Message, ex.Kind.ToString(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fallo al persistir resultado de Push: Outbox permanece Pending (TX rollback).
            result.Message = ex.Message;
            await TouchSyncStateErrorAsync(deviceId, ex.Message, "PushPersistFailed", ct);
        }

        try
        {
            await PullAllPagesAsync(session, deviceId, cookie!, result, ct);
            result.PullCompleted = true;
        }
        catch (SyncTransportException ex) when (ex.Kind == SyncTransportFailureKind.Unauthorized)
        {
            result.AuthRequired = true;
            result.Message = ex.Message;
            await TouchSyncStateErrorAsync(deviceId, ex.Message, "Unauthorized", ct);
            return result;
        }
        catch (SyncTransportException ex)
        {
            result.Message = string.IsNullOrEmpty(result.Message)
                ? ex.Message
                : result.Message + " | " + ex.Message;
            await TouchSyncStateErrorAsync(deviceId, ex.Message, ex.Kind.ToString(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fallo al aplicar página: cursor no avanzó (rollback). Reintentable.
            result.Message = string.IsNullOrEmpty(result.Message)
                ? ex.Message
                : result.Message + " | " + ex.Message;
            await TouchSyncStateErrorAsync(deviceId, ex.Message, "PullApplyFailed", ct);
        }

        var state = await EnsureSyncStateAsync(deviceId, ct);
        result.CursorAfter = state.LastPulledSequence;
        if (result.PullCompleted)
        {
            state.LastSuccessfulSyncUtc = DateTime.UtcNow;
            state.LastError = null;
            state.LastConnectivityStatus = "Online";
            state.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return result;
    }

    private async Task PushOutboxAsync(
        LocalSession session,
        string deviceId,
        string cookie,
        SyncRunResult result,
        CancellationToken ct)
    {
        result.SkippedOtherUser = await _db.PendingOperations.AsNoTracking()
            .CountAsync(o =>
                o.Status == PendingOperationStatus.Pending
                && o.UserId != session.UserId, ct);

        // Una pasada explícita por SyncAsync: no reenviar Transient en el mismo ciclo.
        var alreadyAttempted = new List<Guid>();

        while (true)
        {
            var skip = alreadyAttempted;
            var batch = await _db.PendingOperations
                .Include(o => o.LocalNepRecord)
                .Where(o =>
                    o.Status == PendingOperationStatus.Pending
                    && o.UserId == session.UserId
                    && !skip.Contains(o.Id))
                .OrderBy(o => o.CreatedAtUtc)
                .Take(PushBatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            foreach (var op in batch)
            {
                alreadyAttempted.Add(op.Id);
            }

            var request = new ClientSyncPushRequest
            {
                ProtocolVersion = SyncConstants.ProtocolVersion,
                DeviceId = deviceId,
                Operations = batch.Select(BuildOperationDto).ToList()
            };

            ClientSyncPushResponse response;
            try
            {
                response = await _api.PushAsync(session.ServerBaseUrl!, cookie, request, ct);
            }
            catch (SyncTransportException ex) when (
                ex.Kind is SyncTransportFailureKind.Network
                    or SyncTransportFailureKind.Timeout
                    or SyncTransportFailureKind.ServerError)
            {
                // Transient a nivel de lote: incrementar intentos, seguir Pending.
                foreach (var op in batch)
                {
                    op.AttemptCount++;
                    op.LastAttemptAtUtc = DateTime.UtcNow;
                    op.LastError = Truncate(ex.Message, 2000);
                    op.LastServerErrorCode = "TRANSIENT_TRANSPORT";
                    result.PushedTransient++;
                }

                await _db.SaveChangesAsync(ct);
                throw;
            }

            var byId = response.Results.ToDictionary(
                r => r.ClientOperationId,
                StringComparer.Ordinal);

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                foreach (var op in batch)
                {
                    if (!byId.TryGetValue(op.ClientOperationId, out var serverResult))
                    {
                        op.AttemptCount++;
                        op.LastAttemptAtUtc = DateTime.UtcNow;
                        op.LastError = "El servidor no devolvió resultado para esta operación.";
                        op.LastServerErrorCode = "MISSING_RESULT";
                        op.Status = PendingOperationStatus.SyncError;
                        result.PushedSyncError++;
                        continue;
                    }

                    ApplyPushResult(op, serverResult, result);
                }

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                _db.ChangeTracker.Clear();
                throw;
            }

        }
    }

    private static ClientSyncOperationDto BuildOperationDto(PendingOperation op)
    {
        var dto = new ClientSyncOperationDto
        {
            ClientOperationId = op.ClientOperationId,
            OperationType = MapOperationType(op.OperationType),
            CaptureSessionId = op.CaptureSessionId,
            ExpectedConcurrencyStamp = ResolveExpectedStamp(op),
            ClientCreatedAtUtc = op.CreatedAtUtc
        };

        using var doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(op.PayloadJson) ? "{}" : op.PayloadJson);
        dto.Payload = BuildWirePayload(op, doc.RootElement);
        return dto;
    }

    private static string? ResolveExpectedStamp(PendingOperation op)
    {
        if (!string.IsNullOrWhiteSpace(op.ExpectedConcurrencyStamp))
        {
            return op.ExpectedConcurrencyStamp;
        }

        return op.LocalNepRecord?.ConcurrencyStamp;
    }

    private static JsonElement BuildWirePayload(PendingOperation op, JsonElement stored)
    {
        if (op.OperationType == OfflineOperationType.CreateRecord)
        {
            // SyncCreateRecordPayload: solo campos de negocio (sin ClientOperationId anidado).
            CreateRecordPayload? local = null;
            try
            {
                local = stored.Deserialize<CreateRecordPayload>(ClientSyncJson.Options);
            }
            catch (JsonException)
            {
                return stored.Clone();
            }

            var wire = new
            {
                telar = local?.Telar ?? "",
                neps = local?.Neps ?? 0,
                tela = local?.Tela ?? "",
                loteTrama = local?.LoteTrama ?? "",
                turno = local?.Turno ?? "",
                operario = local?.Operario ?? "",
                lineaProduccion = local?.LineaProduccion ?? "",
                observacion = local?.Observacion ?? ""
            };
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(wire, ClientSyncJson.Options));
            return doc.RootElement.Clone();
        }

        if (op.OperationType is OfflineOperationType.UpdateRecord or OfflineOperationType.DeleteRecord)
        {
            // Asegurar EntityId = ServerRecordId ?? LocalId / TargetServerRecordId.
            var entityId = op.TargetServerRecordId
                           ?? op.LocalNepRecord?.ServerRecordId
                           ?? op.LocalNepRecordId
                           ?? Guid.Empty;
            if (stored.ValueKind == JsonValueKind.Object)
            {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    var hasEntity = false;
                    foreach (var prop in stored.EnumerateObject())
                    {
                        if (prop.NameEquals("entityId") || prop.NameEquals("EntityId"))
                        {
                            writer.WriteString("entityId", entityId);
                            hasEntity = true;
                        }
                        else
                        {
                            prop.WriteTo(writer);
                        }
                    }

                    if (!hasEntity)
                    {
                        writer.WriteString("entityId", entityId);
                    }

                    writer.WriteEndObject();
                }

                using var doc = JsonDocument.Parse(stream.ToArray());
                return doc.RootElement.Clone();
            }
        }

        return stored.Clone();
    }

    private static string MapOperationType(OfflineOperationType type) => type switch
    {
        OfflineOperationType.CreateRecord => SyncConstants.OperationCreateRecord,
        OfflineOperationType.UpdateRecord => SyncConstants.OperationUpdateRecord,
        OfflineOperationType.DeleteRecord => SyncConstants.OperationDeleteRecord,
        OfflineOperationType.ApplyCorrective => SyncConstants.OperationApplyCorrective,
        _ => type.ToString()
    };

    private static void ApplyPushResult(
        PendingOperation op,
        ClientSyncOperationResultDto serverResult,
        SyncRunResult result)
    {
        op.AttemptCount++;
        op.LastAttemptAtUtc = DateTime.UtcNow;
        op.LastServerErrorCode = serverResult.ErrorCode;
        op.LastError = Truncate(serverResult.Message, 2000);

        var name = serverResult.Result?.Trim() ?? string.Empty;

        if (string.Equals(name, ClientSyncResultNames.Accepted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, ClientSyncResultNames.Duplicate, StringComparison.OrdinalIgnoreCase))
        {
            op.Status = PendingOperationStatus.Synced;
            op.LastError = null;
            if (string.Equals(name, ClientSyncResultNames.Accepted, StringComparison.OrdinalIgnoreCase))
            {
                result.PushedAccepted++;
            }
            else
            {
                result.PushedDuplicate++;
            }

            ApplyAcceptedToLocalRecord(op, serverResult);
            return;
        }

        if (string.Equals(name, ClientSyncResultNames.Conflict, StringComparison.OrdinalIgnoreCase))
        {
            op.Status = PendingOperationStatus.Conflict;
            op.ConflictServerConcurrencyStamp =
                serverResult.ServerConcurrencyStamp ?? serverResult.ConcurrencyStamp;
            if (serverResult.ServerSnapshot is { } snap
                && snap.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
            {
                op.ConflictServerSnapshotJson = snap.GetRawText();
            }

            if (op.LocalNepRecord is not null)
            {
                op.LocalNepRecord.SyncStatus = LocalSyncStatus.Conflict;
            }

            result.PushedConflict++;
            return;
        }

        if (string.Equals(name, ClientSyncResultNames.TransientError, StringComparison.OrdinalIgnoreCase))
        {
            op.Status = PendingOperationStatus.Pending;
            result.PushedTransient++;
            return;
        }

        // Forbidden, Invalid, CLIENT_OPERATION_REUSED, ENTITY_DELETED, etc.
        op.Status = PendingOperationStatus.SyncError;
        if (string.Equals(serverResult.ErrorCode, "ENTITY_DELETED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(serverResult.ErrorCode, "ENTITY_NOT_FOUND", StringComparison.OrdinalIgnoreCase))
        {
            op.Status = PendingOperationStatus.Conflict;
            if (op.LocalNepRecord is not null)
            {
                op.LocalNepRecord.IsDeleted = true;
                op.LocalNepRecord.SyncStatus = LocalSyncStatus.Conflict;
            }

            result.PushedConflict++;
            return;
        }

        if (op.LocalNepRecord is not null
            && op.LocalNepRecord.SyncStatus == LocalSyncStatus.PendingSync)
        {
            // No marcar Synced; dejar PendingSync o Conflict UX.
            op.LocalNepRecord.SyncStatus = LocalSyncStatus.Conflict;
        }

        result.PushedSyncError++;
    }

    private static void ApplyAcceptedToLocalRecord(
        PendingOperation op,
        ClientSyncOperationResultDto serverResult)
    {
        var record = op.LocalNepRecord;
        if (record is null)
        {
            return;
        }

        if (serverResult.EntityId is Guid eid && eid != Guid.Empty)
        {
            record.ServerRecordId = eid;
            op.TargetServerRecordId = eid;
        }

        if (!string.IsNullOrWhiteSpace(serverResult.ConcurrencyStamp))
        {
            record.ConcurrencyStamp = serverResult.ConcurrencyStamp;
            op.ExpectedConcurrencyStamp = serverResult.ConcurrencyStamp;
        }

        record.UpdatedAtUtc = DateTime.UtcNow;
        record.SyncStatus = LocalSyncStatus.Synced;
        record.IsDeleted = op.OperationType == OfflineOperationType.DeleteRecord;
    }

    private async Task PullAllPagesAsync(
        LocalSession session,
        string deviceId,
        string cookie,
        SyncRunResult result,
        CancellationToken ct)
    {
        var state = await EnsureSyncStateAsync(deviceId, ct);
        var safety = 0;
        const int maxPages = 10_000;

        while (safety++ < maxPages)
        {
            var cursorBefore = state.LastPulledSequence;
            var pullRequest = new ClientSyncPullRequest
            {
                ProtocolVersion = SyncConstants.ProtocolVersion,
                DeviceId = deviceId,
                Cursor = cursorBefore,
                PageSize = SyncConstants.DefaultPageSize
            };

            var page = await _api.PullAsync(session.ServerBaseUrl!, cookie, pullRequest, ct);

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                // Releer state en la TX.
                state = await _db.SyncStates.FirstAsync(x => x.Id == 1, ct);
                if (state.LastPulledSequence != cursorBefore)
                {
                    // Otra corrida avanzó el cursor; abortar esta página.
                    await tx.RollbackAsync(ct);
                    return;
                }

                foreach (var change in page.Changes.OrderBy(c => c.Sequence))
                {
                    if (string.Equals(change.ChangeType, SyncConstants.ChangeRecordUpserted, StringComparison.Ordinal))
                    {
                        await ApplyUpsertAsync(change, session.UserId, result, ct);
                    }
                    else if (string.Equals(change.ChangeType, SyncConstants.ChangeRecordDeleted, StringComparison.Ordinal))
                    {
                        await ApplyDeleteAsync(change, session.UserId, result, ct);
                    }
                }

                // Avanzar cursor solo tras aplicar la página completa.
                state.LastPulledSequence = page.NextCursor;
                state.UpdatedAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                _db.ChangeTracker.Clear();
                throw;
            }

            result.CursorAfter = page.NextCursor;
            if (!page.HasMore || page.Changes.Count == 0)
            {
                break;
            }
        }
    }

    private async Task ApplyUpsertAsync(
        ClientSyncChangeDto change,
        string sessionUserId,
        SyncRunResult result,
        CancellationToken ct)
    {
        ClientNepRecordSnapshot? snap;
        try
        {
            // Reparse por GetRawText: JsonElement del wire puede quedar inválido tras dispose del documento.
            snap = JsonSerializer.Deserialize<ClientNepRecordSnapshot>(
                change.Payload.GetRawText(), ClientSyncJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Payload RecordUpserted inválido Sequence={change.Sequence}.", ex);
        }

        if (snap is null || snap.Id == Guid.Empty)
        {
            throw new InvalidOperationException($"Snapshot sin Id Sequence={change.Sequence}.");
        }

        var local = await FindLocalByServerOrClientOpAsync(snap.Id, snap.ClientOperationId, ct);
        var pendingForEntity = await ListPendingForEntityAsync(snap.Id, snap.ClientOperationId, ct);

        // Misma operación local (Create/Update ya aceptada o aún Pending con mismo ClientOperationId).
        var sameOp = pendingForEntity.FirstOrDefault(p =>
            string.Equals(p.ClientOperationId, snap.ClientOperationId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(snap.ClientOperationId));

        var otherPending = pendingForEntity
            .Where(p => p.Status == PendingOperationStatus.Pending
                        && (sameOp is null || p.Id != sameOp.Id))
            .ToList();

        if (local is null)
        {
            // Nuevo remoto: solo si pertenece al usuario de sesión (o sin owner).
            if (!string.IsNullOrWhiteSpace(snap.OwnerUserId)
                && !string.Equals(snap.OwnerUserId, sessionUserId, StringComparison.OrdinalIgnoreCase))
            {
                // El servidor ya filtró autorización; aún así asociamos UserId del owner.
            }

            local = new LocalNepRecord
            {
                Id = snap.Id,
                ClientOperationId = string.IsNullOrWhiteSpace(snap.ClientOperationId)
                    ? $"pull:{snap.Id:N}"
                    : snap.ClientOperationId!,
                UserId = string.IsNullOrWhiteSpace(snap.OwnerUserId) ? sessionUserId : snap.OwnerUserId!,
                ServerRecordId = snap.Id,
                SyncStatus = LocalSyncStatus.Synced,
                CreatedAtUtc = snap.CreatedAtUtc == default ? DateTime.UtcNow : snap.CreatedAtUtc
            };
            _db.LocalNepRecords.Add(local);
            ApplySnapshotFields(local, snap, overwriteBusinessFields: true);
            result.PulledUpserts++;
            return;
        }

        if (sameOp is not null
            && sameOp.Status is PendingOperationStatus.Synced or PendingOperationStatus.Pending)
        {
            // Resultado de nuestra operación: actualizar réplica, sin conflicto falso.
            ApplySnapshotFields(local, snap, overwriteBusinessFields: true);
            local.ServerRecordId = snap.Id;
            local.SyncStatus = LocalSyncStatus.Synced;
            local.IsDeleted = false;
            if (sameOp.Status == PendingOperationStatus.Pending)
            {
                sameOp.Status = PendingOperationStatus.Synced;
                sameOp.TargetServerRecordId = snap.Id;
                sameOp.ExpectedConcurrencyStamp = snap.ConcurrencyStamp;
            }

            result.PulledUpserts++;
            return;
        }

        if (otherPending.Count > 0)
        {
            // Hay edición local pendiente distinta: NO LWW de campos de negocio.
            // Actualizar stamp para el próximo Push / Conflict esperado.
            local.ConcurrencyStamp = snap.ConcurrencyStamp;
            local.UpdatedAtUtc = snap.UpdatedAtUtc ?? local.UpdatedAtUtc;
            local.ServerRecordId ??= snap.Id;
            local.IsDeleted = false;
            foreach (var p in otherPending)
            {
                p.ExpectedConcurrencyStamp = snap.ConcurrencyStamp;
            }

            result.PulledUpserts++;
            return;
        }

        ApplySnapshotFields(local, snap, overwriteBusinessFields: true);
        local.ServerRecordId = snap.Id;
        local.SyncStatus = LocalSyncStatus.Synced;
        local.IsDeleted = false;
        result.PulledUpserts++;
    }

    private async Task ApplyDeleteAsync(
        ClientSyncChangeDto change,
        string sessionUserId,
        SyncRunResult result,
        CancellationToken ct)
    {
        ClientNepRecordDeletedSnapshot? tomb;
        try
        {
            tomb = JsonSerializer.Deserialize<ClientNepRecordDeletedSnapshot>(
                change.Payload.GetRawText(), ClientSyncJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Payload RecordDeleted inválido Sequence={change.Sequence}.", ex);
        }

        var entityId = change.EntityId != Guid.Empty ? change.EntityId : tomb?.Id ?? Guid.Empty;
        if (entityId == Guid.Empty)
        {
            throw new InvalidOperationException($"Tombstone sin EntityId Sequence={change.Sequence}.");
        }

        var local = await FindLocalByServerOrClientOpAsync(entityId, clientOperationId: null, ct);
        if (local is null)
        {
            // Crear marcador tombstone para evitar reaparición.
            local = new LocalNepRecord
            {
                Id = entityId,
                ClientOperationId = $"deleted:{entityId:N}",
                UserId = string.IsNullOrWhiteSpace(tomb?.OwnerUserId) ? sessionUserId : tomb!.OwnerUserId,
                ServerRecordId = entityId,
                CreatedAtUtc = tomb?.DeletedAtUtc ?? DateTime.UtcNow,
                IsDeleted = true,
                SyncStatus = LocalSyncStatus.Synced,
                ConcurrencyStamp = tomb?.LastConcurrencyStamp
            };
            _db.LocalNepRecords.Add(local);
        }
        else
        {
            local.IsDeleted = true;
            local.ServerRecordId ??= entityId;
            local.SyncStatus = LocalSyncStatus.Synced;
            if (!string.IsNullOrWhiteSpace(tomb?.LastConcurrencyStamp))
            {
                local.ConcurrencyStamp = tomb.LastConcurrencyStamp;
            }
        }

        var pendings = await ListPendingForEntityAsync(entityId, clientOperationId: null, ct);
        foreach (var p in pendings.Where(p => p.Status == PendingOperationStatus.Pending))
        {
            p.Status = PendingOperationStatus.Conflict;
            p.LastServerErrorCode = "ENTITY_DELETED";
            p.LastError = "El registro fue eliminado en el servidor.";
            p.ConflictServerConcurrencyStamp = tomb?.LastConcurrencyStamp;
        }

        result.PulledDeletes++;
    }

    private async Task<LocalNepRecord?> FindLocalByServerOrClientOpAsync(
        Guid serverEntityId,
        string? clientOperationId,
        CancellationToken ct)
    {
        var byServer = await _db.LocalNepRecords
            .FirstOrDefaultAsync(r =>
                r.Id == serverEntityId || r.ServerRecordId == serverEntityId, ct);
        if (byServer is not null)
        {
            return byServer;
        }

        if (string.IsNullOrWhiteSpace(clientOperationId))
        {
            return null;
        }

        return await _db.LocalNepRecords
            .FirstOrDefaultAsync(r => r.ClientOperationId == clientOperationId, ct);
    }

    private async Task<List<PendingOperation>> ListPendingForEntityAsync(
        Guid serverEntityId,
        string? clientOperationId,
        CancellationToken ct)
    {
        var q = _db.PendingOperations
            .Include(o => o.LocalNepRecord)
            .Where(o =>
                o.TargetServerRecordId == serverEntityId
                || o.LocalNepRecordId == serverEntityId
                || (o.LocalNepRecord != null && o.LocalNepRecord.ServerRecordId == serverEntityId)
                || (o.LocalNepRecord != null && o.LocalNepRecord.Id == serverEntityId));

        var list = await q.ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(clientOperationId))
        {
            var byOp = await _db.PendingOperations
                .Include(o => o.LocalNepRecord)
                .Where(o => o.ClientOperationId == clientOperationId)
                .ToListAsync(ct);
            foreach (var op in byOp)
            {
                if (list.All(x => x.Id != op.Id))
                {
                    list.Add(op);
                }
            }
        }

        return list;
    }

    private static void ApplySnapshotFields(
        LocalNepRecord local,
        ClientNepRecordSnapshot snap,
        bool overwriteBusinessFields)
    {
        if (overwriteBusinessFields)
        {
            local.Telar = snap.Telar ?? string.Empty;
            local.Neps = snap.Neps;
            local.Tela = snap.Tela ?? string.Empty;
            local.LoteTrama = snap.LoteTrama ?? string.Empty;
            local.Turno = snap.Turno ?? string.Empty;
            local.Operario = snap.Operario ?? string.Empty;
            local.LineaProduccion = snap.LineaProduccion ?? string.Empty;
            local.Observacion = snap.Observacion ?? string.Empty;
            local.CaptureSessionId = snap.CaptureSessionId;
            local.AccionCorrectiva = snap.AccionCorrectiva ?? string.Empty;
            local.ResponsableRevision = snap.ResponsableRevision ?? string.Empty;
            local.RevisadoPorSupervisor = snap.RevisadoPorSupervisor;
            local.FechaRevisionUtc = snap.FechaRevisionUtc;
            if (snap.CreatedAtUtc != default)
            {
                local.CreatedAtUtc = snap.CreatedAtUtc;
            }
        }

        local.ConcurrencyStamp = snap.ConcurrencyStamp;
        local.UpdatedAtUtc = snap.UpdatedAtUtc ?? DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(snap.ClientOperationId)
            && local.ClientOperationId.StartsWith("pull:", StringComparison.Ordinal))
        {
            local.ClientOperationId = snap.ClientOperationId!;
        }
    }

    private async Task TouchSyncStateErrorAsync(
        string deviceId,
        string message,
        string connectivity,
        CancellationToken ct)
    {
        var state = await EnsureSyncStateAsync(deviceId, ct);
        state.LastError = Truncate(message, 2000);
        state.LastConnectivityStatus = connectivity;
        state.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task<SyncState> EnsureSyncStateAsync(string deviceId, CancellationToken ct)
    {
        var state = await _db.SyncStates.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (state is not null)
        {
            if (string.IsNullOrWhiteSpace(state.DeviceId))
            {
                state.DeviceId = deviceId;
            }

            return state;
        }

        state = new SyncState { Id = 1, DeviceId = deviceId, UpdatedAtUtc = DateTime.UtcNow };
        _db.SyncStates.Add(state);
        await _db.SaveChangesAsync(ct);
        return state;
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= max ? value : value[..max];
    }
}
