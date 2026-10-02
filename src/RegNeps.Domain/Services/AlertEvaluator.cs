using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;

namespace RegNeps.Domain.Services;

/// <summary>
/// Evalúa calificación de calidad NEPS y reglas de reincidencia.
/// La calificación usa <see cref="NepsQualityCriteria"/> (no lee umbrales legacy 30/60).
/// </summary>
public static class AlertEvaluator
{
    /// <summary>
    /// Calificación oficial del registro. <paramref name="config"/> se ignora para umbrales
    /// (compatibilidad de firma); la fuente de verdad es <see cref="NepsQualityCriteria"/>.
    /// </summary>
    public static AlertLevel GetLevel(double neps, AlertConfig? config = null)
    {
        _ = config;
        return NepsQualityCriteria.ClassifyByNeps(neps);
    }

    public static IReadOnlyList<string> GetRecommendations(AlertLevel level, bool reincidencia = false)
    {
        var list = new List<string>();
        switch (level)
        {
            case AlertLevel.Mention:
                list.Add("Verificar tensión y alimentación de trama.");
                list.Add("Revisar limpieza del telar y zona de trama.");
                list.Add("Registrar observación y notificar al supervisor si persiste.");
                break;
            case AlertLevel.CriticalAdjustment:
                list.Add("Detener o reducir velocidad según procedimiento de planta.");
                list.Add("Inspeccionar trama, peines y zona de inserción.");
                list.Add("Aplicar acción correctiva y marcar revisión de supervisor.");
                list.Add("Notificar al supervisor de inmediato.");
                break;
            case AlertLevel.SecondQuality:
                list.Add("Clasificar producción según procedimiento de 2da calidad.");
                list.Add("Detener o aislar el telar según procedimiento de planta.");
                list.Add("Aplicar acción correctiva y notificar al supervisor de inmediato.");
                break;
            default:
                list.Add("Medición dentro de rango OK. Continuar monitoreo rutinario.");
                break;
        }

        if (reincidencia)
        {
            list.Add("Reincidencia crítica detectada: programar revisión técnica del telar.");
        }

        return list;
    }

    public static bool HasCriticalRecurrence(
        IEnumerable<NepRecord> records,
        string telar,
        AlertConfig config,
        DateTime? referenceUtc = null)
    {
        if (string.IsNullOrWhiteSpace(telar))
        {
            return false;
        }

        var now = referenceUtc ?? DateTime.UtcNow;
        var windowStart = now.AddDays(-Math.Max(1, config.DiasParaReincidencia));
        var criticals = records
            .Where(r => string.Equals(r.Telar, telar, StringComparison.OrdinalIgnoreCase))
            .Where(r => r.CreatedAt >= windowStart && r.CreatedAt <= now)
            .Where(r => NepsQualityCriteria.IsCriticalNotificationLevel(GetLevel(r.Neps, config)))
            .OrderBy(r => r.CreatedAt)
            .ToList();

        return criticals.Count >= config.CantidadReincidenciasCriticas;
    }
}
