namespace RegNeps.OfflineStore.Bridge;

/// <summary>
/// Procesa requests del bridge con whitelist de acciones. Sin reflexión ni execute(any).
/// </summary>
public sealed class OfflineBridgeProcessor
{
    private readonly IOfflineBridgeHandlers _handlers;

    public OfflineBridgeProcessor(IOfflineBridgeHandlers handlers)
    {
        _handlers = handlers;
    }

    public async Task<OfflineBridgeEnvelope> ProcessAsync(
        OfflineBridgeEnvelope request,
        CancellationToken ct = default)
    {
        var requestId = string.IsNullOrWhiteSpace(request.RequestId)
            ? Guid.NewGuid().ToString("N")
            : request.RequestId.Trim();

        var validationError = OfflineBridgeCodec.ValidateRequest(request);
        if (validationError is not null)
        {
            return OfflineBridgeCodec.CreateError(
                requestId,
                request.Action,
                validationError,
                DescribeValidation(validationError));
        }

        var action = request.Action!.Trim();

        try
        {
            return action switch
            {
                OfflineBridgeActions.SessionSave => await HandleSaveAsync(requestId, action, request, ct),
                OfflineBridgeActions.SessionClear => await HandleClearAsync(requestId, action, ct),
                OfflineBridgeActions.SessionGet => await HandleGetAsync(requestId, action, ct),
                OfflineBridgeActions.CaptureOpen => await HandleOpenCaptureAsync(requestId, action, ct),
                _ => OfflineBridgeCodec.CreateError(
                    requestId,
                    action,
                    OfflineBridgeErrorCodes.UnauthorizedAction,
                    "Acción no permitida en este protocolo.")
            };
        }
        catch (ArgumentException ex)
        {
            return OfflineBridgeCodec.CreateError(
                requestId, action, OfflineBridgeErrorCodes.InvalidPayload, ex.Message);
        }
        catch (Exception ex)
        {
            return OfflineBridgeCodec.CreateError(
                requestId, action, OfflineBridgeErrorCodes.HandlerError, ex.Message);
        }
    }

    /// <summary>Parsea URL deep-link y procesa.</summary>
    public async Task<(bool Handled, OfflineBridgeEnvelope? Response)> TryProcessUrlAsync(
        string? rawUrl,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawUrl) ||
            !rawUrl.StartsWith(OfflineBridgeConstants.Scheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null);
        }

        if (!OfflineBridgeCodec.TryParseInvokeUrl(rawUrl, out var envelope, out var error) || envelope is null)
        {
            var requestId = Guid.NewGuid().ToString("N");
            return (true, OfflineBridgeCodec.CreateError(
                requestId,
                null,
                error ?? OfflineBridgeErrorCodes.InvalidEnvelope,
                "URL de bridge inválida."));
        }

        var response = await ProcessAsync(envelope, ct);
        return (true, response);
    }

    private async Task<OfflineBridgeEnvelope> HandleSaveAsync(
        string requestId,
        string action,
        OfflineBridgeEnvelope request,
        CancellationToken ct)
    {
        var payload = OfflineBridgeCodec.DeserializeSavePayload(request.Payload, out var error);
        if (payload is null)
        {
            return OfflineBridgeCodec.CreateError(
                requestId,
                action,
                error ?? OfflineBridgeErrorCodes.InvalidPayload,
                "Payload de sesión inválido o contiene campos prohibidos.");
        }

        await _handlers.SaveSessionAsync(payload, ct);
        return OfflineBridgeCodec.CreateSuccess(requestId, action, new { saved = true, userId = payload.UserId });
    }

    private async Task<OfflineBridgeEnvelope> HandleClearAsync(
        string requestId,
        string action,
        CancellationToken ct)
    {
        var result = await _handlers.ClearSessionAsync(ct);
        return OfflineBridgeCodec.CreateSuccess(requestId, action, result);
    }

    private async Task<OfflineBridgeEnvelope> HandleGetAsync(
        string requestId,
        string action,
        CancellationToken ct)
    {
        var view = await _handlers.GetSessionAsync(ct);
        return OfflineBridgeCodec.CreateSuccess(requestId, action, view);
    }

    private async Task<OfflineBridgeEnvelope> HandleOpenCaptureAsync(
        string requestId,
        string action,
        CancellationToken ct)
    {
        await _handlers.OpenCaptureAsync(ct);
        return OfflineBridgeCodec.CreateSuccess(requestId, action, new { opened = true });
    }

    private static string DescribeValidation(string code) => code switch
    {
        OfflineBridgeErrorCodes.IncompatibleProtocol =>
            $"Se requiere protocolVersion={OfflineBridgeConstants.ProtocolVersion}.",
        OfflineBridgeErrorCodes.UnauthorizedAction =>
            "Acción no autorizada en el bridge 2D.1.",
        OfflineBridgeErrorCodes.UnknownAction =>
            "Acción desconocida.",
        _ => "Sobre de mensaje inválido."
    };
}
