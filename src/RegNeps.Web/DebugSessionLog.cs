using System.Text.Json;

/// <summary>Instrumentación temporal de auditoría (sesión debug 9dffda). No registrar secretos.</summary>
internal static class DebugSessionLog
{
    private static readonly object Gate = new();

    public static void Write(string hypothesisId, string location, string message, object? data = null)
    {
        // #region agent log
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["sessionId"] = "9dffda",
                ["hypothesisId"] = hypothesisId,
                ["location"] = location,
                ["message"] = message,
                ["data"] = data,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            var line = JsonSerializer.Serialize(payload) + Environment.NewLine;
            lock (Gate)
            {
                foreach (var path in CandidatePaths())
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }

                        File.AppendAllText(path, line);
                        break;
                    }
                    catch
                    {
                        // Probar siguiente ruta candidata.
                    }
                }
            }
        }
        catch
        {
            // No interrumpir el flujo de la app por fallos de telemetría.
        }
        // #endregion
    }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "debug-9dffda.log"));
        yield return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "debug-9dffda.log"));
        yield return Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "debug-9dffda.log"));
    }
}
