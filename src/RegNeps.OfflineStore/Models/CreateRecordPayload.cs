namespace RegNeps.OfflineStore.Models;

/// <summary>Payload versionado almacenado en PendingOperation.PayloadJson (protocolo v1).</summary>
public sealed class CreateRecordPayload
{
    public int ProtocolVersion { get; set; } = OfflineStoreConstants.ProtocolVersion;
    public string Telar { get; set; } = string.Empty;
    public double Neps { get; set; }
    public string Tela { get; set; } = string.Empty;
    public string LoteTrama { get; set; } = string.Empty;
    public string Turno { get; set; } = string.Empty;
    public string Operario { get; set; } = string.Empty;
    public string LineaProduccion { get; set; } = string.Empty;
    public string Observacion { get; set; } = string.Empty;
    public string ClientOperationId { get; set; } = string.Empty;
    public string? CaptureSessionId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
