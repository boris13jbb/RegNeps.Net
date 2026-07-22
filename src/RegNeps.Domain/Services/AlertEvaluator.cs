using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;

namespace RegNeps.Domain.Services;

/// <summary>Evalúa nivel de alerta (paridad AlertService Flutter).</summary>
public static class AlertEvaluator
{
    public static AlertLevel GetLevel(double neps, AlertConfig config)
    {
        if (!config.AlertasActivas)
        {
            return AlertLevel.Normal;
        }

        var value = (int)Math.Round(neps, MidpointRounding.AwayFromZero);
        if (value <= config.LimiteNormalMax)
        {
            return AlertLevel.Normal;
        }

        if (value <= config.LimiteAdvertenciaMax)
        {
            return AlertLevel.Advertencia;
        }

        return AlertLevel.Critico;
    }

    public static IReadOnlyList<string> GetRecommendations(AlertLevel level, bool reincidencia = false)
    {
        var list = new List<string>();
        switch (level)
        {
            case AlertLevel.Advertencia:
                list.Add("Verificar tensión y alimentación de trama.");
                list.Add("Revisar limpieza del telar y zona de trama.");
                list.Add("Registrar observación y notificar al supervisor si persiste.");
                break;
            case AlertLevel.Critico:
                list.Add("Detener o reducir velocidad según procedimiento de planta.");
                list.Add("Inspeccionar trama, peines y zona de inserción.");
                list.Add("Aplicar acción correctiva y marcar revisión de supervisor.");
                list.Add("Notificar al supervisor de inmediato.");
                break;
            default:
                list.Add("Medición dentro de rango normal. Continuar monitoreo rutinario.");
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
            .Where(r => GetLevel(r.Neps, config) == AlertLevel.Critico)
            .OrderBy(r => r.CreatedAt)
            .ToList();

        return criticals.Count >= config.CantidadReincidenciasCriticas;
    }
}
