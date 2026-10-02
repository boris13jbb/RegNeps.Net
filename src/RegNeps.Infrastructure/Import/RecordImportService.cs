using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using RegNeps.Application.Abstractions;
using RegNeps.Application.Records;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;

namespace RegNeps.Infrastructure.Import;

public sealed class RecordImportService : IRecordImportService
{
    private readonly INepRecordRepository _records;

    public RecordImportService(INepRecordRepository records) => _records = records;

    public Task<RecordImportResult> ImportFileAsync(
        Stream stream,
        string fileName,
        string? createdByUserId,
        string? createdByEmail,
        string? createdByRole,
        CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return ext == ".csv"
            ? ImportCsvAsync(stream, createdByUserId, createdByEmail, createdByRole, ct)
            : ImportExcelAsync(stream, createdByUserId, createdByEmail, createdByRole, ct);
    }

    public async Task<RecordImportResult> ImportExcelAsync(
        Stream stream,
        string? createdByUserId,
        string? createdByEmail,
        string? createdByRole,
        CancellationToken ct = default)
    {
        var rows = new List<RecordImportRowResult>();
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

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (header, col) in map)
            {
                fields[header] = row.Cell(col).GetFormattedString()?.Trim() ?? string.Empty;
            }

            var result = await TryImportRowAsync(
                rowNum, fields, createdByUserId, createdByEmail, createdByRole, ct);
            rows.Add(result);
            if (result.Success)
            {
                imported++;
            }
        }

        return new RecordImportResult { Imported = imported, Rows = rows };
    }

    public async Task<RecordImportResult> ImportCsvAsync(
        Stream stream,
        string? createdByUserId,
        string? createdByEmail,
        string? createdByRole,
        CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var content = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("El archivo CSV está vacío.");
        }

        // Quitar BOM residual si el lector no lo eliminó.
        if (content.Length > 0 && content[0] == '\uFEFF')
        {
            content = content[1..];
        }

        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            throw new InvalidOperationException("El archivo CSV no contiene filas.");
        }

        var delimiter = DetectDelimiter(lines[0]);
        var headers = SplitCsvLine(lines[0], delimiter)
            .Select(NormalizeHeader)
            .ToList();
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(headers[i]) && !map.ContainsKey(headers[i]))
            {
                map[headers[i]] = i;
            }
        }

        RequireHeaders(map, "TELAR", "NEPS");

        var rows = new List<RecordImportRowResult>();
        var imported = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rowNum = i + 1;
            var cells = SplitCsvLine(lines[i], delimiter);
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (header, idx) in map)
            {
                fields[header] = idx < cells.Count ? cells[idx].Trim() : string.Empty;
            }

            var result = await TryImportRowAsync(
                rowNum, fields, createdByUserId, createdByEmail, createdByRole, ct);
            rows.Add(result);
            if (result.Success)
            {
                imported++;
            }
        }

        return new RecordImportResult { Imported = imported, Rows = rows };
    }

    private async Task<RecordImportRowResult> TryImportRowAsync(
        int rowNum,
        Dictionary<string, string> fields,
        string? createdByUserId,
        string? createdByEmail,
        string? createdByRole,
        CancellationToken ct)
    {
        try
        {
            fields.TryGetValue("TELAR", out var telar);
            if (string.IsNullOrWhiteSpace(telar))
            {
                return Fail(rowNum, "TELAR es obligatorio.");
            }

            fields.TryGetValue("NEPS", out var nepsText);
            if (!TryParseDouble(nepsText, out var neps))
            {
                return Fail(rowNum, "NEPS inválido.");
            }

            if (neps < 0)
            {
                return Fail(rowNum, "NEPS no puede ser negativo.");
            }

            fields.TryGetValue("LOTE", out var lote);
            if (string.IsNullOrWhiteSpace(lote))
            {
                lote = NepsConstants.LoteTramaPrefix;
            }

            fields.TryGetValue("FECHA", out var fechaText);
            var createdAt = TryParseDate(fechaText) ?? DateTime.UtcNow;

            fields.TryGetValue("TELA", out var tela);
            fields.TryGetValue("TURNO", out var turno);
            fields.TryGetValue("OPERARIO", out var operario);
            fields.TryGetValue("LINEA", out var linea);
            fields.TryGetValue("OBSERVACION", out var observacion);

            var record = new NepRecord
            {
                Telar = telar.Trim(),
                Neps = neps,
                Tela = (tela ?? string.Empty).Trim(),
                LoteTrama = lote.Trim().ToUpperInvariant(),
                Turno = (turno ?? string.Empty).Trim(),
                Operario = (operario ?? string.Empty).Trim(),
                LineaProduccion = (linea ?? string.Empty).Trim(),
                Observacion = (observacion ?? string.Empty).Trim(),
                CreatedAt = createdAt.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(createdAt, DateTimeKind.Local).ToUniversalTime()
                    : createdAt.ToUniversalTime(),
                CreatedByUserId = createdByUserId,
                CreatedByEmail = createdByEmail,
                CreatedByRole = createdByRole
            };

            await _records.AddAsync(record, ct);
            return new RecordImportRowResult { RowNumber = rowNum, Success = true };
        }
        catch (Exception ex)
        {
            return Fail(rowNum, ex.Message);
        }
    }

    private static RecordImportRowResult Fail(int rowNum, string reason) =>
        new() { RowNumber = rowNum, Success = false, Reason = reason };

    private static char DetectDelimiter(string headerLine)
    {
        var commas = headerLine.Count(c => c == ',');
        var semis = headerLine.Count(c => c == ';');
        return semis > commas ? ';' : ',';
    }

    private static List<string> SplitCsvLine(string line, char delimiter)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (ch == delimiter && !inQuotes)
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(ch);
        }

        result.Add(sb.ToString());
        return result;
    }

    private static bool TryParseDouble(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return double.TryParse(text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out value)
               || double.TryParse(text.Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out value);
    }

    private static DateTime? TryParseDate(string? text)
    {
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
                throw new InvalidOperationException($"Falta la columna obligatoria '{key}' en el archivo.");
            }
        }
    }
}
