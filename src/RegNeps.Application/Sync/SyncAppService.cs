using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RegNeps.Application.Abstractions;
using RegNeps.Application.Permissions;
using RegNeps.Application.Records;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;
using RegNeps.Domain.Services;
using RegNeps.Domain.Sync;

namespace RegNeps.Application.Sync;

/// <summary>
/// Orquestación Push/Pull v1: CreateRecord, UpdateRecord, DeleteRecord.
/// Autorización siempre desde claims + matriz de permisos.
/// </summary>
public sealed class SyncAppService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ISyncPersistence _persistence;
    private readonly IPermissionService _permissions;
    private readonly ILogger<SyncAppService> _logger;

    public SyncAppService(
        ISyncPersistence persistence,
        IPermissionService permissions,
        ILogger<SyncAppService> logger)
    {
        _persistence = persistence;
        _permissions = permissions;
        _logger = logger;
    }

    public async Task<SyncPushResponse> PushAsync(
        SyncPushRequest request,
        RecordActor actor,
        string? correlationId,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await _permissions.EnsureLoadedAsync(ct);

        var response = new SyncPushResponse
        {
            ProtocolVersion = SyncProtocol.Version,
            ServerTimeUtc = DateTime.UtcNow
        };

        if (request is null)
        {
            response.Results.Add(Invalid(string.Empty, "REQUEST_NULL", "Solicitud inválida."));
            return response;
        }

        if (request.ProtocolVersion != SyncProtocol.Version)
        {
            response.Results.Add(Invalid(string.Empty, "PROTOCOL_VERSION",
                $"ProtocolVersion no soportado. Esperado {SyncProtocol.Version}."));
            LogPush(actor, request.DeviceId, null, SyncOperationResult.Invalid, sw.ElapsedMilliseconds, correlationId, null);
            return response;
        }

        if (!TryNormalizeDeviceId(request.DeviceId, out var deviceId, out var deviceError))
        {
            response.Results.Add(Invalid(string.Empty, "DEVICE_ID", deviceError!));
            LogPush(actor, request.DeviceId, null, SyncOperationResult.Invalid, sw.ElapsedMilliseconds, correlationId, null);
            return response;
        }

        if (request.Operations is null || request.Operations.Count == 0)
        {
            response.Results.Add(Invalid(string.Empty, "OPERATIONS_EMPTY", "Debe enviar al menos una operación."));
            return response;
        }

        if (request.Operations.Count > SyncConstants.MaxPageSize)
        {
            response.Results.Add(Invalid(string.Empty, "OPERATIONS_LIMIT",
                $"Máximo {SyncConstants.MaxPageSize} operaciones por Push."));
            return response;
        }

        foreach (var op in request.Operations)
        {
            response.Results.Add(await ProcessOperationAsync(op, actor, deviceId, correlationId, ct));
        }

        LogPush(actor, deviceId, null, null, sw.ElapsedMilliseconds, correlationId,
            $"ops={request.Operations.Count}");
        return response;
    }

    public async Task<SyncPullResponse> PullAsync(
        SyncPullRequest request,
        RecordActor actor,
        string? correlationId,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await _permissions.EnsureLoadedAsync(ct);

        var serverTime = DateTime.UtcNow;
        if (request is null)
        {
            return EmptyPull(serverTime, cursor: 0);
        }

        var safeCursor = request.Cursor < 0 ? 0 : request.Cursor;

        if (request.ProtocolVersion != SyncProtocol.Version)
        {
            _logger.LogWarning(
                "Sync Pull protocolo inválido. UserId={UserId} DeviceId={DeviceId} CorrelationId={CorrelationId}",
                actor.UserId, request.DeviceId, correlationId);
            return EmptyPull(serverTime, safeCursor);
        }

        if (!TryNormalizeDeviceId(request.DeviceId, out var deviceId, out _))
        {
            _logger.LogWarning(
                "Sync Pull DeviceId inválido. UserId={UserId} CorrelationId={CorrelationId}",
                actor.UserId, correlationId);
            return EmptyPull(serverTime, safeCursor);
        }

        if (!CanViewRecords(actor))
        {
            _logger.LogInformation(
                "Sync Pull Forbidden. UserId={UserId} DeviceId={DeviceId} CorrelationId={CorrelationId} DurationMs={DurationMs}",
                actor.UserId, deviceId, correlationId, sw.ElapsedMilliseconds);
            return new SyncPullResponse
            {
                ProtocolVersion = SyncProtocol.Version,
                ServerTimeUtc = serverTime,
                NextCursor = safeCursor,
                HasMore = false,
                Changes = []
            };
        }

        var page = await _persistence.PullAuthorizedChangesAsync(
            actor, safeCursor, SyncProtocol.NormalizePageSize(request.PageSize), ct);
        var changes = page.AuthorizedChanges.Select(MapChange).ToList();

        _logger.LogInformation(
            "Sync Pull. UserId={UserId} DeviceId={DeviceId} Cursor={Cursor} NextCursor={NextCursor} Returned={Returned} HasMore={HasMore} CorrelationId={CorrelationId} DurationMs={DurationMs}",
            actor.UserId, deviceId, safeCursor, page.NextCursor, changes.Count, page.HasMore, correlationId, sw.ElapsedMilliseconds);

        return new SyncPullResponse
        {
            ProtocolVersion = SyncProtocol.Version,
            ServerTimeUtc = serverTime,
            NextCursor = page.NextCursor,
            HasMore = page.HasMore,
            Changes = changes
        };
    }

    private async Task<SyncOperationResultDto> ProcessOperationAsync(
        SyncOperationDto op,
        RecordActor actor,
        string deviceId,
        string? correlationId,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var clientOpId = op?.ClientOperationId?.Trim() ?? string.Empty;

        try
        {
            if (op is null)
            {
                return Invalid(clientOpId, "OPERATION_NULL", "Operación inválida.");
            }

            if (string.IsNullOrWhiteSpace(clientOpId))
            {
                var r = Invalid(clientOpId, "CLIENT_OPERATION_ID", "ClientOperationId es obligatorio.");
                LogPush(actor, deviceId, clientOpId, SyncOperationResult.Invalid, sw.ElapsedMilliseconds, correlationId, null);
                return r;
            }

            if (clientOpId.Length > SyncConstants.MaxClientOperationIdLength)
            {
                return Invalid(clientOpId, "CLIENT_OPERATION_ID", "ClientOperationId demasiado largo.");
            }

            var opType = (op.OperationType ?? string.Empty).Trim();
            if (string.Equals(opType, SyncConstants.OperationCreateRecord, StringComparison.OrdinalIgnoreCase))
            {
                return await ProcessCreateAsync(op, clientOpId, actor, deviceId, correlationId, sw, ct);
            }

            if (string.Equals(opType, SyncConstants.OperationUpdateRecord, StringComparison.OrdinalIgnoreCase))
            {
                return await ProcessUpdateAsync(op, clientOpId, actor, deviceId, correlationId, sw, ct);
            }

            if (string.Equals(opType, SyncConstants.OperationDeleteRecord, StringComparison.OrdinalIgnoreCase))
            {
                return await ProcessDeleteAsync(op, clientOpId, actor, deviceId, correlationId, sw, ct);
            }

            var unsupported = Invalid(clientOpId, "OPERATION_TYPE_UNSUPPORTED",
                $"OperationType '{opType}' no soportado en protocolo v1 (CreateRecord/UpdateRecord/DeleteRecord).");
            LogPush(actor, deviceId, clientOpId, SyncOperationResult.Invalid, sw.ElapsedMilliseconds, correlationId, opType);
            return unsupported;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Sync Push TransientError. UserId={UserId} DeviceId={DeviceId} ClientOperationId={ClientOperationId} CorrelationId={CorrelationId}",
                actor.UserId, deviceId, clientOpId, correlationId);
            return new SyncOperationResultDto
            {
                ClientOperationId = clientOpId,
                Result = nameof(SyncOperationResult.TransientError),
                ErrorCode = "TRANSIENT",
                Message = "Error temporal al procesar la operación."
            };
        }
    }

    private async Task<SyncOperationResultDto> ProcessCreateAsync(
        SyncOperationDto op,
        string clientOpId,
        RecordActor actor,
        string deviceId,
        string? correlationId,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (!CanCapture(actor))
        {
            var forbidden = new SyncOperationResultDto
            {
                ClientOperationId = clientOpId,
                Result = nameof(SyncOperationResult.Forbidden),
                ErrorCode = "CAPTURE_FORBIDDEN",
                Message = "No tiene permiso para capturar registros."
            };
            LogPush(actor, deviceId, clientOpId, SyncOperationResult.Forbidden, sw.ElapsedMilliseconds, correlationId, null);
            return forbidden;
        }

        if (op.Payload is null || op.Payload.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return Invalid(clientOpId, "PAYLOAD_REQUIRED", "Payload es obligatorio.");
        }

        SyncCreateRecordPayload? payload;
        try
        {
            payload = op.Payload.Value.Deserialize<SyncCreateRecordPayload>(JsonOptions);
        }
        catch (JsonException)
        {
            return Invalid(clientOpId, "PAYLOAD_FORMAT", "Payload JSON inválido.");
        }

        if (payload is null)
        {
            return Invalid(clientOpId, "PAYLOAD_FORMAT", "Payload JSON inválido.");
        }

        var createRequest = new CreateNepRecordRequest
        {
            Telar = payload.Telar ?? string.Empty,
            Neps = payload.Neps,
            Tela = payload.Tela ?? string.Empty,
            LoteTrama = payload.LoteTrama ?? string.Empty,
            Turno = payload.Turno ?? string.Empty,
            Operario = payload.Operario ?? string.Empty,
            LineaProduccion = payload.LineaProduccion ?? string.Empty,
            Observacion = payload.Observacion ?? string.Empty,
            ClientOperationId = clientOpId,
            CaptureSessionId = string.IsNullOrWhiteSpace(op.CaptureSessionId)
                ? null
                : op.CaptureSessionId.Trim()
        };

        if (createRequest.CaptureSessionId?.Length > SyncConstants.MaxCaptureSessionIdLength)
        {
            return Invalid(clientOpId, "CAPTURE_SESSION_ID", "CaptureSessionId demasiado largo.");
        }

        try
        {
            NepRecordService.ValidateCreateRequest(createRequest);
        }
        catch (ArgumentException ex)
        {
            var invalid = Invalid(clientOpId, "VALIDATION", ex.Message);
            LogPush(actor, deviceId, clientOpId, SyncOperationResult.Invalid, sw.ElapsedMilliseconds, correlationId, null);
            return invalid;
        }

        var persist = await _persistence.CreateRecordAtomicallyAsync(createRequest, actor, deviceId, ct);
        if (persist.Result is SyncOperationResult.Accepted or SyncOperationResult.Duplicate)
        {
            var level = AlertEvaluator.GetLevel(persist.Record!.Neps);
            var dto = new SyncOperationResultDto
            {
                ClientOperationId = clientOpId,
                Result = persist.Result.ToString(),
                EntityId = persist.Record.Id,
                ConcurrencyStamp = persist.Record.ConcurrencyStamp,
                QualityLabel = level.ToDisplayLabel(),
                ChangeSequence = persist.ChangeSequence
            };
            LogPush(actor, deviceId, clientOpId, persist.Result, sw.ElapsedMilliseconds, correlationId, null);
            return dto;
        }

        var mapped = new SyncOperationResultDto
        {
            ClientOperationId = clientOpId,
            Result = persist.Result.ToString(),
            ErrorCode = persist.ErrorCode,
            Message = persist.Message
        };
        LogPush(actor, deviceId, clientOpId, persist.Result, sw.ElapsedMilliseconds, correlationId, persist.ErrorCode);
        return mapped;
    }

    private async Task<SyncOperationResultDto> ProcessUpdateAsync(
        SyncOperationDto op,
        string clientOpId,
        RecordActor actor,
        string deviceId,
        string? correlationId,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (!CanEdit(actor))
        {
            var forbidden = new SyncOperationResultDto
            {
                ClientOperationId = clientOpId,
                Result = nameof(SyncOperationResult.Forbidden),
                ErrorCode = "EDIT_FORBIDDEN",
                Message = "No tiene permiso para editar registros."
            };
            LogPush(actor, deviceId, clientOpId, SyncOperationResult.Forbidden, sw.ElapsedMilliseconds, correlationId, null);
            return forbidden;
        }

        if (op.Payload is null || op.Payload.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return Invalid(clientOpId, "PAYLOAD_REQUIRED", "Payload es obligatorio.");
        }

        SyncUpdateRecordPayload? payload;
        try
        {
            payload = op.Payload.Value.Deserialize<SyncUpdateRecordPayload>(JsonOptions);
        }
        catch (JsonException)
        {
            return Invalid(clientOpId, "PAYLOAD_FORMAT", "Payload JSON inválido.");
        }

        if (payload is null || payload.EntityId == Guid.Empty)
        {
            return Invalid(clientOpId, "PAYLOAD_FORMAT", "EntityId es obligatorio.");
        }

        var expectedStamp = FirstNonEmpty(op.ExpectedConcurrencyStamp, payload.ExpectedConcurrencyStamp);
        if (string.IsNullOrWhiteSpace(expectedStamp))
        {
            return Invalid(clientOpId, "CONCURRENCY_STAMP", "ExpectedConcurrencyStamp es obligatorio.");
        }

        var fields = new SyncUpdateRecordPayload
        {
            EntityId = payload.EntityId,
            Telar = payload.Telar ?? string.Empty,
            Neps = payload.Neps,
            Tela = payload.Tela ?? string.Empty,
            LoteTrama = payload.LoteTrama ?? string.Empty,
            Turno = payload.Turno ?? string.Empty,
            Operario = payload.Operario ?? string.Empty,
            LineaProduccion = payload.LineaProduccion ?? string.Empty,
            Observacion = payload.Observacion ?? string.Empty
        };

        try
        {
            NepRecordService.ValidateCreateRequest(new CreateNepRecordRequest
            {
                Telar = fields.Telar,
                Neps = fields.Neps,
                Tela = fields.Tela,
                LoteTrama = fields.LoteTrama,
                Turno = fields.Turno,
                Operario = fields.Operario,
                LineaProduccion = fields.LineaProduccion,
                Observacion = fields.Observacion,
                ClientOperationId = clientOpId
            });
        }
        catch (ArgumentException ex)
        {
            return Invalid(clientOpId, "VALIDATION", ex.Message);
        }

        var captureSessionId = string.IsNullOrWhiteSpace(op.CaptureSessionId)
            ? null
            : op.CaptureSessionId.Trim();

        var outcome = await _persistence.UpdateRecordAtomicallyAsync(
            fields, expectedStamp, clientOpId, captureSessionId, actor, deviceId, ct);

        var dto = MapMutationDto(clientOpId, outcome);
        LogPush(actor, deviceId, clientOpId, outcome.Result, sw.ElapsedMilliseconds, correlationId, outcome.ErrorCode);
        return dto;
    }

    private async Task<SyncOperationResultDto> ProcessDeleteAsync(
        SyncOperationDto op,
        string clientOpId,
        RecordActor actor,
        string deviceId,
        string? correlationId,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (!CanDelete(actor))
        {
            var forbidden = new SyncOperationResultDto
            {
                ClientOperationId = clientOpId,
                Result = nameof(SyncOperationResult.Forbidden),
                ErrorCode = "DELETE_FORBIDDEN",
                Message = "No tiene permiso para eliminar registros."
            };
            LogPush(actor, deviceId, clientOpId, SyncOperationResult.Forbidden, sw.ElapsedMilliseconds, correlationId, null);
            return forbidden;
        }

        if (op.Payload is null || op.Payload.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return Invalid(clientOpId, "PAYLOAD_REQUIRED", "Payload es obligatorio.");
        }

        SyncDeleteRecordPayload? payload;
        try
        {
            payload = op.Payload.Value.Deserialize<SyncDeleteRecordPayload>(JsonOptions);
        }
        catch (JsonException)
        {
            return Invalid(clientOpId, "PAYLOAD_FORMAT", "Payload JSON inválido.");
        }

        if (payload is null || payload.EntityId == Guid.Empty)
        {
            return Invalid(clientOpId, "PAYLOAD_FORMAT", "EntityId es obligatorio.");
        }

        var expectedStamp = FirstNonEmpty(op.ExpectedConcurrencyStamp, payload.ExpectedConcurrencyStamp);
        if (string.IsNullOrWhiteSpace(expectedStamp))
        {
            return Invalid(clientOpId, "CONCURRENCY_STAMP", "ExpectedConcurrencyStamp es obligatorio.");
        }

        var outcome = await _persistence.DeleteRecordAtomicallyAsync(
            payload.EntityId, expectedStamp, clientOpId, actor, deviceId, ct);

        var dto = MapMutationDto(clientOpId, outcome);
        LogPush(actor, deviceId, clientOpId, outcome.Result, sw.ElapsedMilliseconds, correlationId, outcome.ErrorCode);
        return dto;
    }

    private static SyncOperationResultDto MapMutationDto(
        string clientOpId,
        AtomicNepRecordMutationResult outcome)
    {
        if (outcome.Result is SyncOperationResult.Accepted or SyncOperationResult.Duplicate)
        {
            string? quality = null;
            if (outcome.Record is not null)
            {
                quality = AlertEvaluator.GetLevel(outcome.Record.Neps).ToDisplayLabel();
            }

            return new SyncOperationResultDto
            {
                ClientOperationId = clientOpId,
                Result = outcome.Result.ToString(),
                EntityId = outcome.EntityId ?? outcome.Record?.Id,
                ConcurrencyStamp = outcome.Record?.ConcurrencyStamp ?? outcome.ServerConcurrencyStamp,
                QualityLabel = quality,
                ChangeSequence = outcome.ChangeSequence,
                Message = outcome.Result == SyncOperationResult.Duplicate
                    ? (outcome.Message ?? "Operación ya procesada.")
                    : outcome.Message,
                ErrorCode = outcome.Result == SyncOperationResult.Duplicate ? outcome.ErrorCode : null
            };
        }

        if (outcome.Result == SyncOperationResult.Conflict)
        {
            return new SyncOperationResultDto
            {
                ClientOperationId = clientOpId,
                Result = nameof(SyncOperationResult.Conflict),
                EntityId = outcome.EntityId,
                ServerConcurrencyStamp = outcome.ServerConcurrencyStamp,
                ServerSnapshot = ParseSnapshot(outcome.ServerSnapshotJson),
                ChangeSequence = outcome.ChangeSequence,
                ErrorCode = outcome.ErrorCode ?? "CONFLICT",
                Message = outcome.Message ?? "Conflicto de concurrencia."
            };
        }

        return new SyncOperationResultDto
        {
            ClientOperationId = clientOpId,
            Result = outcome.Result.ToString(),
            EntityId = outcome.EntityId,
            ErrorCode = outcome.ErrorCode,
            Message = outcome.Message,
            ChangeSequence = outcome.ChangeSequence
        };
    }

    private static JsonElement? ParseSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v.Trim();
            }
        }

        return null;
    }

    private bool CanCapture(RecordActor actor) =>
        _permissions.HasPermissionByRoleCode(actor.EffectiveRoleCode, actor.IsSuperAdmin, true, AppPermission.CaptureRecords);

    private bool CanEdit(RecordActor actor) =>
        _permissions.HasPermissionByRoleCode(actor.EffectiveRoleCode, actor.IsSuperAdmin, true, AppPermission.EditRecords);

    private bool CanDelete(RecordActor actor) =>
        _permissions.HasPermissionByRoleCode(actor.EffectiveRoleCode, actor.IsSuperAdmin, true, AppPermission.DeleteRecords);

    private bool CanViewRecords(RecordActor actor) =>
        _permissions.HasPermissionByRoleCode(actor.EffectiveRoleCode, actor.IsSuperAdmin, true, AppPermission.ViewRecords)
        || _permissions.HasPermissionByRoleCode(actor.EffectiveRoleCode, actor.IsSuperAdmin, true, AppPermission.CaptureRecords);

    private static SyncChangeDto MapChange(Domain.Entities.SyncChangeLog log)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(log.PayloadJson) ? "{}" : log.PayloadJson);
        return new SyncChangeDto
        {
            Sequence = log.Sequence,
            EntityType = log.EntityType,
            EntityId = log.EntityId,
            ChangeType = log.ChangeType,
            OccurredAtUtc = log.OccurredAtUtc,
            Payload = doc.RootElement.Clone()
        };
    }

    private static SyncPullResponse EmptyPull(DateTime serverTime, long cursor) => new()
    {
        ProtocolVersion = SyncProtocol.Version,
        ServerTimeUtc = serverTime,
        NextCursor = cursor < 0 ? 0 : cursor,
        HasMore = false,
        Changes = []
    };

    private static SyncOperationResultDto Invalid(string clientOpId, string code, string message) => new()
    {
        ClientOperationId = clientOpId,
        Result = nameof(SyncOperationResult.Invalid),
        ErrorCode = code,
        Message = message
    };

    private static bool TryNormalizeDeviceId(string? raw, out string deviceId, out string? error)
    {
        deviceId = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            error = "DeviceId es obligatorio.";
            return false;
        }

        if (deviceId.Length > SyncConstants.MaxDeviceIdLength)
        {
            error = "DeviceId demasiado largo.";
            return false;
        }

        error = null;
        return true;
    }

    private void LogPush(
        RecordActor actor,
        string? deviceId,
        string? clientOperationId,
        SyncOperationResult? result,
        long durationMs,
        string? correlationId,
        string? detail)
    {
        _logger.LogInformation(
            "Sync Push. UserId={UserId} DeviceId={DeviceId} ClientOperationId={ClientOperationId} Result={Result} DurationMs={DurationMs} CorrelationId={CorrelationId} Detail={Detail}",
            actor.UserId,
            deviceId,
            clientOperationId,
            result?.ToString() ?? "-",
            durationMs,
            correlationId,
            detail);
    }
}
