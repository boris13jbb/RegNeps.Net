using RegNeps.Domain.Enums;

namespace RegNeps.Domain.Constants;

/// <summary>
/// Criterios oficiales de calificación NEPS (única fuente de verdad).
/// El campo <c>NepRecord.Neps</c> es el puntaje Q (conteo en 0.09 m²).
/// NEPS/m² = Neps / <see cref="NepsConstants.TestLengthM"/>.
/// </summary>
/// <remarks>
/// <para>
/// Equivalencia con Q entero: Q=18 → 200 NEPS/m²; Q=45 → 500; Q=54 → 600.
/// Ante Neps fraccionarios, la clasificación operativa usa Q redondeado
/// (<see cref="MidpointRounding.AwayFromZero"/>) vía <see cref="ClassifyByNeps"/>.
/// <see cref="ClassifyByNepsPerM2"/> aplica las bandas de densidad de forma continua
/// (para leyenda/tests); con Q entero ambas coinciden.
/// </para>
/// </remarks>
public static class NepsQualityCriteria
{
    public const int OkScoreMax = 18;
    public const int MentionScoreMax = 45;
    public const int CriticalAdjustmentScoreMax = 54;

    public const double OkNepsPerM2Max = 200d;
    public const double MentionNepsPerM2Max = 500d;
    public const double CriticalAdjustmentNepsPerM2Max = 600d;

    /// <summary>Cota exclusiva superior de Neps cuyo redondeo AwayFromZero es ≤ <see cref="OkScoreMax"/>.</summary>
    public const double OkNepsExclusiveUpper = OkScoreMax + 0.5d;

    /// <summary>Cota exclusiva superior de Neps cuyo redondeo es ≤ <see cref="MentionScoreMax"/>.</summary>
    public const double MentionNepsExclusiveUpper = MentionScoreMax + 0.5d;

    /// <summary>Cota exclusiva superior de Neps cuyo redondeo es ≤ <see cref="CriticalAdjustmentScoreMax"/>.</summary>
    public const double CriticalAdjustmentNepsExclusiveUpper = CriticalAdjustmentScoreMax + 0.5d;

    public static int ToScore(double neps) =>
        (int)Math.Round(neps, MidpointRounding.AwayFromZero);

    /// <summary>Clasificación operativa del registro (Q = redondeo de Neps).</summary>
    public static AlertLevel ClassifyByNeps(double neps) =>
        ClassifyByScore(ToScore(neps));

    public static AlertLevel ClassifyByScore(int q)
    {
        if (q <= OkScoreMax)
        {
            return AlertLevel.Ok;
        }

        if (q <= MentionScoreMax)
        {
            return AlertLevel.Mention;
        }

        if (q <= CriticalAdjustmentScoreMax)
        {
            return AlertLevel.CriticalAdjustment;
        }

        return AlertLevel.SecondQuality;
    }

    /// <summary>
    /// Clasificación por densidad NEPS/m² (bandas continuas oficiales).
    /// Con Q entero y NEPS/m² = Q/0.09 coincide con <see cref="ClassifyByScore"/>.
    /// </summary>
    public static AlertLevel ClassifyByNepsPerM2(double nepsPerM2)
    {
        if (nepsPerM2 <= OkNepsPerM2Max)
        {
            return AlertLevel.Ok;
        }

        if (nepsPerM2 <= MentionNepsPerM2Max)
        {
            return AlertLevel.Mention;
        }

        if (nepsPerM2 <= CriticalAdjustmentNepsPerM2Max)
        {
            return AlertLevel.CriticalAdjustment;
        }

        return AlertLevel.SecondQuality;
    }

    /// <summary>Niveles que disparan notificación/alerta crítica (SignalR), si AlertasActivas.</summary>
    public static bool IsCriticalNotificationLevel(AlertLevel level) =>
        level is AlertLevel.CriticalAdjustment or AlertLevel.SecondQuality;

    public static bool RequiresFollowUp(AlertLevel level) =>
        level != AlertLevel.Ok;
}
