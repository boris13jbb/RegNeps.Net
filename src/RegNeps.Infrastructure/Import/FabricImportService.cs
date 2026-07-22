using ClosedXML.Excel;
using RegNeps.Application.Abstractions;
using RegNeps.Domain.Entities;

namespace RegNeps.Infrastructure.Import;

/// <summary>
/// Importa catálogo de telas desde Excel (paridad Flutter fabric_catalog_service).
/// Acepta columna única "Tela"/"Nombre" o Nombre + Codigo.
/// </summary>
public sealed class FabricImportService : IFabricImportService
{
    private readonly IFabricRepository _fabrics;

    public FabricImportService(IFabricRepository fabrics) => _fabrics = fabrics;

    public async Task<FabricImportResult> ImportExcelAsync(Stream stream, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var added = 0;
        var updated = 0;
        var skipped = 0;

        using var workbook = new XLWorkbook(stream);
        var existing = (await _fabrics.GetAllAsync(ct))
            .ToDictionary(f => f.Name.Trim(), f => f, StringComparer.OrdinalIgnoreCase);
        var seenInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sheet in workbook.Worksheets)
        {
            ct.ThrowIfCancellationRequested();
            var firstRow = sheet.FirstRowUsed();
            if (firstRow is null)
            {
                continue;
            }

            var map = BuildHeaderMap(firstRow);
            var nameCol = FindColumn(map, "NOMBRE", "TELA", "FABRIC", "NAME");
            var codeCol = FindColumn(map, "CODIGO", "CÓDIGO", "CODE", "COD");

            // Sin encabezado reconocible: primera columna de datos (estilo Flutter).
            if (nameCol is null)
            {
                nameCol = 1;
            }

            var startRow = firstRow.RowNumber();
            var headerCell = sheet.Cell(startRow, nameCol.Value).GetString().Trim();
            if (IsHeaderValue(headerCell))
            {
                startRow++;
            }

            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? startRow - 1;
            for (var rowNum = startRow; rowNum <= lastRow; rowNum++)
            {
                ct.ThrowIfCancellationRequested();
                var row = sheet.Row(rowNum);
                if (row.IsEmpty())
                {
                    continue;
                }

                var name = row.Cell(nameCol.Value).GetString().Trim();
                if (string.IsNullOrWhiteSpace(name) || IsHeaderValue(name))
                {
                    continue;
                }

                if (!seenInFile.Add(name))
                {
                    skipped++;
                    continue;
                }

                string? code = null;
                if (codeCol is not null)
                {
                    var rawCode = row.Cell(codeCol.Value).GetString().Trim();
                    code = string.IsNullOrWhiteSpace(rawCode) ? null : rawCode;
                }

                if (existing.TryGetValue(name, out var fabric))
                {
                    var changed = false;
                    if (!string.Equals(fabric.Code, code, StringComparison.Ordinal))
                    {
                        fabric.Code = code;
                        changed = true;
                    }

                    if (!fabric.IsActive)
                    {
                        fabric.IsActive = true;
                        changed = true;
                    }

                    if (changed)
                    {
                        await _fabrics.UpdateAsync(fabric, ct);
                        updated++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                else
                {
                    var created = await _fabrics.AddAsync(new Fabric
                    {
                        Name = name,
                        Code = code,
                        IsActive = true
                    }, ct);
                    existing[created.Name] = created;
                    added++;
                }
            }
        }

        if (added == 0 && updated == 0 && skipped == 0 && errors.Count == 0)
        {
            errors.Add("No se encontraron telas válidas en el archivo.");
        }

        return new FabricImportResult
        {
            Added = added,
            Updated = updated,
            Skipped = skipped,
            Errors = errors
        };
    }

    private static Dictionary<int, string> BuildHeaderMap(IXLRow headerRow)
    {
        var map = new Dictionary<int, string>();
        foreach (var cell in headerRow.CellsUsed())
        {
            var key = NormalizeHeader(cell.GetString());
            if (!string.IsNullOrEmpty(key))
            {
                map[cell.Address.ColumnNumber] = key;
            }
        }

        return map;
    }

    private static int? FindColumn(Dictionary<int, string> map, params string[] aliases)
    {
        foreach (var (col, header) in map)
        {
            foreach (var alias in aliases)
            {
                if (string.Equals(header, NormalizeHeader(alias), StringComparison.Ordinal))
                {
                    return col;
                }
            }
        }

        return null;
    }

    private static string NormalizeHeader(string value) =>
        value.Trim().ToUpperInvariant()
            .Replace('Á', 'A').Replace('É', 'E').Replace('Í', 'I')
            .Replace('Ó', 'O').Replace('Ú', 'U');

    private static bool IsHeaderValue(string value)
    {
        var n = NormalizeHeader(value);
        return n is "TELA" or "NOMBRE" or "FABRIC" or "NAME" or "CODIGO" or "CODE" or "ACTIVO" or "CREADO";
    }
}
