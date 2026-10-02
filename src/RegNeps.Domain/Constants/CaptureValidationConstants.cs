namespace RegNeps.Domain.Constants;

/// <summary>
/// Umbrales y ventanas de validación en captura (paridad con Flutter).
/// </summary>
public static class CaptureValidationConstants
{
    /// <summary>Neps por encima de este valor exigen confirmación explícita del operador.</summary>
    public const double NepsConfirmationThreshold = 100;

    /// <summary>Ventana en minutos para detectar duplicados recientes (mismo usuario y medición).</summary>
    public const int DuplicateWindowMinutes = 2;
}
