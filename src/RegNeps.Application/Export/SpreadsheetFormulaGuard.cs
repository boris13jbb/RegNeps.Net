namespace RegNeps.Application.Export;

/// <summary>
/// Neutraliza inyección de fórmulas en celdas de texto al abrir CSV/Excel.
/// No debe aplicarse a valores numéricos (p. ej. Neps negativos).
/// </summary>
public static class SpreadsheetFormulaGuard
{
    public static bool IsFormulaInjectionRisk(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var c = value[0];
        return c is '=' or '+' or '-' or '@' or '\t' or '\r';
    }

    /// <summary>Prefija comilla simple si el texto es riesgoso para hojas de cálculo.</summary>
    public static string NeutralizeText(string? value)
    {
        value ??= string.Empty;
        return IsFormulaInjectionRisk(value) ? "'" + value : value;
    }
}
