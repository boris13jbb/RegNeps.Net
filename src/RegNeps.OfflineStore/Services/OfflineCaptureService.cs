using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Services;
using RegNeps.OfflineStore.Abstractions;
using RegNeps.OfflineStore.Entities;
using RegNeps.OfflineStore.Enums;
using RegNeps.OfflineStore.Models;

namespace RegNeps.OfflineStore.Services;

/// <summary>
/// Captura offline: CreateRecord + UpdateRecord (FASE 2D.5).
/// Delete offline no expuesto. Atómico LocalNepRecord + PendingOperation.
/// </summary>
public sealed class OfflineCaptureService
{
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
    private string? _activeCaptureSessionId;

    public OfflineCaptureService(
        LocalSyncDbContext db,
        OfflineSessionService sessions,
        IDeviceIdStore deviceIds)
    {
        _db = db;
        _sessions = sessions;
        _deviceIds = deviceIds;
    }

    public string EnsureCaptureSessionId()
    {
        _activeCaptureSessionId ??= Guid.NewGuid().ToString("N");
        return _activeCaptureSessionId;
    }

    public void StartNewCaptureSession() =>
        _activeCaptureSessionId = Guid.NewGuid().ToString("N");

    public async Task<OfflineCaptureResult> CreateRecordAsync(
        OfflineCreateRecordRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = await _sessions.GetValidSessionAsync(ct)
            ?? throw new InvalidOperationException(
                "No hay sesión offline válida. Inicie sesión online en el dispositivo primero.");

        if (!_sessions.HasPermission(session, OfflineStoreConstants.CaptureRecordsPermission))
        {
            throw new UnauthorizedAccessException(
                "El snapshot de sesión no incluye permiso CaptureRecords (solo UX; el servidor revalidará).");
        }

        ValidateBusinessFields(
            request.Telar, request.Neps, request.Tela, request.LoteTrama,
            request.Turno, request.Operario, request.LineaProduccion, request.Observacion);

        var deviceId = await _deviceIds.GetOrCreateAsync(ct);
        var clientOperationId = Guid.NewGuid().ToString("N");
        var captureSessionId = string.IsNullOrWhiteSpace(request.CaptureSessionId)
            ? EnsureCaptureSessionId()
            : request.CaptureSessionId.Trim();
        var now = DateTime.UtcNow;
        var lote = NormalizeLote(request.LoteTrama);

        var localId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        var payload = new CreateRecordPayload
        {
            ProtocolVersion = OfflineStoreConstants.ProtocolVersion,
            Telar = request.Telar.Trim(),
            Neps = request.Neps,
            Tela = request.Tela?.Trim() ?? string.Empty,
            LoteTrama = lote,
            Turno = request.Turno?.Trim() ?? string.Empty,
            Operario = request.Operario?.Trim() ?? string.Empty,
            LineaProduccion = request.LineaProduccion?.Trim() ?? string.Empty,
            Observacion = request.Observacion?.Trim() ?? string.Empty,
            ClientOperationId = clientOperationId,
            CaptureSessionId = captureSessionId,
            CreatedAtUtc = now
        };

        var record = new LocalNepRecord
        {
            Id = localId,
            ClientOperationId = clientOperationId,
            CaptureSessionId = captureSessionId,
            Telar = payload.Telar,
            Neps = payload.Neps,
            Tela = payload.Tela,
            LoteTrama = payload.LoteTrama,
            Turno = payload.Turno,
            Operario = payload.Operario,
            LineaProduccion = payload.LineaProduccion,
            Observacion = payload.Observacion,
            CreatedAtUtc = now,
            UserId = session.UserId,
            SyncStatus = LocalSyncStatus.PendingSync
        };

        var operation = new PendingOperation
        {
            Id = operationId,
            ClientOperationId = clientOperationId,
            OperationType = OfflineOperationType.CreateRecord,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            ProtocolVersion = OfflineStoreConstants.ProtocolVersion,
            CreatedAtUtc = now,
            AttemptCount = 0,
            Status = PendingOperationStatus.Pending,
            UserId = session.UserId,
            DeviceId = deviceId,
            CaptureSessionId = captureSessionId,
            LocalNepRecordId = localId
        };

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            _db.LocalNepRecords.Add(record);
            await _db.SaveChangesAsync(ct);

            _db.PendingOperations.Add(operation);
            await _db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            throw;
        }

