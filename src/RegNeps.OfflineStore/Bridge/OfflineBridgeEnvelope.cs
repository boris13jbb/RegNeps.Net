using System.Text.Json;
using System.Text.Json.Serialization;

namespace RegNeps.OfflineStore.Bridge;

/// <summary>Sobre versionado del bridge (request/response).</summary>
public sealed class OfflineBridgeEnvelope
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }

    /// <summary>"request" o "response".</summary>
    [JsonPropertyName("messageType")]
    public string MessageType { get; set; } = OfflineBridgeConstants.MessageTypeRequest;

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; set; }

    [JsonPropertyName("ok")]
    public bool? Ok { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }
}
