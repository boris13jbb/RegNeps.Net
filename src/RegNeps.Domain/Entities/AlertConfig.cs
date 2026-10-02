namespace RegNeps.Domain.Entities;

/// <summary>
/// Configuración operativa de alertas (activación y reincidencia).
/// Los umbrales de calidad oficiales viven en <see cref="Constants.NepsQualityCriteria"/>.
/// </summary>
public sealed class AlertConfig
{
    public int Id { get; set; } = 1;

    /// <summary>
    /// Legacy/deprecado: ya no determina la calificación de calidad
    /// (fuente de verdad: <c>NepsQualityCriteria</c>). Se conserva en BD por compatibilidad.
    /// </summary>
    public int LimiteNormalMax { get; set; } = 18;

    /// <summary>
    /// Legacy/deprecado: ya no determina la calificación de calidad
    /// (fuente de verdad: <c>NepsQualityCriteria</c>). Se conserva en BD por compatibilidad.
    /// </summary>
    public int LimiteAdvertenciaMax { get; set; } = 45;

    public int CantidadReincidenciasCriticas { get; set; } = 3;
    public int DiasParaReincidencia { get; set; } = 1;

    /// <summary>
    /// Si es false, no se envían notificaciones automáticas (p. ej. SignalR).
    /// No altera la calificación oficial de calidad.
    /// </summary>
    public bool AlertasActivas { get; set; } = true;

    /// <summary>Legacy: derivado de <see cref="LimiteAdvertenciaMax"/>; no usar para calificación.</summary>
    public int LimiteCriticoMin => LimiteAdvertenciaMax + 1;

    public int VentanaReincidenciasHoras => Math.Max(1, DiasParaReincidencia) * 24;
}
