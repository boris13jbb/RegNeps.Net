namespace RegNeps.OfflineStore.Abstractions;

/// <summary>
/// FASE 2G — defensa en profundidad: nunca persistir cookies/Bearer como «material seguro».
/// </summary>
public static class SecureAuthMaterialGuard
{
    public static bool LooksLikeHttpCookieOrBearer(string? material)
    {
        if (string.IsNullOrWhiteSpace(material))
        {
            return false;
        }

        var m = material.Trim();
        if (m.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (m.Contains("RegNeps.Auth", StringComparison.OrdinalIgnoreCase) ||
            m.Contains(".AspNetCore.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static void EnsureNotAuthCookieOrBearer(string? material)
    {
        if (LooksLikeHttpCookieOrBearer(material))
        {
            throw new ArgumentException(
                "No se permite almacenar cookies ni tokens de autenticación en el material seguro.");
        }
    }
}
