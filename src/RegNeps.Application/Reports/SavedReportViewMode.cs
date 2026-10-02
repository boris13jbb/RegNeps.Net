namespace RegNeps.Application.Reports;

/// <summary>
/// Modo de visualización al abrir un informe guardado en Registros (<c>?reportId=</c>).
/// </summary>
public static class SavedReportViewMode
{
    public static bool IsReadOnlyView(Guid? openedReportId) => openedReportId is not null;
}
