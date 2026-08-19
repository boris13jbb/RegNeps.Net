using RegNeps.Domain.Constants;
using RegNeps.Domain.Enums;

namespace RegNeps.Domain.Entities;

/// <summary>Informe guardado (filtros, metadatos y copia de las filas).</summary>
public sealed class SavedReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }
    public string? CreatedByName { get; set; }

    /// <summary>JSON de filtros aplicados al guardar.</summary>
    public string FiltersJson { get; set; } = "{}";

    /// <summary>Cantidad de registros al momento del guardado.</summary>
    public int RecordCount { get; set; }

    /// <summary>Resumen visible en la lista de informes.</summary>
    public string? SummaryText { get; set; }

    /// <summary>Copia JSON de las filas al guardar, para poder reabrir el informe aunque se vacíe la tabla viva.</summary>
    public string? SnapshotJson { get; set; }
}

/// <summary>Entrada del catálogo de lotes/trama.</summary>
public sealed class LoteTramaItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class LoteTramaDefaults
{
    public static IReadOnlyList<string> Codes { get; } =
    [
        $"{NepsConstants.LoteTramaPrefix}H001",
        $"{NepsConstants.LoteTramaPrefix}H002",
        $"{NepsConstants.LoteTramaPrefix}H003",
        $"{NepsConstants.LoteTramaPrefix}H004",
        $"{NepsConstants.LoteTramaPrefix}H005",
        $"{NepsConstants.LoteTramaPrefix}H006",
        $"{NepsConstants.LoteTramaPrefix}H007",
        $"{NepsConstants.LoteTramaPrefix}H008",
        $"{NepsConstants.LoteTramaPrefix}A001",
        $"{NepsConstants.LoteTramaPrefix}A002",
        $"{NepsConstants.LoteTramaPrefix}B001",
        $"{NepsConstants.LoteTramaPrefix}B002",
        $"{NepsConstants.LoteTramaPrefix}C001",
        $"{NepsConstants.LoteTramaPrefix}C002",
        $"{NepsConstants.LoteTramaPrefix}D001",
        $"{NepsConstants.LoteTramaPrefix}D002"
    ];
}
