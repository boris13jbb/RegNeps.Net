namespace RegNeps.Application.Records;

/// <summary>
/// Resultado explícito de intentar entregar un archivo o texto para compartir.
/// </summary>
public enum ShareDeliveryResult
{
    Shared = 0,
    NativeRequested = 1,
    Copied = 2,
    Cancelled = 3,
    Unsupported = 4,
    Failed = 5,
    Downloaded = 6
}

/// <summary>
/// Política testeable: qué hacer con la selección y qué mensaje mostrar.
/// </summary>
public static class ShareDeliveryPolicy
{
    /// <summary>
    /// Solo un "shared" real podría limpiar, y únicamente si la UI lo pide.
    /// Captura usa clearOnSharedSuccess=false para conservar la selección del usuario.
    /// </summary>
    public static bool ShouldClearSelection(ShareDeliveryResult result, bool clearOnSharedSuccess) =>
        clearOnSharedSuccess && result == ShareDeliveryResult.Shared;

    public static ShareDeliveryResult Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ShareDeliveryResult.Failed;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "shared" => ShareDeliveryResult.Shared,
            "native-requested" => ShareDeliveryResult.NativeRequested,
            "copied" => ShareDeliveryResult.Copied,
            "cancelled" => ShareDeliveryResult.Cancelled,
            "unsupported" => ShareDeliveryResult.Unsupported,
            "failed" => ShareDeliveryResult.Failed,
            "downloaded" => ShareDeliveryResult.Downloaded,
            _ => ShareDeliveryResult.Failed
        };
    }

    public static string? UserMessage(ShareDeliveryResult result, int recordCount)
    {
        return result switch
        {
            ShareDeliveryResult.Shared => "Archivo compartido.",
            ShareDeliveryResult.NativeRequested => "Se abrió el menú para compartir.",
            ShareDeliveryResult.Downloaded => "El archivo se descargó.",
            ShareDeliveryResult.Copied =>
                "No se pudo abrir el menú de compartir. El contenido fue copiado al portapapeles.",
            ShareDeliveryResult.Cancelled => null,
            ShareDeliveryResult.Unsupported =>
                "No se pudo abrir el menú de compartir en este dispositivo.",
            ShareDeliveryResult.Failed => "No se pudo compartir el archivo.",
            _ => "No se pudo compartir el archivo."
        };
    }

    public static bool IsError(ShareDeliveryResult result) =>
        result is ShareDeliveryResult.Unsupported or ShareDeliveryResult.Failed;
}
