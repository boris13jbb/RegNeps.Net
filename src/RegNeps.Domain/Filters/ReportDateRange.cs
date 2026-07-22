using System.Globalization;

namespace RegNeps.Domain.Filters;

/// <summary>
/// Rango de fechas de reporte en UTC, inclusivo en días calendario locales.
/// Usa intervalo semiabierto [FromUtc, ToExclusiveUtc) para no perder el día "Hasta".
/// </summary>
public readonly record struct ReportDateRange(DateTime FromUtc, DateTime ToExclusiveUtc)
{
    public DateTime ToInclusiveUtc => ToExclusiveUtc.AddTicks(-1);

    public string LabelLocal =>
        $"{FromUtc.ToLocalTime():dd/MM/yyyy} – {ToInclusiveUtc.ToLocalTime():dd/MM/yyyy}";

    /// <summary>
    /// Construye el rango a partir de fechas de calendario (solo día), en zona local del servidor.
    /// Incluye ambos extremos (desde 00:00 del día inicial hasta 23:59:59.999… del día final).
    /// </summary>
    public static ReportDateRange FromLocalCalendarDates(DateTime fromDate, DateTime toDate)
    {
        var from = DateTime.SpecifyKind(fromDate.Date, DateTimeKind.Local);
        var to = DateTime.SpecifyKind(toDate.Date, DateTimeKind.Local);

        if (from > to)
        {
            throw new ArgumentException(
                "La fecha inicial no puede ser posterior a la fecha final.");
        }

        var fromUtc = from.ToUniversalTime();
        var toExclusiveUtc = to.AddDays(1).ToUniversalTime();
        return new ReportDateRange(fromUtc, toExclusiveUtc);
    }

    /// <summary>
    /// Interpreta query string yyyy-MM-dd (o DateTime) como días de calendario local.
    /// Evita el fallo típico de model-binding que trata la fecha como UTC y colapsa el rango.
    /// </summary>
    public static bool TryParseQuery(
        string? fromText,
        string? toText,
        out ReportDateRange range,
        out string? error)
    {
        range = default;
        error = null;

        if (string.IsNullOrWhiteSpace(fromText) || string.IsNullOrWhiteSpace(toText))
        {
            error = "Debe indicar fecha inicial y fecha final.";
            return false;
        }

        if (!TryParseCalendarDate(fromText, out var fromDate) ||
            !TryParseCalendarDate(toText, out var toDate))
        {
            error = "Formato de fecha inválido. Use AAAA-MM-DD.";
            return false;
        }

        try
        {
            range = FromLocalCalendarDates(fromDate, toDate);
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryParseCalendarDate(string text, out DateTime date)
    {
        text = text.Trim();
        if (DateOnly.TryParseExact(
                text,
                ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var only))
        {
            date = only.ToDateTime(TimeOnly.MinValue);
            return true;
        }

        if (DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var dt))
        {
            date = dt.Date;
            return true;
        }

        date = default;
        return false;
    }

    public void ApplyTo(RecordFilters filters)
    {
        filters.FromUtc = FromUtc;
        filters.ToUtc = ToInclusiveUtc;
        filters.ToExclusiveUtc = ToExclusiveUtc;
    }

    /// <summary>
    /// Asegura intervalo [inicio, fin] por día calendario local en filtros persistidos o parciales.
    /// Corrige casos legacy donde solo se guardó ToUtc a medianoche (un solo día efectivo).
    /// </summary>
    public static void EnsureConsolidatedRange(RecordFilters filters)
    {
        if (filters.FromUtc is null)
        {
            return;
        }

        if (filters.ToExclusiveUtc is not null)
        {
            return;
        }

        if (filters.ToUtc is null)
        {
            return;
        }

        var fromDay = filters.FromUtc.Value.ToLocalTime().Date;
        var toDay = filters.ToUtc.Value.ToLocalTime().Date;
        FromLocalCalendarDates(fromDay, toDay).ApplyTo(filters);
    }
}
