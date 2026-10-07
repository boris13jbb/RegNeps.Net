namespace RegNeps.Domain.Enums;

/// <summary>Calificación oficial de calidad NEPS (criterios VICUNHA).</summary>
public enum AlertLevel
{
    Ok = 0,
    Mention = 1,
    CriticalAdjustment = 2,
    SecondQuality = 3
}

public static class AlertLevelExtensions
{
    public static string ToDisplayLabel(this AlertLevel level) => level switch
    {
        AlertLevel.Ok => "OK",
        AlertLevel.Mention => "Mención",
        AlertLevel.CriticalAdjustment => "Crítico - Realizar Ajuste",
        AlertLevel.SecondQuality => "2da Calidad",
        _ => level.ToString()
    };

    /// <summary>
    /// Parsea filtros de UI/API. Acepta nombres oficiales y alias legacy
    /// (Normal→Ok, Advertencia→Mention, Critico→CriticalAdjustment).
    /// </summary>
    public static bool TryParseFilter(string? value, out AlertLevel level)
    {
        level = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (Enum.TryParse(value, ignoreCase: true, out level)
            && Enum.IsDefined(typeof(AlertLevel), level))
        {
            return true;
        }

        level = value.Trim().ToLowerInvariant() switch
        {
            "normal" or "ok" => AlertLevel.Ok,
            "advertencia" or "mencion" or "mención" => AlertLevel.Mention,
            "critico" or "crítico" or "critical" => AlertLevel.CriticalAdjustment,
            "2da" or "2dacalidad" or "segunda" or "segunda calidad" => AlertLevel.SecondQuality,
            _ => (AlertLevel)(-1)
        };

        return Enum.IsDefined(typeof(AlertLevel), level);
    }
}
