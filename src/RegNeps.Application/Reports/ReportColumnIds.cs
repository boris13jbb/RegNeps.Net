namespace RegNeps.Application.Reports;

/// <summary>
/// Identificadores estables de columnas del informe consolidado (CSV / Excel / PDF).
/// </summary>
public static class ReportColumnIds
{
    public const string Nro = "nro";
    public const string Fecha = "fecha";
    public const string Lote = "lote";
    public const string Tela = "tela";
    public const string Telar = "telar";
    public const string Neps = "neps";
    public const string Mts = "mts";
    public const string Estado = "estado";
    public const string Observacion = "observacion";
    public const string Recomendacion = "recomendacion";

    public static readonly IReadOnlyList<string> All =
    [
        Nro, Fecha, Lote, Tela, Telar, Neps, Mts, Estado, Observacion, Recomendacion
    ];

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [Nro] = "Nro",
        [Fecha] = "Fecha",
        [Lote] = "Lote de trama",
        [Tela] = "Tela",
        [Telar] = "Telar",
        [Neps] = "Neps",
        [Mts] = "Mts calculados",
        [Estado] = "Estado alerta",
        [Observacion] = "Observación",
        [Recomendacion] = "Recomendación"
    };

    /// <summary>
    /// Normaliza la selección del usuario. Si viene vacía o nula, usa todas las columnas.
    /// Conserva el orden canónico de <see cref="All"/>.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? selected)
    {
        if (selected is null)
            return All;

        var set = new HashSet<string>(
            selected
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        if (set.Count == 0)
            return All;

        var ordered = All.Where(id => set.Contains(id)).ToList();
        return ordered.Count == 0 ? All : ordered;
    }

    public static bool Includes(IReadOnlyList<string> columns, string id) =>
        columns.Any(c => string.Equals(c, id, StringComparison.OrdinalIgnoreCase));
}
