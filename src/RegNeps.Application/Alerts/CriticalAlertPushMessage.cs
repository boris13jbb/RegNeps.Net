namespace RegNeps.Application.Alerts;

/// <summary>
/// Mensaje mínimo en tiempo real; sin neps, tela ni datos sensibles del registro.
/// La UI completa se carga vía servicios que aplican permisos de lectura.
/// </summary>
public sealed record CriticalAlertPushMessage(
    Guid RecordId,
    DateTime CreatedAtUtc,
    string Summary = "Nueva alerta crítica registrada");
