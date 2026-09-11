using RegNeps.Domain.Constants;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Services;

namespace RegNeps.Domain.Entities;

/// <summary>Registro de medición de neps (equivalente a NepRecord en Flutter).</summary>
public sealed class NepRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Telar { get; set; } = string.Empty;
    public double Neps { get; set; }
    public string Tela { get; set; } = string.Empty;
    public string LoteTrama { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Turno { get; set; } = string.Empty;
    public string Operario { get; set; } = string.Empty;
    public string LineaProduccion { get; set; } = string.Empty;
    public string Observacion { get; set; } = string.Empty;
    public bool RevisadoPorSupervisor { get; set; }
    public string AccionCorrectiva { get; set; } = string.Empty;
    public string ResponsableRevision { get; set; } = string.Empty;
    public DateTime? FechaRevision { get; set; }
    public string? CreatedByUserId { get; set; }
    public string? CreatedByEmail { get; set; }
    public string? CreatedByRole { get; set; }
    public DateTime? UpdatedAt { get; set; }

    /// <summary>Token de concurrencia optimista (regenerado en cada actualización).</summary>
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Idempotencia de creación por usuario (anti doble clic / reintento).
    /// No es clave de negocio: el mismo telar+lote de dos usuarios produce dos filas.
    /// </summary>
    public string? ClientOperationId { get; set; }

    /// <summary>
    /// Sesión de captura en pantalla (muchas mediciones por sesión).
    /// Distinto de <see cref="ClientOperationId"/>. Históricos sin sesión quedan en NULL.
    /// </summary>
    public string? CaptureSessionId { get; set; }

    public List<CorrectiveActionEntry> HistorialAcciones { get; set; } = [];

    /// <summary>Metros calculados: Neps / 0.09</summary>
    public double MtsCalculados => Neps / NepsConstants.TestLengthM;

    public AlertLevel GetAlertLevel(AlertConfig? config = null) =>
        AlertEvaluator.GetLevel(Neps, config ?? new AlertConfig());

    public bool RequiereSeguimiento(AlertConfig? config = null) =>
        GetAlertLevel(config) != AlertLevel.Normal && !RevisadoPorSupervisor;
}
