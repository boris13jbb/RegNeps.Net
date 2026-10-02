namespace RegNeps.OfflineStore.Models;

/// <summary>
/// Payload Outbox UpdateRecord (camelCase wire = SyncUpdateRecordPayload).
/// No incluye identidad de usuario ni stamp en payload de negocio (stamp va en PendingOperation).
/// </summary>
public sealed class UpdateRecordPayload
{
    public Guid EntityId { get; set; }
    public string Telar { get; set; } = string.Empty;
    public double Neps { get; set; }
    public string Tela { get; set; } = string.Empty;
    public string LoteTrama { get; set; } = string.Empty;
    public string Turno { get; set; } = string.Empty;
    public string Operario { get; set; } = string.Empty;
    public string LineaProduccion { get; set; } = string.Empty;
    public string Observacion { get; set; } = string.Empty;
}
