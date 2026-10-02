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
/// Captura offline v1: solo CreateRecord, atómica (LocalNepRecord + PendingOperation).
/// </summary>
public sealed class OfflineCaptureService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
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

        Validate(request);

        var deviceId = await _deviceIds.GetOrCreateAsync(ct);
        var clientOperationId = Guid.NewGuid().ToString("N");
        var captureSessionId = string.IsNullOrWhiteSpace(request.CaptureSessionId)
            ? EnsureCaptureSessionId()
            : request.CaptureSessionId.Trim();
        var now = DateTime.UtcNow;
        var lote = string.IsNullOrWhiteSpace(request.LoteTrama)
            ? NepsConstants.LoteTramaPrefix
            : request.LoteTrama.Trim().ToUpperInvariant();

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
            _db.PendingOperations.Add(operation);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            // Limpiar tracker tras fallo para no dejar entidades huérfanas en memoria.
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

    public Task<IReadOnlyList<LocalNepRecord>> ListRecentAsync(
        int take = 50,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);
        return ListRecentInternalAsync(take, ct);
    }

    private async Task<IReadOnlyList<LocalNepRecord>> ListRecentInternalAsync(int take, CancellationToken ct)
    {
        var session = await _sessions.GetValidSessionAsync(ct);
        if (session is null)
        {
            return Array.Empty<LocalNepRecord>();
        }

        return await _db.LocalNepRecords.AsNoTracking()
            .Where(r => r.UserId == session.UserId)
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(take)
            .ToListAsync(ct);
    }

    private static void Validate(OfflineCreateRecordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Telar))
        {
            throw new ArgumentException("El telar es obligatorio.", nameof(request.Telar));
        }

        if (request.Neps <= 0)
        {
            throw new ArgumentException("Los neps deben ser mayores que cero.", nameof(request.Neps));
        }
    }
}
