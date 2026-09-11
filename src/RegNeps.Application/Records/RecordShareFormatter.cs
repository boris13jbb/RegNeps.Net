using System.Globalization;
using System.Text;
using RegNeps.Domain.Entities;

namespace RegNeps.Application.Records;

/// <summary>
/// Formato de texto para compartir registros.
/// Solo formatea la colección recibida; no decide qué IDs incluir.
/// </summary>
public static class RecordShareFormatter
{
    public static string Format(IReadOnlyList<NepRecord> records)
    {
        if (records is null || records.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < records.Count; i++)
        {
            if (i > 0)
            {
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }

            AppendRecord(sb, records[i]);
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendRecord(StringBuilder sb, NepRecord r)
    {
        var local = r.CreatedAt.ToLocalTime();
        sb.AppendLine($"Fecha: {local:dd/MM/yyyy}");
        sb.AppendLine($"Hora: {local:HH:mm}");
        sb.AppendLine($"Telar: {r.Telar}");
        sb.AppendLine($"Neps: {r.Neps.ToString("0.##", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Mts: {r.MtsCalculados.ToString("0", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Tela: {r.Tela}");
        sb.AppendLine($"Lote: {r.LoteTrama}");
        if (!string.IsNullOrWhiteSpace(r.Turno))
        {
            sb.AppendLine($"Turno: {r.Turno}");
        }

        if (!string.IsNullOrWhiteSpace(r.Operario))
        {
            sb.AppendLine($"Operario: {r.Operario}");
        }

        if (!string.IsNullOrWhiteSpace(r.Observacion))
        {
            sb.AppendLine($"Observación: {r.Observacion}");
        }
    }
}
