namespace RegNeps.Domain.Entities;

/// <summary>Entrada del historial de acciones correctivas.</summary>
public sealed class CorrectiveActionEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid NepRecordId { get; set; }
    public NepRecord? NepRecord { get; set; }
    public string Accion { get; set; } = string.Empty;
    public string Responsable { get; set; } = string.Empty;
    public DateTime Fecha { get; set; } = DateTime.UtcNow;
}
