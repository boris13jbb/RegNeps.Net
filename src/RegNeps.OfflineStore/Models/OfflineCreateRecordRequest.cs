namespace RegNeps.OfflineStore.Models;

public sealed class OfflineCreateRecordRequest
{
    public string Telar { get; set; } = string.Empty;
    public double Neps { get; set; }
    public string Tela { get; set; } = string.Empty;
    public string LoteTrama { get; set; } = string.Empty;
    public string Turno { get; set; } = string.Empty;
    public string Operario { get; set; } = string.Empty;
    public string LineaProduccion { get; set; } = string.Empty;
    public string Observacion { get; set; } = string.Empty;

    /// <summary>Si es null, se genera una sesión nueva o se reutiliza la activa del servicio.</summary>
    public string? CaptureSessionId { get; set; }
}
