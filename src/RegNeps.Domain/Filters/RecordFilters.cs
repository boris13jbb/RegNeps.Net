using RegNeps.Domain.Enums;

namespace RegNeps.Domain.Filters;

/// <summary>Filtros de registros (paridad con RecordFilters Flutter).</summary>
public sealed class RecordFilters
{
    public string? Telar { get; set; }
    public string? Tela { get; set; }
    public string? LoteTrama { get; set; }
    public string? Turno { get; set; }
    public string? Operario { get; set; }
    public string? LineaProduccion { get; set; }
    public string? Search { get; set; }
    public double? NepsMin { get; set; }
    public double? NepsMax { get; set; }
    public double? MtsMin { get; set; }
    public double? MtsMax { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }

    /// <summary>
    /// Fin exclusivo del rango (CreatedAt &lt; ToExclusiveUtc). Preferido sobre ToUtc inclusivo.
    /// </summary>
    public DateTime? ToExclusiveUtc { get; set; }
    public AlertLevel? AlertLevel { get; set; }
    public bool? RevisadoPorSupervisor { get; set; }
    public bool? ConAccionCorrectiva { get; set; }
    public bool SoloPendientes { get; set; }
}
