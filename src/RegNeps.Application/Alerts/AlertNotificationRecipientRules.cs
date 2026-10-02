namespace RegNeps.Application.Alerts;

/// <summary>
/// Reglas puras para decidir quién debe recibir push de alerta crítica en tiempo real.
/// </summary>
public static class AlertNotificationRecipientRules
{
    public static bool OwnsRecord(
        string candidateUserId,
        string? candidateExternalUserId,
        string? recordCreatedByUserId)
    {
        if (string.IsNullOrWhiteSpace(recordCreatedByUserId))
        {
            return false;
        }

        if (string.Equals(recordCreatedByUserId, candidateUserId, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(candidateExternalUserId)
               && string.Equals(recordCreatedByUserId, candidateExternalUserId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Destinatario: activo, AlertasActivas, ViewAlerts y puede ver el registro (SeesAllRecords o dueño).
    /// </summary>
    public static bool ShouldReceiveCriticalAlert(
        bool alertasActivas,
        bool isActive,
        bool hasViewAlerts,
        bool seesAllRecords,
        string candidateUserId,
        string? candidateExternalUserId,
        string? recordCreatedByUserId)
    {
        if (!alertasActivas || !isActive || !hasViewAlerts)
        {
            return false;
        }

        if (seesAllRecords)
        {
            return true;
        }

        return OwnsRecord(candidateUserId, candidateExternalUserId, recordCreatedByUserId);
    }
}
