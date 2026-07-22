using System.Globalization;
using ClosedXML.Excel;
using RegNeps.Application.Abstractions;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;

namespace RegNeps.Infrastructure.Import;

public sealed class RecordImportService : IRecordImportService
{
    private readonly INepRecordRepository _records;

    public RecordImportService(INepRecordRepository records) => _records = records;

    public async Task<(int Imported, IReadOnlyList<string> Errors)> ImportExcelAsync(
        Stream stream,
        string? createdByUserId,
        string? createdByEmail,
        string? createdByRole,
        CancellationToken ct = default)
    {
        var errors = new List<string>();
        var imported = 0;

        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();
        var headerRow = sheet.FirstRowUsed()
            ?? throw new InvalidOperationException("El archivo Excel no contiene filas.");

        var map = BuildHeaderMap(headerRow);
        RequireHeaders(map, "TELAR", "NEPS");

        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var rowNum = headerRow.RowNumber() + 1; rowNum <= lastRow; rowNum++)
        {
            ct.ThrowIfCancellationRequested();
            var row = sheet.Row(rowNum);
            if (row.IsEmpty())
            {
                continue;
            }

            try
            {
                var telar = GetString(row, map, "TELAR");
                if (string.IsNullOrWhiteSpace(telar))
                {
                    errors.Add($"Fila {rowNum}: TELAR es obligatorio.");
                    continue;
                }

                if (!TryGetDouble(row, map, "NEPS", out var neps))
                {
                    errors.Add($"Fila {rowNum}: NEPS inválido.");
                    continue;
                }

                if (neps < 0)
                {
                    errors.Add($"Fila {rowNum}: NEPS no puede ser negativo.");
                    continue;
                }

                var lote = GetString(row, map, "LOTE");
                if (string.IsNullOrWhiteSpace(lote))
                {
                    lote = NepsConstants.LoteTramaPrefix;
                }

                var createdAt = TryGetDate(row, map, "FECHA") ?? DateTime.UtcNow;

                var record = new NepRecord
                {
                    Telar = telar.Trim(),
                    Neps = neps,
                    Tela = GetString(row, map, "TELA").Trim(),
                    LoteTrama = lote.Trim().ToUpperInvariant(),
                    Turno = GetString(row, map, "TURNO").Trim(),
                    Operario = GetString(row, map, "OPERARIO").Trim(),
                    LineaProduccion = GetString(row, map, "LINEA").Trim(),
                    Observacion = GetString(row, map, "OBSERVACION").Trim(),
                    CreatedAt = createdAt.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(createdAt, DateTimeKind.Local).ToUniversalTime()
                        : createdAt.ToUniversalTime(),
                    CreatedByUserId = createdByUserId,
                    CreatedByEmail = createdByEmail,
                    CreatedByRole = createdByRole
                };

                await _records.AddAsync(record, ct);
                imported++;
            }
            catch (Exception ex)
            {
                errors.Add($"Fila {rowNum}: {ex.Message}");
            }
        }

        return (imported, errors);
    }

    private static Dictionary<string, int> BuildHeaderMap(IXLRow headerRow)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.CellsUsed())
        {
            var key = NormalizeHeader(cell.GetString());
            if (!string.IsNullOrWhiteSpace(key) && !map.ContainsKey(key))
            {
                map[key] = cell.Address.ColumnNumber;
            }
        }

        return map;
    }

    private static string NormalizeHeader(string value) =>
        value.Trim().ToUpperInvariant()
            .Replace('Á', 'A').Replace('É', 'E').Replace('Í', 'I').Replace('Ó', 'O').Replace('Ú', 'U');

    private static void RequireHeaders(Dictionary<string, int> map, params string[] required)
    {
        foreach (var key in required)
        {
            if (!map.ContainsKey(key))
            {
                throw new InvalidOperationException($"Falta la columna obligatoria '{key}' en el Excel.");
            }
        }
    }

    private static string GetString(IXLRow row, Dictionary<string, int> map, string header)
    {
        if (!map.TryGetValue(header, out var col))
        {
            return string.Empty;
        }

        return row.Cell(col).GetFormattedString()?.Trim() ?? string.Empty;
    }

    private static bool TryGetDouble(IXLRow row, Dictionary<string, int> map, string header, out double value)
    {
        value = 0;
        if (!map.TryGetValue(header, out var col))
        {
            return false;
        }

        var cell = row.Cell(col);
        if (cell.TryGetValue(out double d))
        {
            value = d;
            return true;
        }

        var text = cell.GetFormattedString()?.Trim();
        return !string.IsNullOrWhiteSpace(text) &&
               double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static DateTime? TryGetDate(IXLRow row, Dictionary<string, int> map, string header)
    {
        if (!map.TryGetValue(header, out var col))
        {
            return null;
        }

        var cell = row.Cell(col);
        if (cell.TryGetValue(out DateTime dt))
        {
            return dt;
        }

        var text = cell.GetFormattedString()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed) ||
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
        {
            return parsed;
        }

        return null;
    }
}
