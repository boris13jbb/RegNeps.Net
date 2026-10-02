using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RegNeps.OfflineStore.Bridge;

/// <summary>
/// Serialización y validación del protocolo bridge (sin efectos laterales).
/// </summary>
public static class OfflineBridgeCodec
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public static OfflineBridgeEnvelope CreateRequest(string action, object? payload, string? requestId = null)
    {
        JsonElement? payloadElement = null;
        if (payload is not null)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, JsonOptions));
            payloadElement = doc.RootElement.Clone();
        }

        return new OfflineBridgeEnvelope
        {
            ProtocolVersion = OfflineBridgeConstants.ProtocolVersion,
            MessageType = OfflineBridgeConstants.MessageTypeRequest,
            RequestId = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : requestId.Trim(),
            Action = action,
            Payload = payloadElement
        };
    }

    public static OfflineBridgeEnvelope CreateSuccess(string requestId, string? action, object? result)
    {
        JsonElement? resultElement = null;
        if (result is not null)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, JsonOptions));
            resultElement = doc.RootElement.Clone();
        }

        return new OfflineBridgeEnvelope
        {
            ProtocolVersion = OfflineBridgeConstants.ProtocolVersion,
            MessageType = OfflineBridgeConstants.MessageTypeResponse,
            RequestId = requestId,
            Action = action,
            Ok = true,
            Result = resultElement
        };
    }

    public static OfflineBridgeEnvelope CreateError(
        string requestId,
        string? action,
        string errorCode,
        string errorMessage)
    {
        return new OfflineBridgeEnvelope
        {
            ProtocolVersion = OfflineBridgeConstants.ProtocolVersion,
            MessageType = OfflineBridgeConstants.MessageTypeResponse,
            RequestId = requestId,
            Action = action,
            Ok = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }

    public static string Serialize(OfflineBridgeEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, JsonOptions);

    public static bool TryParseEnvelope(string json, out OfflineBridgeEnvelope? envelope, out string? errorCode)
    {
        envelope = null;
        errorCode = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            errorCode = OfflineBridgeErrorCodes.InvalidEnvelope;
            return false;
        }

        try
        {
            envelope = JsonSerializer.Deserialize<OfflineBridgeEnvelope>(json, JsonOptions);
            if (envelope is null || string.IsNullOrWhiteSpace(envelope.RequestId))
            {
                errorCode = OfflineBridgeErrorCodes.InvalidEnvelope;
                envelope = null;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            errorCode = OfflineBridgeErrorCodes.InvalidEnvelope;
            return false;
        }
    }

    /// <summary>
    /// Valida request: versión, tipo, acción conocida (whitelist explícita).
    /// </summary>
    public static string? ValidateRequest(OfflineBridgeEnvelope envelope)
    {
        if (!string.Equals(envelope.MessageType, OfflineBridgeConstants.MessageTypeRequest, StringComparison.OrdinalIgnoreCase))
        {
            return OfflineBridgeErrorCodes.InvalidEnvelope;
        }

        if (envelope.ProtocolVersion != OfflineBridgeConstants.ProtocolVersion)
        {
            return OfflineBridgeErrorCodes.IncompatibleProtocol;
        }

        if (string.IsNullOrWhiteSpace(envelope.Action))
        {
            return OfflineBridgeErrorCodes.UnknownAction;
        }

        var action = envelope.Action.Trim();
        if (!OfflineBridgeActions.IsAllowed(action))
        {
            // Acciones sync/push/pull u otras → no autorizadas en 2D.1 (no ejecución arbitraria).
            return OfflineBridgeErrorCodes.UnauthorizedAction;
        }

        return null;
    }

    public static string BuildInvokeUrl(OfflineBridgeEnvelope request)
    {
        var json = Serialize(request);
        var encoded = Uri.EscapeDataString(json);
        return $"{OfflineBridgeConstants.Scheme}://{OfflineBridgeConstants.InvokeHost}?data={encoded}";
    }

    public static bool TryParseInvokeUrl(string? rawUrl, out OfflineBridgeEnvelope? envelope, out string? errorCode)
    {
        envelope = null;
        errorCode = null;

        if (string.IsNullOrWhiteSpace(rawUrl) || rawUrl.Length > OfflineBridgeConstants.MaxUrlLength)
        {
            errorCode = OfflineBridgeErrorCodes.InvalidEnvelope;
            return false;
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            errorCode = OfflineBridgeErrorCodes.InvalidEnvelope;
            return false;
        }

        if (!string.Equals(uri.Scheme, OfflineBridgeConstants.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            errorCode = OfflineBridgeErrorCodes.InvalidEnvelope;
            return false;
        }

        var host = string.IsNullOrEmpty(uri.Host) ? uri.AbsolutePath.Trim('/') : uri.Host;
        if (!string.Equals(host, OfflineBridgeConstants.InvokeHost, StringComparison.OrdinalIgnoreCase))
        {
            errorCode = OfflineBridgeErrorCodes.UnknownAction;
            return false;
        }

        var data = GetQueryValue(uri, "data");
        if (string.IsNullOrWhiteSpace(data))
        {
            errorCode = OfflineBridgeErrorCodes.InvalidEnvelope;
            return false;
        }

        string json;
        try
        {
            json = Uri.UnescapeDataString(data);
        }
        catch (UriFormatException)
        {
            errorCode = OfflineBridgeErrorCodes.InvalidEnvelope;
            return false;
        }

        return TryParseEnvelope(json, out envelope, out errorCode);
    }

    public static string? RejectForbiddenFields(JsonElement? payload)
    {
        if (payload is null || payload.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (payload.Value.ValueKind != JsonValueKind.Object)
        {
            return OfflineBridgeErrorCodes.InvalidPayload;
        }

        foreach (var prop in payload.Value.EnumerateObject())
        {
            if (IsForbiddenFieldName(prop.Name))
            {
                return OfflineBridgeErrorCodes.ForbiddenField;
            }
        }

        return null;
    }

    public static bool IsForbiddenFieldName(string name) =>
        OfflineBridgeConstants.ForbiddenPayloadFieldNames.Any(f =>
            string.Equals(f, name, StringComparison.OrdinalIgnoreCase));

    public static OfflineSessionSavePayload? DeserializeSavePayload(JsonElement? payload, out string? errorCode)
    {
        errorCode = RejectForbiddenFields(payload);
        if (errorCode is not null)
        {
            return null;
        }

        if (payload is null || payload.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            errorCode = OfflineBridgeErrorCodes.InvalidPayload;
            return null;
        }

        try
        {
            var dto = payload.Value.Deserialize<OfflineSessionSavePayload>(JsonOptions);
            if (dto is null || string.IsNullOrWhiteSpace(dto.UserId))
            {
                errorCode = OfflineBridgeErrorCodes.InvalidPayload;
                return null;
            }

            if (dto.ExtensionData is not null)
            {
                foreach (var key in dto.ExtensionData.Keys)
                {
                    if (IsForbiddenFieldName(key))
                    {
                        errorCode = OfflineBridgeErrorCodes.ForbiddenField;
                        return null;
                    }
                }
            }

            // Defensa extra: permisos no pueden colar "password".
            if (dto.Permissions.Any(p => IsForbiddenFieldName(p)))
            {
                errorCode = OfflineBridgeErrorCodes.ForbiddenField;
                return null;
            }

            return dto;
        }
        catch (JsonException)
        {
            errorCode = OfflineBridgeErrorCodes.InvalidPayload;
            return null;
        }
    }

    private static string? GetQueryValue(Uri uri, string key)
    {
        var query = uri.Query;
        if (string.IsNullOrEmpty(query) || query.Length < 2)
        {
            return null;
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var name = eq < 0 ? part : part[..eq];
            if (!string.Equals(Uri.UnescapeDataString(name), key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return eq < 0 ? string.Empty : part[(eq + 1)..];
        }

        return null;
    }

    /// <summary>Escapa JSON para EvaluateJavaScriptAsync (comillas simples envolventes).</summary>
    public static string ToJavaScriptStringLiteral(string value)
    {
        var sb = new StringBuilder(value.Length + 16);
        sb.Append('\'');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\'':
                    sb.Append("\\'");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\u2028':
                    sb.Append("\\u2028");
                    break;
                case '\u2029':
                    sb.Append("\\u2029");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        sb.Append('\'');
        return sb.ToString();
    }
}
