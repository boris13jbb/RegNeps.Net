using RegNeps.Domain.Constants;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Services;
using RegNeps.OfflineStore.Enums;

namespace RegNeps.OfflineStore.Entities;

/// <summary>Registro local mínimo para captura/consulta offline (no réplica central).</summary>
public sealed class LocalNepRecord
{
    public Guid Id { get; set; }

    public string ClientOperationId { get; set; } = string.Empty;

    public string? CaptureSessionId { get; set; }

    public string Telar { get; set; } = string.Empty;

    public double Neps { get; set; }

    public string Tela { get; set; } = string.Empty;

    public string LoteTrama { get; set; } = string.Empty;

    public string Turno { get; set; } = string.Empty;

    public string Operario { get; set; } = string.Empty;

    public string LineaProduccion { get; set; } = string.Empty;

    public string Observacion { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>Token de concurrencia del servidor (réplica). Vacío hasta el primer sync.</summary>
    public string? ConcurrencyStamp { get; set; }

    public string UserId { get; set; } = string.Empty;

    public LocalSyncStatus SyncStatus { get; set; } = LocalSyncStatus.PendingSync;

    /// <summary>Id del registro en el servidor tras Create Accepted/Duplicate (puede diferir del Id local).</summary>
    public Guid? ServerRecordId { get; set; }

    /// <summary>Tombstone local: el registro fue eliminado en servidor; no debe reaparecer.</summary>
    public bool IsDeleted { get; set; }

    public string AccionCorrectiva { get; set; } = string.Empty;

    public string ResponsableRevision { get; set; } = string.Empty;

    public bool RevisadoPorSupervisor { get; set; }

    public DateTime? FechaRevisionUtc { get; set; }

    public double MtsCalculados => Neps / NepsConstants.TestLengthM;

    public AlertLevel GetAlertLevel() => AlertEvaluator.GetLevel(Neps);

    public string GetQualityLabel() => GetAlertLevel().ToDisplayLabel();

    public PendingOperation? PendingOperation { get; set; }
}
