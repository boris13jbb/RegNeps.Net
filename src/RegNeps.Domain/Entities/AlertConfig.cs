namespace RegNeps.Domain.Entities;

/// <summary>Umbrales de alerta (paridad con AlertConfig Flutter).</summary>
public sealed class AlertConfig
{
    public int Id { get; set; } = 1;
    public int LimiteNormalMax { get; set; } = 30;
    public int LimiteAdvertenciaMax { get; set; } = 60;
    public int CantidadReincidenciasCriticas { get; set; } = 3;
    public int DiasParaReincidencia { get; set; } = 1;
    public bool AlertasActivas { get; set; } = true;

    public int LimiteCriticoMin => LimiteAdvertenciaMax + 1;
    public int VentanaReincidenciasHoras => Math.Max(1, DiasParaReincidencia) * 24;
}
