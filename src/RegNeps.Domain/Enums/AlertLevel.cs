namespace RegNeps.Domain.Enums;

/// <summary>Nivel de alerta según cantidad de neps.</summary>
public enum AlertLevel
{
    Normal = 0,
    Advertencia = 1,
    Critico = 2
}

public static class AlertLevelExtensions
{
    public static string ToDisplayLabel(this AlertLevel level) => level switch
    {
        AlertLevel.Normal => "Normal",
        AlertLevel.Advertencia => "Advertencia",
        AlertLevel.Critico => "Crítico",
        _ => level.ToString()
    };
}
