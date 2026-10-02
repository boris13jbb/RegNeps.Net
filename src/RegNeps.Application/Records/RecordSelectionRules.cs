namespace RegNeps.Application.Records;

/// <summary>
/// Reglas de selección múltiple en listados (Registros): memoria de UI, no persistida.
/// </summary>
public static class RecordSelectionRules
{
    /// <summary>Al cambiar filtros o de página la selección se vacía por completo.</summary>
    public static void ClearOnPageOrFilterChange(HashSet<Guid> selection) =>
        selection.Clear();

    /// <summary>Deja solo IDs presentes en la página visible (poda fantasmas tras reload).</summary>
    public static void PruneToVisible(HashSet<Guid> selection, IEnumerable<Guid> visiblePageIds)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var visible = visiblePageIds as HashSet<Guid> ?? visiblePageIds.ToHashSet();
        selection.RemoveWhere(id => !visible.Contains(id));
    }

    public static bool CanEditSelection(int selectedCount) => selectedCount == 1;
}
