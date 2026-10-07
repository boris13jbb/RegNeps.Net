using RegNeps.OfflineStore.Enums;

namespace RegNeps.OfflineStore.Entities;

/// <summary>Catálogo local mínimo (telas/lotes) para combos de captura offline.</summary>
public sealed class LocalCatalogItem
{
    public Guid Id { get; set; }

    public LocalCatalogKind Kind { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime UpdatedAtUtc { get; set; }
}
