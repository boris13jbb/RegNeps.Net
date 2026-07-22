using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using RegNeps.Application.Abstractions;
using RegNeps.Application.Analytics;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Services;

namespace RegNeps.Infrastructure.Export;

/// <summary>
/// Generación CSV/Excel/PDF en paridad con ReportExportService Flutter (estilo Completo).
/// </summary>
public sealed class ExportFileService : IExportFileService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static ExportFileService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] BuildCsv(IReadOnlyList<NepRecord> records, AlertConfig config, string style = "completo")
    {
        if (IsClassic(style))
            return BuildClassicCsv(records, config);

        var sorted = SortForReport(records);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',',
            "Nro", "Fecha", "Lote de trama", "Tela", "Telar", "Neps", "Mts calculados",
            "Estado alerta", "Observación", "Recomendación"));

        for (var i = 0; i < sorted.Count; i++)
        {
            var r = sorted[i];
            var level = AlertEvaluator.GetLevel(r.Neps, config);
            var rec = string.Join(' ', AlertEvaluator.GetRecommendations(level));
            sb.Append(i + 1).Append(',');
            sb.Append(Escape(FormatDate(r.CreatedAt))).Append(',');
            sb.Append(Escape(r.LoteTrama)).Append(',');
            sb.Append(Escape(r.Tela)).Append(',');
            sb.Append(Escape(r.Telar)).Append(',');
            sb.Append(Escape(FormatDecimal(r.Neps))).Append(',');
            sb.Append(Escape(FormatMts(r.MtsCalculados))).Append(',');
            sb.Append(Escape(level.ToDisplayLabel())).Append(',');
            sb.Append(Escape(r.Observacion)).Append(',');
            sb.Append(Escape(rec));
            sb.AppendLine();
        }

        var summary = Summarize(sorted);
        sb.AppendLine($"TOTAL REGISTROS,{sorted.Count}");
        sb.AppendLine($"TOTAL NEPS,{FormatDecimal(summary.TotalNeps)}");
        sb.AppendLine($"PROMEDIO NEPS,{FormatMts(summary.AverageNeps)}");

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public byte[] BuildExcel(
        IReadOnlyList<NepRecord> records,
        AlertConfig config,
        string title = "Informe Neps VICUNHA",
        string style = "completo")
    {
        if (IsClassic(style))
            return BuildClassicExcel(records, config, title);

        var sorted = SortForReport(records);
        using var workbook = new XLWorkbook();

        WriteRegistrosSheet(workbook, sorted, config, title);
        WriteGroupSheet(workbook, "Resumen por telar", GroupBy(sorted, r => r.Telar, "(sin telar)", config));
        WriteGroupSheet(workbook, "Resumen por tela", GroupBy(sorted, r => r.Tela, "(sin tela)", config));
        WriteGroupSheet(workbook, "Resumen por lote", GroupBy(sorted, r => r.LoteTrama, "(sin lote)", config));
        WriteAlertSheet(workbook, "Alertas críticas", sorted, config, AlertLevel.Critico);
        WriteAlertSheet(workbook, "Advertencias", sorted, config, AlertLevel.Advertencia);
        WriteTrendSheet(workbook, sorted);

        workbook.Worksheet("Registros").Position = 1;

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public byte[] BuildPdf(
        IReadOnlyList<NepRecord> records,
        AlertConfig config,
        string title = "Reporte de Control de Calidad — Neps VICUNHA",
        string? filtersDescription = null,
        string style = "completo")
    {
        if (IsClassic(style))
            return BuildClassicPdf(records, config, title, filtersDescription);

        var sorted = SortForReport(records);
        var summary = Summarize(sorted);
        var critical = sorted.Where(r => AlertEvaluator.GetLevel(r.Neps, config) == AlertLevel.Critico).ToList();
        var byTelar = GroupBy(sorted, r => r.Telar, "(sin telar)", config);
        var topTelars = byTelar.OrderByDescending(g => g.TotalNeps).Take(10).ToList();
        var bestTelars = byTelar
            .Where(g => g.RecordCount > 0)
            .OrderBy(g => g.AverageNeps)
            .Take(10)
            .ToList();
        var porTela = GroupBy(sorted, r => r.Tela, "(sin tela)", config).Take(10).ToList();
        var porLote = GroupBy(sorted, r => r.LoteTrama, "(sin lote)", config).Take(10).ToList();
        var worstTela = porTela.OrderByDescending(g => g.AverageNeps).FirstOrDefault();
        var worstLote = porLote.OrderByDescending(g => g.AverageNeps).FirstOrDefault();
        var criticalTelars = byTelar.Count(g => g.CriticalCount > 0);
        var bestTelar = bestTelars.FirstOrDefault();

        var recommendations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in critical.Take(15))
        {
            foreach (var tip in AlertEvaluator.GetRecommendations(AlertLevel.Critico))
                recommendations.Add(tip);
        }

        var generatedAt = DateTime.Now;
        var navy = Color.FromHex("#1F2A2E");
        var cream = Color.FromHex("#F7EAC5");
        var muted = Color.FromHex("#CFD8C5");

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Column(col =>
                {
                    col.Item().Background(navy).Padding(12).Column(h =>
                    {
                        h.Item().Text("VICUNHA — Sistema de Control de Calidad Textil")
                            .FontColor(cream).SemiBold().FontSize(16);
                        h.Item().Text(title).FontColor(muted).FontSize(10);
                        h.Item().Text($"Generado: {FormatDate(generatedAt)}").FontColor(muted).FontSize(9);
                    });
                });

                page.Content().PaddingTop(12).Column(col =>
                {
                    col.Spacing(10);

                    col.Item().Text($"Fórmula utilizada: Mts calculados = Neps / {NepsConstants.TestLengthM.ToString(Inv)}")
                        .SemiBold().FontSize(11);

                    if (!string.IsNullOrWhiteSpace(filtersDescription))
                    {
                        col.Item().Text($"Filtros aplicados: {filtersDescription}").FontSize(9);
                    }

                    col.Item().Text("Resumen ejecutivo").SemiBold().FontSize(12);
                    col.Item().Text(
                        $"Registros: {sorted.Count} | Total neps: {FormatDecimal(summary.TotalNeps)} | " +
                        $"Promedio: {FormatMts(summary.AverageNeps)} | Críticos: {critical.Count} | " +
                        $"Telares con críticos: {criticalTelars}");
                    if (worstTela is not null)
                        col.Item().Text($"Tela más problemática: {worstTela.Key} (prom. {FormatMts(worstTela.AverageNeps)})");
                    if (worstLote is not null)
                        col.Item().Text($"Lote más problemático: {worstLote.Key} (prom. {FormatMts(worstLote.AverageNeps)})");
                    if (bestTelar is not null)
                        col.Item().Text($"Mejor telar (menor promedio): {bestTelar.Key} (prom. {FormatMts(bestTelar.AverageNeps)})");

                    col.Item().Text("Tabla principal de registros").SemiBold().FontSize(12);
                    col.Item().Element(c => WritePdfRecordsTable(c, sorted, config));

                    if (critical.Count > 0)
                    {
                        col.Item().Text("Alertas críticas").SemiBold().FontSize(12);
                        col.Item().Element(c => WritePdfAlertTable(c, critical, config));
                    }

                    if (topTelars.Count > 0)
                    {
                        col.Item().Text("Top 10 telares con más neps").SemiBold().FontSize(12);
                        col.Item().Element(c => WritePdfGroupTable(
                            c,
                            ["Telar", "Total neps", "Promedio por m²", "Registros"],
                            topTelars.Select(g => new[]
                            {
                                g.Key,
                                FormatDecimal(g.TotalNeps),
                                FormatMts(g.AverageNeps / NepsConstants.TestLengthM),
                                g.RecordCount.ToString(Inv)
                            }).ToList()));
                    }

                    if (bestTelars.Count > 0)
                    {
                        col.Item().Text("Mejores telares (menor neps/m²)").SemiBold().FontSize(12);
                        col.Item().Element(c => WritePdfGroupTable(
                            c,
                            ["Telar", "Total neps", "Promedio por m²", "Registros"],
                            bestTelars.Select(g => new[]
                            {
                                g.Key,
                                FormatDecimal(g.TotalNeps),
                                FormatMts(g.AverageNeps / NepsConstants.TestLengthM),
                                g.RecordCount.ToString(Inv)
                            }).ToList()));
                    }

                    if (porTela.Count > 0)
                    {
                        col.Item().Text("Resumen por tela").SemiBold().FontSize(12);
                        col.Item().Element(c => WritePdfGroupTable(
                            c,
                            ["Tela", "Total neps", "Promedio", "Registros"],
                            porTela.Select(g => new[]
                            {
                                g.Key,
                                FormatDecimal(g.TotalNeps),
                                FormatMts(g.AverageNeps),
                                g.RecordCount.ToString(Inv)
                            }).ToList()));
                    }

                    if (porLote.Count > 0)
                    {
                        col.Item().Text("Resumen por lote/trama").SemiBold().FontSize(12);
                        col.Item().Element(c => WritePdfGroupTable(
                            c,
                            ["Lote/trama", "Total neps", "Promedio", "Registros"],
                            porLote.Select(g => new[]
                            {
                                g.Key,
                                FormatDecimal(g.TotalNeps),
                                FormatMts(g.AverageNeps),
                                g.RecordCount.ToString(Inv)
                            }).ToList()));
                    }

                    if (recommendations.Count > 0)
                    {
                        col.Item().Text("Recomendaciones automáticas").SemiBold().FontSize(12);
                        foreach (var tip in recommendations)
                            col.Item().Text($"• {tip}").FontSize(9);
                    }

                    col.Item().PaddingTop(16).BorderTop(1).BorderColor(Colors.Grey.Medium).PaddingTop(8)
                        .Text("Firma supervisor / responsable de calidad: _______________________________")
                        .FontSize(9);
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Página ");
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        });

        return document.GeneratePdf();
    }

    public byte[] BuildFabricsCsv(IReadOnlyList<Fabric> fabrics)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Nombre,Codigo,Activo,Creado");
        foreach (var f in fabrics)
        {
            sb.Append(Escape(f.Name)).Append(',');
            sb.Append(Escape(f.Code ?? string.Empty)).Append(',');
            sb.Append(f.IsActive ? "Si" : "No").Append(',');
            sb.Append(Escape(FormatDate(f.CreatedAt)));
            sb.AppendLine();
        }

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public byte[] BuildFabricsExcel(IReadOnlyList<Fabric> fabrics)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Telas");
        sheet.Cell(1, 1).Value = "Nombre";
        sheet.Cell(1, 2).Value = "Codigo";
        sheet.Cell(1, 3).Value = "Activo";
        sheet.Cell(1, 4).Value = "Creado";
        sheet.Row(1).Style.Font.SetBold();

        var row = 2;
        foreach (var f in fabrics)
        {
            sheet.Cell(row, 1).Value = f.Name;
            sheet.Cell(row, 2).Value = f.Code ?? string.Empty;
            sheet.Cell(row, 3).Value = f.IsActive ? "Si" : "No";
            sheet.Cell(row, 4).Value = f.CreatedAt.ToLocalTime();
            row++;
        }

        sheet.Columns().AdjustToContents();
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public byte[] BuildLotesCsv(IReadOnlyList<LoteTramaItem> lotes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Codigo,Activo,Creado");
        foreach (var lote in lotes)
        {
            sb.Append(Escape(lote.Code)).Append(',');
            sb.Append(lote.IsActive ? "Si" : "No").Append(',');
            sb.Append(Escape(FormatDate(lote.CreatedAt)));
            sb.AppendLine();
        }

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public byte[] BuildLotesExcel(IReadOnlyList<LoteTramaItem> lotes)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Lotes");
        sheet.Cell(1, 1).Value = "Codigo";
        sheet.Cell(1, 2).Value = "Activo";
        sheet.Cell(1, 3).Value = "Creado";
        sheet.Row(1).Style.Font.SetBold();

        var row = 2;
        foreach (var lote in lotes)
        {
            sheet.Cell(row, 1).Value = lote.Code;
            sheet.Cell(row, 2).Value = lote.IsActive ? "Si" : "No";
            sheet.Cell(row, 3).Value = lote.CreatedAt.ToLocalTime();
            row++;
        }

        sheet.Columns().AdjustToContents();
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public byte[] BuildAnalyticsCsv(AnalyticsSummary summary, string? periodDescription = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(periodDescription))
        {
            sb.AppendLine($"Periodo,{Escape(periodDescription)}");
        }

        sb.AppendLine("Metrica,Valor");
        sb.AppendLine($"Registros,{summary.TotalRecords}");
        sb.AppendLine($"Promedio neps,{FormatDecimal(summary.AverageNeps)}");
        sb.AppendLine($"Total mts,{FormatMts(summary.TotalMts)}");
        sb.AppendLine($"Criticos,{summary.CriticalCount}");
        sb.AppendLine($"Advertencias,{summary.WarningCount}");
        sb.AppendLine($"Indice calidad,{FormatDecimal(summary.QualityIndex)}");
        sb.AppendLine();

        sb.AppendLine("Telar,Promedio,Registros,Criticos,Advertencias,Mts");
        foreach (var g in summary.ByTelar)
        {
            sb.Append(Escape(g.Key)).Append(',');
            sb.Append(FormatDecimal(g.AverageNeps)).Append(',');
            sb.Append(g.RecordCount).Append(',');
            sb.Append(g.CriticalCount).Append(',');
            sb.Append(g.WarningCount).Append(',');
            sb.Append(FormatMts(g.TotalMts));
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Tela,Promedio,Registros,Criticos,Advertencias,Mts");
        foreach (var g in summary.ByTela)
        {
            sb.Append(Escape(g.Key)).Append(',');
            sb.Append(FormatDecimal(g.AverageNeps)).Append(',');
            sb.Append(g.RecordCount).Append(',');
            sb.Append(g.CriticalCount).Append(',');
            sb.Append(g.WarningCount).Append(',');
            sb.Append(FormatMts(g.TotalMts));
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Fecha,Promedio,Cantidad");
        foreach (var d in summary.ByDay)
        {
            sb.Append(Escape(d.Date.ToString("dd/MM/yyyy", Inv))).Append(',');
            sb.Append(FormatDecimal(d.AverageNeps)).Append(',');
            sb.Append(d.Count);
            sb.AppendLine();
        }

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public byte[] BuildAnalyticsExcel(AnalyticsSummary summary, string? periodDescription = null)
    {
        using var workbook = new XLWorkbook();

        var kpis = workbook.Worksheets.Add("KPIs");
        kpis.Cell(1, 1).Value = "Métrica";
        kpis.Cell(1, 2).Value = "Valor";
        kpis.Row(1).Style.Font.SetBold();
        if (!string.IsNullOrWhiteSpace(periodDescription))
        {
            kpis.Cell(2, 1).Value = "Periodo";
            kpis.Cell(2, 2).Value = periodDescription;
        }

        var start = string.IsNullOrWhiteSpace(periodDescription) ? 2 : 3;
        kpis.Cell(start, 1).Value = "Registros";
        kpis.Cell(start, 2).Value = summary.TotalRecords;
        kpis.Cell(start + 1, 1).Value = "Promedio neps";
        kpis.Cell(start + 1, 2).Value = summary.AverageNeps;
        kpis.Cell(start + 2, 1).Value = "Total mts";
        kpis.Cell(start + 2, 2).Value = summary.TotalMts;
        kpis.Cell(start + 3, 1).Value = "Críticos";
        kpis.Cell(start + 3, 2).Value = summary.CriticalCount;
        kpis.Cell(start + 4, 1).Value = "Advertencias";
        kpis.Cell(start + 4, 2).Value = summary.WarningCount;
        kpis.Cell(start + 5, 1).Value = "Índice calidad %";
        kpis.Cell(start + 5, 2).Value = summary.QualityIndex;
        kpis.Columns().AdjustToContents();

        WriteAnalyticsGroupSheet(workbook, "Por telar", summary.ByTelar);
        WriteAnalyticsGroupSheet(workbook, "Por tela", summary.ByTela);

        var daySheet = workbook.Worksheets.Add("Por día");
        daySheet.Cell(1, 1).Value = "Fecha";
        daySheet.Cell(1, 2).Value = "Promedio";
        daySheet.Cell(1, 3).Value = "Cantidad";
        daySheet.Row(1).Style.Font.SetBold();
        var r = 2;
        foreach (var d in summary.ByDay)
        {
            daySheet.Cell(r, 1).Value = d.Date;
            daySheet.Cell(r, 2).Value = d.AverageNeps;
            daySheet.Cell(r, 3).Value = d.Count;
            r++;
        }

        daySheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public byte[] BuildAnalyticsPdf(
        AnalyticsSummary summary,
        string? periodDescription = null,
        IReadOnlyList<AnalyticsChartImage>? chartImages = null)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontSize(10));
                page.Header().Column(col =>
                {
                    col.Item().Text("Gráficas / Analytics — RegNeps VICUNHA").SemiBold().FontSize(14);
                    if (!string.IsNullOrWhiteSpace(periodDescription))
                    {
                        col.Item().Text(periodDescription).FontSize(9).FontColor(Colors.Grey.Darken2);
                    }

                    col.Item().Text($"Generado: {FormatDate(DateTime.UtcNow)}").FontSize(8).FontColor(Colors.Grey.Darken1);
                });

                page.Content().PaddingTop(12).Column(col =>
                {
                    col.Item().Text(
                        $"Registros: {summary.TotalRecords} | Promedio: {FormatDecimal(summary.AverageNeps)} | " +
                        $"Críticos: {summary.CriticalCount} | Calidad: {FormatDecimal(summary.QualityIndex)}%");

                    if (chartImages is { Count: > 0 })
                    {
                        col.Item().PaddingTop(12).Text("Visualizaciones").SemiBold().FontSize(12);
                        foreach (var img in chartImages)
                        {
                            if (img.PngBytes.Length == 0)
                            {
                                continue;
                            }

                            col.Item().PaddingTop(8).Text(img.Title).SemiBold().FontSize(9);
                            col.Item().MaxHeight(240).Image(img.PngBytes).FitArea();
                        }
                    }

                    col.Item().PaddingTop(10).Text("Top telares (peor promedio)").SemiBold();
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn(3);
                            c.RelativeColumn(1);
                            c.RelativeColumn(1);
                            c.RelativeColumn(1);
                        });
                        t.Header(h =>
                        {
                            h.Cell().Element(HeaderCell).Text("Telar");
                            h.Cell().Element(HeaderCell).Text("Prom.");
                            h.Cell().Element(HeaderCell).Text("Regs");
                            h.Cell().Element(HeaderCell).Text("Crít.");
                        });
                        foreach (var g in summary.ByTelar.Take(15))
                        {
                            t.Cell().Element(BodyCell).Text(g.Key);
                            t.Cell().Element(BodyCell).Text(FormatDecimal(g.AverageNeps));
                            t.Cell().Element(BodyCell).Text(g.RecordCount.ToString(Inv));
                            t.Cell().Element(BodyCell).Text(g.CriticalCount.ToString(Inv));
                        }
                    });

                    col.Item().PaddingTop(10).Text("Top telas").SemiBold();
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn(3);
                            c.RelativeColumn(1);
                            c.RelativeColumn(1);
                            c.RelativeColumn(1);
                        });
                        t.Header(h =>
                        {
                            h.Cell().Element(HeaderCell).Text("Tela");
                            h.Cell().Element(HeaderCell).Text("Prom.");
                            h.Cell().Element(HeaderCell).Text("Regs");
                            h.Cell().Element(HeaderCell).Text("Crít.");
                        });
                        foreach (var g in summary.ByTela.Take(15))
                        {
                            t.Cell().Element(BodyCell).Text(g.Key);
                            t.Cell().Element(BodyCell).Text(FormatDecimal(g.AverageNeps));
                            t.Cell().Element(BodyCell).Text(g.RecordCount.ToString(Inv));
                            t.Cell().Element(BodyCell).Text(g.CriticalCount.ToString(Inv));
                        }
                    });
                });
            });
        });

        return document.GeneratePdf();
    }

    private static void WriteAnalyticsGroupSheet(
        XLWorkbook workbook,
        string name,
        IReadOnlyList<RegNeps.Application.Analytics.GroupSummary> groups)
    {
        var sheet = workbook.Worksheets.Add(name);
        sheet.Cell(1, 1).Value = "Clave";
        sheet.Cell(1, 2).Value = "Promedio";
        sheet.Cell(1, 3).Value = "Registros";
        sheet.Cell(1, 4).Value = "Críticos";
        sheet.Cell(1, 5).Value = "Advertencias";
        sheet.Cell(1, 6).Value = "Mts";
        sheet.Row(1).Style.Font.SetBold();
        var row = 2;
        foreach (var g in groups)
        {
            sheet.Cell(row, 1).Value = g.Key;
            sheet.Cell(row, 2).Value = g.AverageNeps;
            sheet.Cell(row, 3).Value = g.RecordCount;
            sheet.Cell(row, 4).Value = g.CriticalCount;
            sheet.Cell(row, 5).Value = g.WarningCount;
            sheet.Cell(row, 6).Value = g.TotalMts;
            row++;
        }

        sheet.Columns().AdjustToContents();
    }

    public byte[] BuildImportTemplate()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Importacion");
        var headers = new[]
        {
            "NRO", "FECHA", "LOTE DE TRAMA", "NOMBRE DE TELA", "TELAR", "NEPS", "MTS CALCULADOS",
            "TURNO", "OPERARIO", "LINEA PRODUCCION", "OBSERVACION"
        };

        for (var i = 0; i < headers.Length; i++)
            sheet.Cell(1, i + 1).Value = headers[i];

        sheet.Row(1).Style.Font.SetBold();
        sheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static bool IsClassic(string? style) =>
        string.Equals(style?.Trim(), "clasico", StringComparison.OrdinalIgnoreCase)
        || string.Equals(style?.Trim(), "classic", StringComparison.OrdinalIgnoreCase);

    private byte[] BuildClassicCsv(IReadOnlyList<NepRecord> records, AlertConfig config)
    {
        var sorted = SortForReport(records);
        var summary = Summarize(sorted);
        var sb = new StringBuilder();
        sb.AppendLine($"Formula utilizada: Mts calculados = Neps / {NepsConstants.TestLengthM.ToString(Inv)}");
        sb.AppendLine(string.Join(',',
            "Nro", "Fecha", "Lote de trama", "Tela", "Telar", "Neps", "Mts calculados",
            "Estado alerta", "Observación", "Recomendación"));

        for (var i = 0; i < sorted.Count; i++)
        {
            var r = sorted[i];
            var level = AlertEvaluator.GetLevel(r.Neps, config);
            sb.Append(i + 1).Append(',');
            sb.Append(Escape(FormatDate(r.CreatedAt))).Append(',');
            sb.Append(Escape(r.LoteTrama)).Append(',');
            sb.Append(Escape(r.Tela)).Append(',');
            sb.Append(Escape(r.Telar)).Append(',');
            sb.Append(Escape(FormatDecimal(r.Neps))).Append(',');
            sb.Append(Escape(FormatMts(r.MtsCalculados))).Append(',');
            sb.Append(Escape(level.ToDisplayLabel())).Append(',');
            sb.Append(Escape(r.Observacion)).Append(',');
            sb.Append(Escape(string.Join(' ', AlertEvaluator.GetRecommendations(level))));
            sb.AppendLine();
        }

        // Filas de totales embebidas (paridad Flutter clásico).
        sb.Append(sorted.Count).Append(',');
        sb.Append(Escape("TOTAL REGISTROS")).Append(',');
        sb.AppendLine(",,,,,,,,");
        sb.Append(Escape("TOTAL NEPS")).Append(",,,,,");
        sb.Append(Escape(FormatDecimal(summary.TotalNeps))).AppendLine(",,,,");
        sb.Append(Escape("PROMEDIO NEPS")).Append(",,,,,");
        sb.Append(Escape(FormatMts(summary.AverageNeps))).AppendLine(",,,,");

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private byte[] BuildClassicExcel(IReadOnlyList<NepRecord> records, AlertConfig config, string title)
    {
        var sorted = SortForReport(records);
        var summary = Summarize(sorted);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Informe");

        sheet.Cell(1, 1).Value = "VICUNHA jeansidentity";
        sheet.Cell(1, 1).Style.Font.SetBold().Font.FontSize = 14;
        sheet.Cell(2, 1).Value = $"Formula utilizada: Mts calculados = Neps / {NepsConstants.TestLengthM.ToString(Inv)}";
        sheet.Cell(3, 1).Value = title;

        var headers = new[]
        {
            "Nro", "Fecha", "Lote de trama", "Tela", "Telar", "Neps", "Mts calculados",
            "Estado alerta", "Observación", "Recomendación"
        };
        for (var i = 0; i < headers.Length; i++)
            sheet.Cell(5, i + 1).Value = headers[i];
        sheet.Row(5).Style.Font.SetBold().Fill.BackgroundColor = XLColor.FromHtml("#3C4043");
        sheet.Row(5).Style.Font.FontColor = XLColor.White;

        for (var i = 0; i < sorted.Count; i++)
        {
            var r = sorted[i];
            var level = AlertEvaluator.GetLevel(r.Neps, config);
            var row = i + 6;
            sheet.Cell(row, 1).Value = i + 1;
            sheet.Cell(row, 2).Value = FormatDate(r.CreatedAt);
            sheet.Cell(row, 3).Value = r.LoteTrama;
            sheet.Cell(row, 4).Value = r.Tela;
            sheet.Cell(row, 5).Value = r.Telar;
            sheet.Cell(row, 6).Value = r.Neps;
            sheet.Cell(row, 7).Value = Math.Round(r.MtsCalculados, MidpointRounding.AwayFromZero);
            sheet.Cell(row, 8).Value = level.ToDisplayLabel();
            sheet.Cell(row, 9).Value = r.Observacion;
            sheet.Cell(row, 10).Value = string.Join(' ', AlertEvaluator.GetRecommendations(level));
        }

        var summaryRow = sorted.Count + 7;
        sheet.Cell(summaryRow, 1).Value = $"Total registros: {sorted.Count}";
        sheet.Cell(summaryRow + 1, 1).Value = $"Total neps: {FormatDecimal(summary.TotalNeps)}";
        sheet.Cell(summaryRow + 2, 1).Value = $"Promedio neps: {FormatMts(summary.AverageNeps)}";
        sheet.Range(summaryRow, 1, summaryRow + 2, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#EBDFC3");

        sheet.Columns().AdjustToContents();
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private byte[] BuildClassicPdf(
        IReadOnlyList<NepRecord> records,
        AlertConfig config,
        string title,
        string? filtersDescription)
    {
        var sorted = SortForReport(records);
        var summary = Summarize(sorted);
        var navy = Color.FromHex("#1F2A2E");
        var cream = Color.FromHex("#F7EAC5");
        var muted = Color.FromHex("#CFD8C5");
        var box = Color.FromHex("#EBDFC3");

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Background(navy).Padding(16).Column(h =>
                {
                    h.Item().Row(r =>
                    {
                        r.AutoItem().Text("VICUNHA  ").FontColor(Colors.White).SemiBold().FontSize(20);
                        r.AutoItem().Text("jeansidentity").FontColor(cream).SemiBold().FontSize(20);
                    });
                    h.Item().Text(title).FontColor(muted).FontSize(10);
                });

                page.Content().PaddingTop(14).Column(col =>
                {
                    col.Spacing(8);
                    col.Item().Text($"Formula utilizada: Mts calculados = Neps / {NepsConstants.TestLengthM.ToString(Inv)}")
                        .SemiBold().FontSize(11);
                    if (!string.IsNullOrWhiteSpace(filtersDescription))
                        col.Item().Text($"Filtros aplicados: {filtersDescription}").FontSize(9);

                    col.Item().Element(c => WritePdfRecordsTable(c, sorted, config));

                    col.Item().PaddingTop(10).Background(box).Padding(12).Column(boxCol =>
                    {
                        boxCol.Item().Text($"Total registros: {sorted.Count}").FontSize(10);
                        boxCol.Item().Text($"Total neps: {FormatDecimal(summary.TotalNeps)}").FontSize(10);
                        boxCol.Item().Text($"Promedio neps: {FormatMts(summary.AverageNeps)}").FontSize(10);
                    });
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Página ");
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        });

        return document.GeneratePdf();
    }

    private static void WriteRegistrosSheet(
        XLWorkbook workbook,
        IReadOnlyList<NepRecord> records,
        AlertConfig config,
        string title)
    {
        var sheet = workbook.Worksheets.Add("Registros");
        sheet.Cell(1, 1).Value = title;
        sheet.Range(1, 1, 1, 10).Merge().Style.Font.SetBold().Font.FontSize = 14;
        sheet.Cell(2, 1).Value = $"Fórmula: Mts calculados = Neps / {NepsConstants.TestLengthM.ToString(Inv)}";

        var headers = new[]
        {
            "Nro", "Fecha", "Lote de trama", "Tela", "Telar", "Neps", "Mts calculados",
            "Estado alerta", "Observación", "Recomendación"
        };
        for (var i = 0; i < headers.Length; i++)
            sheet.Cell(4, i + 1).Value = headers[i];
        sheet.Row(4).Style.Font.SetBold().Fill.BackgroundColor = XLColor.FromHtml("#1F2A2E");
        sheet.Row(4).Style.Font.FontColor = XLColor.FromHtml("#F7EAC5");

        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var level = AlertEvaluator.GetLevel(r.Neps, config);
            var row = i + 5;
            sheet.Cell(row, 1).Value = i + 1;
            sheet.Cell(row, 2).Value = FormatDate(r.CreatedAt);
            sheet.Cell(row, 3).Value = r.LoteTrama;
            sheet.Cell(row, 4).Value = r.Tela;
            sheet.Cell(row, 5).Value = r.Telar;
            sheet.Cell(row, 6).Value = r.Neps;
            sheet.Cell(row, 7).Value = Math.Round(r.MtsCalculados, MidpointRounding.AwayFromZero);
            sheet.Cell(row, 8).Value = level.ToDisplayLabel();
            sheet.Cell(row, 9).Value = r.Observacion;
            sheet.Cell(row, 10).Value = string.Join(' ', AlertEvaluator.GetRecommendations(level));
            ApplyAlertFill(sheet.Cell(row, 8), level);
            ApplyAlertFill(sheet.Cell(row, 10), level);
        }

        var summary = Summarize(records);
        var totalRow = records.Count + 5;
        sheet.Cell(totalRow, 1).Value = "TOTALES";
        sheet.Cell(totalRow, 6).Value = summary.TotalNeps;
        sheet.Cell(totalRow + 1, 1).Value = "PROMEDIO NEPS";
        sheet.Cell(totalRow + 1, 6).Value = Math.Round(summary.AverageNeps, MidpointRounding.AwayFromZero);
        sheet.Row(totalRow).Style.Font.SetBold();
        sheet.Row(totalRow + 1).Style.Font.SetBold();

        sheet.Columns().AdjustToContents();
    }

    private static void WriteGroupSheet(XLWorkbook workbook, string name, IReadOnlyList<GroupSummary> groups)
    {
        var sheet = workbook.Worksheets.Add(name);
        var headers = new[] { "Clave", "Registros", "Total neps", "Promedio neps", "Críticos", "Advertencias" };
        for (var i = 0; i < headers.Length; i++)
            sheet.Cell(1, i + 1).Value = headers[i];
        sheet.Row(1).Style.Font.SetBold().Fill.BackgroundColor = XLColor.FromHtml("#1F2A2E");
        sheet.Row(1).Style.Font.FontColor = XLColor.FromHtml("#F7EAC5");

        var row = 2;
        foreach (var g in groups)
        {
            sheet.Cell(row, 1).Value = g.Key;
            sheet.Cell(row, 2).Value = g.RecordCount;
            sheet.Cell(row, 3).Value = g.TotalNeps;
            sheet.Cell(row, 4).Value = Math.Round(g.AverageNeps, MidpointRounding.AwayFromZero);
            sheet.Cell(row, 5).Value = g.CriticalCount;
            sheet.Cell(row, 6).Value = g.WarningCount;
            row++;
        }

        sheet.Columns().AdjustToContents();
    }

    private static void WriteAlertSheet(
        XLWorkbook workbook,
        string name,
        IReadOnlyList<NepRecord> all,
        AlertConfig config,
        AlertLevel level)
    {
        var sheet = workbook.Worksheets.Add(name);
        var headers = new[]
        {
            "Fecha", "Telar", "Tela", "Lote/trama", "Neps", "Estado", "Observación", "Recomendación"
        };
        for (var i = 0; i < headers.Length; i++)
            sheet.Cell(1, i + 1).Value = headers[i];
        sheet.Row(1).Style.Font.SetBold().Fill.BackgroundColor = XLColor.FromHtml("#1F2A2E");
        sheet.Row(1).Style.Font.FontColor = XLColor.FromHtml("#F7EAC5");

        var alerts = SortForReport(all.Where(r => AlertEvaluator.GetLevel(r.Neps, config) == level).ToList());
        var row = 2;
        foreach (var r in alerts)
        {
            var alertLevel = AlertEvaluator.GetLevel(r.Neps, config);
            sheet.Cell(row, 1).Value = FormatDate(r.CreatedAt);
            sheet.Cell(row, 2).Value = r.Telar;
            sheet.Cell(row, 3).Value = r.Tela;
            sheet.Cell(row, 4).Value = r.LoteTrama;
            sheet.Cell(row, 5).Value = r.Neps;
            sheet.Cell(row, 6).Value = alertLevel.ToDisplayLabel();
            sheet.Cell(row, 7).Value = r.Observacion;
            sheet.Cell(row, 8).Value = string.Join(' ', AlertEvaluator.GetRecommendations(alertLevel));
            for (var c = 1; c <= 8; c++)
                ApplyAlertFill(sheet.Cell(row, c), alertLevel);
            row++;
        }

        sheet.Columns().AdjustToContents();
    }

    private static void WriteTrendSheet(XLWorkbook workbook, IReadOnlyList<NepRecord> records)
    {
        var sheet = workbook.Worksheets.Add("Tendencia diaria");
        sheet.Cell(1, 1).Value = "Fecha";
        sheet.Cell(1, 2).Value = "Registros";
        sheet.Cell(1, 3).Value = "Total neps";
        sheet.Cell(1, 4).Value = "Promedio neps";
        sheet.Row(1).Style.Font.SetBold().Fill.BackgroundColor = XLColor.FromHtml("#1F2A2E");
        sheet.Row(1).Style.Font.FontColor = XLColor.FromHtml("#F7EAC5");

        var trend = records
            .GroupBy(r => r.CreatedAt.ToLocalTime().Date)
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                Date = g.Key,
                Count = g.Count(),
                Total = g.Sum(x => x.Neps),
                Avg = g.Average(x => x.Neps)
            });

        var row = 2;
        foreach (var point in trend)
        {
            sheet.Cell(row, 1).Value = point.Date.ToString("yyyy-MM-dd", Inv);
            sheet.Cell(row, 2).Value = point.Count;
            sheet.Cell(row, 3).Value = point.Total;
            sheet.Cell(row, 4).Value = Math.Round(point.Avg, MidpointRounding.AwayFromZero);
            row++;
        }

        sheet.Columns().AdjustToContents();
    }

    private static void WritePdfRecordsTable(IContainer container, IReadOnlyList<NepRecord> records, AlertConfig config)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(28);
                c.RelativeColumn(1.2f);
                c.RelativeColumn(1.1f);
                c.RelativeColumn(1.1f);
                c.RelativeColumn(0.7f);
                c.RelativeColumn(0.6f);
                c.RelativeColumn(0.7f);
                c.RelativeColumn(0.9f);
            });

            table.Header(h =>
            {
                foreach (var title in new[] { "Nro", "Fecha", "Lote", "Tela", "Telar", "Neps", "Mts", "Estado" })
                    h.Cell().Element(HeaderCell).Text(title);
            });

            for (var i = 0; i < records.Count; i++)
            {
                var r = records[i];
                var level = AlertEvaluator.GetLevel(r.Neps, config);
                table.Cell().Element(BodyCell).Text((i + 1).ToString(Inv));
                table.Cell().Element(BodyCell).Text(FormatDate(r.CreatedAt));
                table.Cell().Element(BodyCell).Text(r.LoteTrama);
                table.Cell().Element(BodyCell).Text(r.Tela);
                table.Cell().Element(BodyCell).Text(r.Telar);
                table.Cell().Element(BodyCell).Text(FormatDecimal(r.Neps));
                table.Cell().Element(BodyCell).Text(FormatMts(r.MtsCalculados));
                table.Cell().Element(BodyCell).Text(level.ToDisplayLabel());
            }
        });
    }

    private static void WritePdfAlertTable(IContainer container, IReadOnlyList<NepRecord> alerts, AlertConfig config)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.RelativeColumn(1.1f);
                c.RelativeColumn(0.7f);
                c.RelativeColumn(1f);
                c.RelativeColumn(1f);
                c.RelativeColumn(0.6f);
                c.RelativeColumn(0.9f);
            });

            table.Header(h =>
            {
                foreach (var title in new[] { "Fecha", "Telar", "Tela", "Lote", "Neps", "Estado" })
                    h.Cell().Element(HeaderCell).Text(title);
            });

            foreach (var r in alerts)
            {
                var level = AlertEvaluator.GetLevel(r.Neps, config);
                table.Cell().Element(BodyCell).Text(FormatDate(r.CreatedAt));
                table.Cell().Element(BodyCell).Text(r.Telar);
                table.Cell().Element(BodyCell).Text(r.Tela);
                table.Cell().Element(BodyCell).Text(r.LoteTrama);
                table.Cell().Element(BodyCell).Text(FormatDecimal(r.Neps));
                table.Cell().Element(BodyCell).Text(level.ToDisplayLabel());
            }
        });
    }

    private static void WritePdfGroupTable(IContainer container, string[] headers, List<string[]> rows)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                foreach (var _ in headers)
                    c.RelativeColumn();
            });

            table.Header(h =>
            {
                foreach (var title in headers)
                    h.Cell().Element(HeaderCell).Text(title);
            });

            foreach (var row in rows)
            {
                foreach (var cell in row)
                    table.Cell().Element(BodyCell).Text(cell);
            }
        });
    }

    private static IReadOnlyList<GroupSummary> GroupBy(
        IReadOnlyList<NepRecord> records,
        Func<NepRecord, string> keySelector,
        string emptyKey,
        AlertConfig config)
    {
        return records
            .GroupBy(r =>
            {
                var key = keySelector(r);
                return string.IsNullOrWhiteSpace(key) ? emptyKey : key.Trim();
            })
            .Select(g =>
            {
                var list = g.ToList();
                var total = list.Sum(x => x.Neps);
                return new GroupSummary(
                    g.Key,
                    list.Count,
                    total,
                    list.Count == 0 ? 0 : total / list.Count,
                    list.Count(x => AlertEvaluator.GetLevel(x.Neps, config) == AlertLevel.Critico),
                    list.Count(x => AlertEvaluator.GetLevel(x.Neps, config) == AlertLevel.Advertencia));
            })
            .OrderByDescending(g => g.TotalNeps)
            .ToList();
    }

    private static List<NepRecord> SortForReport(IReadOnlyList<NepRecord> records) =>
        records
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Telar, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Id)
            .ToList();

    private static (double TotalNeps, double AverageNeps) Summarize(IReadOnlyList<NepRecord> records)
    {
        if (records.Count == 0)
            return (0, 0);
        var total = records.Sum(r => r.Neps);
        return (total, total / records.Count);
    }

    private static void ApplyAlertFill(IXLCell cell, AlertLevel level)
    {
        cell.Style.Fill.BackgroundColor = level switch
        {
            AlertLevel.Normal => XLColor.FromHtml("#C8E6C9"),
            AlertLevel.Advertencia => XLColor.FromHtml("#FFE0B2"),
            AlertLevel.Critico => XLColor.FromHtml("#FFCDD2"),
            _ => XLColor.NoColor
        };
    }

    private static IContainer HeaderCell(IContainer container) =>
        container.DefaultTextStyle(x => x.SemiBold().FontSize(8).FontColor(Color.FromHex("#F7EAC5")))
            .Padding(2)
            .Background(Color.FromHex("#1F2A2E"));

    private static IContainer BodyCell(IContainer container) =>
        container.DefaultTextStyle(x => x.FontSize(7))
            .Padding(2)
            .BorderBottom(0.5f)
            .BorderColor(Colors.Grey.Lighten2);

    private static string FormatDate(DateTime utcOrLocal) =>
        utcOrLocal.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Inv);

    /// <summary>Paridad Flutter formatDecimal.</summary>
    private static string FormatDecimal(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.0000001
            ? Math.Round(value).ToString(Inv)
            : value.ToString("0.###", Inv);

    /// <summary>Paridad Flutter formatNumber con decimals=0 (Mts / promedios redondeados).</summary>
    private static string FormatMts(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString(Inv);

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private sealed record GroupSummary(
        string Key,
        int RecordCount,
        double TotalNeps,
        double AverageNeps,
        int CriticalCount,
        int WarningCount);
}