        var level = AlertEvaluator.GetLevel(record.Neps);
        return new OfflineCaptureResult
        {
            Record = record,
            Operation = operation,
            QualityLevel = level,
            QualityLabel = level.ToDisplayLabel()
        };
    }

    /// <summary>
    /// Update offline de un registro ya sincronizado (ServerRecordId + ConcurrencyStamp).
    /// Atómico: LocalNepRecord + PendingOperation UpdateRecord.
    /// </summary>
    public async Task<OfflineCaptureResult> UpdateRecordAsync(
        OfflineUpdateRecordRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = await _sessions.GetValidSessionAsync(ct)
            ?? throw new InvalidOperationException(
                "No hay sesión offline válida. Inicie sesión online en el dispositivo primero.");

        var eligibility = await EvaluateEditEligibilityAsync(session, request.LocalRecordId, ct);
        if (!eligibility.CanEdit)
        {
            throw eligibility.Reason switch
            {
                OfflineEditBlockReason.NoEditPermission => new UnauthorizedAccessException(eligibility.Message),
                OfflineEditBlockReason.NotOwner => new UnauthorizedAccessException(eligibility.Message),
                _ => new InvalidOperationException(eligibility.Message)
            };
        }

        ValidateBusinessFields(
            request.Telar, request.Neps, request.Tela, request.LoteTrama,
            request.Turno, request.Operario, request.LineaProduccion, request.Observacion);

        var record = await _db.LocalNepRecords
            .FirstAsync(r => r.Id == request.LocalRecordId, ct);

        var serverId = record.ServerRecordId!.Value;
        var expectedStamp = record.ConcurrencyStamp!;
        var deviceId = await _deviceIds.GetOrCreateAsync(ct);
        var clientOperationId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var lote = NormalizeLote(request.LoteTrama);

        var payload = new UpdateRecordPayload
        {
            EntityId = serverId,
            Telar = request.Telar.Trim(),
            Neps = request.Neps,
            Tela = request.Tela?.Trim() ?? string.Empty,
            LoteTrama = lote,
            Turno = request.Turno?.Trim() ?? string.Empty,
            Operario = request.Operario?.Trim() ?? string.Empty,
            LineaProduccion = request.LineaProduccion?.Trim() ?? string.Empty,
            Observacion = request.Observacion?.Trim() ?? string.Empty
        };

        var operation = new PendingOperation
        {
            Id = Guid.NewGuid(),
            ClientOperationId = clientOperationId,
            OperationType = OfflineOperationType.UpdateRecord,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            ProtocolVersion = OfflineStoreConstants.ProtocolVersion,
            CreatedAtUtc = now,
            AttemptCount = 0,
            Status = PendingOperationStatus.Pending,
            UserId = session.UserId,
            DeviceId = deviceId,
            CaptureSessionId = record.CaptureSessionId,
            LocalNepRecordId = record.Id,
            TargetServerRecordId = serverId,
            ExpectedConcurrencyStamp = expectedStamp
        };

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            record.Telar = payload.Telar;
            record.Neps = payload.Neps;
            record.Tela = payload.Tela;
            record.LoteTrama = payload.LoteTrama;
            record.Turno = payload.Turno;
            record.Operario = payload.Operario;
            record.LineaProduccion = payload.LineaProduccion;
            record.Observacion = payload.Observacion;
            record.UpdatedAtUtc = now;
            record.SyncStatus = LocalSyncStatus.PendingSync;
            // ConcurrencyStamp local se mantiene hasta Accepted (ExpectedConcurrencyStamp = stamp previo).

            _db.PendingOperations.Add(operation);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            throw;
        }

        var level = AlertEvaluator.GetLevel(record.Neps);
        return new OfflineCaptureResult
        {
            Record = record,
            Operation = operation,
            QualityLevel = level,
            QualityLabel = level.ToDisplayLabel()
        };
    }

    public async Task<OfflineEditEligibility> GetEditEligibilityAsync(
        Guid localRecordId,
        CancellationToken ct = default)
    {
        var session = await _sessions.GetValidSessionAsync(ct);
        if (session is null)
        {
            return Blocked(OfflineEditBlockReason.NoSession, "No hay sesión offline válida.", localRecordId);
        }

        return await EvaluateEditEligibilityAsync(session, localRecordId, ct);
    }

    public async Task<IReadOnlyList<LocalNepRecord>> ListEditableAsync(
        int take = 50,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);
        var session = await _sessions.GetValidSessionAsync(ct);
        if (session is null)
        {
            return Array.Empty<LocalNepRecord>();
        }

        if (!_sessions.HasPermission(session, OfflineStoreConstants.EditRecordsPermission))
        {
            return Array.Empty<LocalNepRecord>();
        }

        var candidates = await _db.LocalNepRecords.AsNoTracking()
            .Where(r => !r.IsDeleted
                        && r.ServerRecordId != null
                        && r.ConcurrencyStamp != null
                        && r.ConcurrencyStamp != "")
            .Where(r => SeesAll(session) || r.UserId == session.UserId)
            .OrderByDescending(r => r.UpdatedAtUtc ?? r.CreatedAtUtc)
            .Take(take * 2)
            .ToListAsync(ct);

        var result = new List<LocalNepRecord>();
        foreach (var r in candidates)
        {
            var elig = await EvaluateEditEligibilityAsync(session, r.Id, ct);
            if (elig.CanEdit)
            {
                result.Add(r);
                if (result.Count >= take)
                {
                    break;
                }
            }
        }

        return result;
    }

    public Task<IReadOnlyList<LocalNepRecord>> ListRecentAsync(
        int take = 50,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);
        return ListRecentInternalAsync(take, ct);
    }

    public Task<LocalNepRecord?> GetLocalRecordAsync(Guid localRecordId, CancellationToken ct = default) =>
        _db.LocalNepRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == localRecordId, ct);

    private async Task<IReadOnlyList<LocalNepRecord>> ListRecentInternalAsync(int take, CancellationToken ct)
    {
        var session = await _sessions.GetValidSessionAsync(ct);
        if (session is null)
        {
            return Array.Empty<LocalNepRecord>();
        }

        return await _db.LocalNepRecords.AsNoTracking()
            .Where(r => r.UserId == session.UserId && !r.IsDeleted)
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(take)
            .ToListAsync(ct);
    }

    private async Task<OfflineEditEligibility> EvaluateEditEligibilityAsync(
        LocalSession session,
        Guid localRecordId,
        CancellationToken ct)
    {
        if (!_sessions.HasPermission(session, OfflineStoreConstants.EditRecordsPermission))
        {
            return Blocked(
                OfflineEditBlockReason.NoEditPermission,
                "Tu sesión no incluye permiso para editar. El servidor lo revalidará al sincronizar.",
                localRecordId);
        }

        var record = await _db.LocalNepRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == localRecordId, ct);
        if (record is null)
        {
            return Blocked(OfflineEditBlockReason.NotFound, "Registro no encontrado.", localRecordId);
        }

        if (record.IsDeleted)
        {
            return Blocked(OfflineEditBlockReason.Deleted, "El registro está eliminado localmente.", localRecordId);
        }

        if (!SeesAll(session)
            && !string.Equals(record.UserId, session.UserId, StringComparison.OrdinalIgnoreCase))
        {
            return Blocked(
                OfflineEditBlockReason.NotOwner,
                "No puedes editar registros de otro usuario en este dispositivo.",
                localRecordId);
        }

        if (record.ServerRecordId is null || record.ServerRecordId == Guid.Empty)
        {
            return Blocked(
                OfflineEditBlockReason.CreateStillPending,
                "Pendiente de sincronización. Espera a que el Create se sincronice antes de editar.",
                localRecordId);
        }

        if (string.IsNullOrWhiteSpace(record.ConcurrencyStamp))
        {
            return Blocked(
                OfflineEditBlockReason.MissingConcurrencyStamp,
                "Falta la marca de concurrencia del servidor. Sincroniza primero.",
                localRecordId,
                record.ServerRecordId);
        }

        // Create Pending del mismo local (sin ServerRecordId ya cubierto) o Update pendiente/conflicto.
        var blocking = await _db.PendingOperations.AsNoTracking()
            .Where(o => o.UserId == session.UserId
                        && (o.LocalNepRecordId == localRecordId
                            || o.TargetServerRecordId == record.ServerRecordId))
            .Where(o => o.Status == PendingOperationStatus.Pending
                        || o.Status == PendingOperationStatus.Sending
                        || o.Status == PendingOperationStatus.Conflict)
            .ToListAsync(ct);

        if (blocking.Any(o => o.OperationType == OfflineOperationType.CreateRecord
                              && o.Status is PendingOperationStatus.Pending or PendingOperationStatus.Sending))
        {
            return Blocked(
                OfflineEditBlockReason.CreateStillPending,
                "Pendiente de sincronización. Espera a que el Create se sincronice antes de editar.",
                localRecordId,
                record.ServerRecordId);
        }

        if (blocking.Any(o => o.OperationType == OfflineOperationType.UpdateRecord))
        {
            return Blocked(
                OfflineEditBlockReason.UpdateAlreadyPending,
                "Ya existe una modificación pendiente o en revisión para este registro.",
                localRecordId,
                record.ServerRecordId);
        }

        return new OfflineEditEligibility
        {
            CanEdit = true,
            Reason = OfflineEditBlockReason.None,
            Message = string.Empty,
            LocalRecordId = localRecordId,
            ServerRecordId = record.ServerRecordId
        };
    }

    private static OfflineEditEligibility Blocked(
        OfflineEditBlockReason reason,
        string message,
        Guid? localId = null,
        Guid? serverId = null) =>
        new()
        {
            CanEdit = false,
            Reason = reason,
            Message = message,
            LocalRecordId = localId,
            ServerRecordId = serverId
        };

    private static bool SeesAll(LocalSession session) =>
        SeesAllRoleCodes.Contains(session.RoleCode ?? string.Empty);

    private static string NormalizeLote(string? loteTrama) =>
        string.IsNullOrWhiteSpace(loteTrama)
            ? NepsConstants.LoteTramaPrefix
            : loteTrama.Trim().ToUpperInvariant();

    private static void ValidateBusinessFields(
        string telar,
        double neps,
        string? tela,
        string? lote,
        string? turno,
        string? operario,
        string? linea,
        string? observacion)
    {
        if (string.IsNullOrWhiteSpace(telar))
        {
            throw new ArgumentException("El telar es obligatorio.", nameof(telar));
        }

        if (neps <= 0)
        {
            throw new ArgumentException("Los neps deben ser mayores que cero.", nameof(neps));
        }

        // Campos opcionales: sin longitud máxima estricta aquí; el servidor revalida.
        _ = tela;
        _ = lote;
        _ = turno;
        _ = operario;
        _ = linea;
        _ = observacion;
    }
}
