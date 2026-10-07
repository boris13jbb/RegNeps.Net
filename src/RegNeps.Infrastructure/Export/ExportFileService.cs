using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using RegNeps.Application.Abstractions;
using RegNeps.Application.Analytics;
using RegNeps.Application.Export;
using RegNeps.Application.Reports;
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

    public byte[] BuildCsv(
        IReadOnlyList<NepRecord> records,
        AlertConfig config,
        string style = "completo",
        IReadOnlyList<string>? columns = null)
    {
        if (IsClassic(style))
            return BuildClassicCsv(records, config, columns);

        var sorted = SortForReport(records);
        var cols = ReportColumnIds.Normalize(columns);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', cols.Select(id => Escape(ReportColumnIds.Labels[id]))));

        for (var i = 0; i < sorted.Count; i++)
        {
            var r = sorted[i];
            var level = AlertEvaluator.GetLevel(r.Neps, config);
            sb.AppendLine(string.Join(',', cols.Select(id => EscapeCsvColumn(id, GetColumnValue(id, i + 1, r, level)))));
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
        string style = "completo",
        IReadOnlyList<string>? columns = null)
    {
        if (IsClassic(style))
            return BuildClassicExcel(records, config, title, columns);

        var sorted = SortForReport(records);
        var cols = ReportColumnIds.Normalize(columns);
        using var workbook = new XLWorkbook();

        WriteRegistrosSheet(workbook, sorted, config, title, cols);
        WriteGroupSheet(workbook, "Resumen por telar", GroupBy(sorted, r => r.Telar, "(sin telar)", config));
        WriteGroupSheet(workbook, "Resumen por tela", GroupBy(sorted, r => r.Tela, "(sin tela)", config));
        WriteGroupSheet(workbook, "Resumen por lote", GroupBy(sorted, r => r.LoteTrama, "(sin lote)", config));
        // Excel limita el nombre de hoja a 31 caracteres.
        WriteAlertSheet(workbook, "Critico y 2da Calidad", sorted, config,
            static l => NepsQualityCriteria.IsCriticalNotificationLevel(l));
        WriteAlertSheet(workbook, "Mención", sorted, config, static l => l == AlertLevel.Mention);
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
        string style = "completo",
        IReadOnlyList<string>? columns = null)
    {
        if (IsClassic(style))
            return BuildClassicPdf(records, config, title, filtersDescription, columns);

        var sorted = SortForReport(records);
        var cols = ReportColumnIds.Normalize(columns);
        var summary = Summarize(sorted);
        var critical = sorted.Where(r =>
            NepsQualityCriteria.IsCriticalNotificationLevel(AlertEvaluator.GetLevel(r.Neps, config))).ToList();
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
            var lvl = AlertEvaluator.GetLevel(r.Neps, config);
            foreach (var tip in AlertEvaluator.GetRecommendations(lvl))
                recommendations.Add(tip);
        }

        var generatedAt = DateTime.Now;
        var navy = Color.FromHex("#1F2A2E");
        var tableHeader = Color.FromHex("#1F4E79");
        var mainHeader = Color.FromHex("#1F2A2E");
        var muted = Color.FromHex("#CFD8C5");
        var execBox = Color.FromHex("#EBDFC3");
        var zebra = Color.FromHex("#F7F5F0");

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Content().Column(col =>
                {
                    col.Spacing(10);

                    // Encabezado (solo flujo de contenido, como el reporte de referencia)
                    col.Item().Background(navy).Padding(14).Column(h =>
                    {
                        h.Item().Text("VICUNHA — Sistema de Control de Calidad Textil")
                            .FontColor(Colors.White).SemiBold().FontSize(15);
                        h.Item().PaddingTop(2).Text(title).FontColor(muted).FontSize(10);
                        h.Item().PaddingTop(4).Text($"Generado: {FormatDate(generatedAt)}")
                            .FontColor(muted).FontSize(9);
                    });

                    col.Item().Text($"Fórmula utilizada: Mts calculados = Neps / {NepsConstants.TestLengthM.ToString(Inv)}")
                        .SemiBold().FontSize(11);

                    if (!string.IsNullOrWhiteSpace(filtersDescription))
                    {
                        col.Item().Text($"Filtros aplicados: {filtersDescription}").FontSize(9);
                    }

                    // Resumen ejecutivo (caja beige)
                    col.Item().Background(execBox).Padding(12).Column(box =>
                    {
                        box.Item().Text("Resumen ejecutivo").SemiBold().FontSize(12);
                        box.Item().PaddingTop(4).Text($"Total registros: {sorted.Count}");
                        box.Item().Text($"Total neps: {FormatDecimal(summary.TotalNeps)}");
                        box.Item().Text($"Promedio neps: {FormatMts(summary.AverageNeps)}");
                        box.Item().Text($"Telares críticos: {criticalTelars}");
                        if (worstTela is not null)
                            box.Item().Text($"Tela más problemática: {worstTela.Key}");
                        if (worstLote is not null)
                            box.Item().Text($"Lote/trama más crítico: {worstLote.Key}");
                        if (bestTelar is not null)
                        {
                            var nepsM2 = bestTelar.AverageNeps / NepsConstants.TestLengthM;
                            box.Item().Text(
                                $"Mejor telar (menor neps/m²): {bestTelar.Key} — {FormatMts(nepsM2)} neps/m² ({bestTelar.RecordCount} registros)");
                        }
                    });

                    col.Item().Text("Tabla principal de registros").SemiBold().FontSize(12);
                    col.Item().Element(c => WritePdfRecordsTable(c, sorted, config, cols, mainHeader, zebra));

                    if (critical.Count > 0)
                    {
                        col.Item().Text("Alertas críticas").SemiBold().FontSize(12);
                        col.Item().Element(c => WritePdfAlertTable(c, critical, config, mainHeader, zebra));
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
                            }).ToList(),
                            tableHeader,
                            zebra));
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
                            }).ToList(),
                            tableHeader,
                            zebra));
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
                            }).ToList(),
                            tableHeader,
                            zebra));
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
                            }).ToList(),
                            tableHeader,
                            zebra));
                    }

                    if (recommendations.Count > 0)
                    {
                        col.Item().Text("Recomendaciones automáticas").SemiBold().FontSize(12);
                        foreach (var tip in recommendations)
                            col.Item().Text($"• {tip}").FontSize(9);
                    }

                    // Bloque de firma (última sección del reporte, como el diseño de referencia)
                    col.Item().PaddingTop(28).Row(r =>
                    {
                        r.RelativeItem().Column(sig =>
                        {
                            sig.Item().Width(170).BorderBottom(1).BorderColor(Colors.Black).Height(18);
                            sig.Item().PaddingTop(3).Text("Firma del supervisor").FontSize(8);
                        });
                        r.RelativeItem().AlignRight().AlignBottom()
                            .Text("VICUNHA — Control de calidad textil")
                            .FontSize(8).FontColor(Colors.Grey.Darken1);
                    });
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Página ").FontSize(8).FontColor(Colors.Grey.Darken1);
                    t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Darken1);
                    t.Span(" / ").FontSize(8).FontColor(Colors.Grey.Darken1);
                    t.TotalPages().FontSize(8).FontColor(Colors.Grey.Darken1);
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
            sb.Append(EscapeText(f.Name)).Append(',');
            sb.Append(EscapeText(f.Code)).Append(',');
            sb.Append(f.IsActive ? "Si" : "No").Append(',');
            sb.Append(EscapeText(FormatDate(f.CreatedAt)));
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
            SetExcelText(sheet.Cell(row, 1), f.Name);
            SetExcelText(sheet.Cell(row, 2), f.Code);
            SetExcelText(sheet.Cell(row, 3), f.IsActive ? "Si" : "No");
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
            sb.Append(EscapeText(lote.Code)).Append(',');
            sb.Append(lote.IsActive ? "Si" : "No").Append(',');
            sb.Append(EscapeText(FormatDate(lote.CreatedAt)));
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
            SetExcelText(sheet.Cell(row, 1), lote.Code);
            SetExcelText(sheet.Cell(row, 2), lote.IsActive ? "Si" : "No");
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
            sb.AppendLine($"Periodo,{EscapeText(periodDescription)}");
        }

        sb.AppendLine("Metrica,Valor");
        sb.AppendLine($"Registros,{summary.TotalRecords}");
        sb.AppendLine($"Promedio neps,{FormatDecimal(summary.AverageNeps)}");
        sb.AppendLine($"Total mts,{FormatMts(summary.TotalMts)}");
        sb.AppendLine($"Criticos (ajuste+2da),{summary.CriticalCount}");
        sb.AppendLine($"Menciones,{summary.WarningCount}");
        sb.AppendLine($"OK,{summary.NormalCount}");
        sb.AppendLine($"Indice calidad,{FormatDecimal(summary.QualityIndex)}");
        sb.AppendLine();

        sb.AppendLine("Telar,Promedio,Registros,Criticos,Menciones,Mts");
        foreach (var g in summary.ByTelar)
        {
            sb.Append(EscapeText(g.Key)).Append(',');
            sb.Append(FormatDecimal(g.AverageNeps)).Append(',');
            sb.Append(g.RecordCount).Append(',');
            sb.Append(g.CriticalCount).Append(',');
            sb.Append(g.WarningCount).Append(',');
            sb.Append(FormatMts(g.TotalMts));
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Tela,Promedio,Registros,Criticos,Menciones,Mts");
        foreach (var g in summary.ByTela)
        {
            sb.Append(EscapeText(g.Key)).Append(',');
            sb.Append(FormatDecimal(g.AverageNeps)).Append(',');
            sb.Append(g.RecordCount).Append(',');
            sb.Append(g.CriticalCount).Append(',');
            sb.Append(g.WarningCount).Append(',');
            sb.Append(FormatMts(g.TotalMts));
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Periodo,Promedio,Cantidad");
        foreach (var d in summary.ByDay)
        {
            sb.Append(EscapeText(d.Label ?? d.Date.ToString("dd/MM/yyyy", Inv))).Append(',');
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
            SetExcelText(kpis.Cell(2, 2), periodDescription);
        }

        var start = string.IsNullOrWhiteSpace(periodDescription) ? 2 : 3;
        kpis.Cell(start, 1).Value = "Registros";
        kpis.Cell(start, 2).Value = summary.TotalRecords;
        kpis.Cell(start + 1, 1).Value = "Promedio neps";
        kpis.Cell(start + 1, 2).Value = summary.AverageNeps;
        kpis.Cell(start + 2, 1).Value = "Total mts";
        kpis.Cell(start + 2, 2).Value = summary.TotalMts;
        kpis.Cell(start + 3, 1).Value = "Críticos (ajuste + 2da)";
        kpis.Cell(start + 3, 2).Value = summary.CriticalCount;
        kpis.Cell(start + 4, 1).Value = "Menciones";
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

    public byte[] BuildReportBuilderExcel(
        ReportBuilderResult result,
        string? periodDescription = null,
        string? filtersDescription = null)
    {
        using var workbook = new XLWorkbook();

        var kpis = workbook.Worksheets.Add("Resumen");
        kpis.Cell(1, 1).Value = "Constructor de informes — RegNeps VICUNHA";
        kpis.Cell(1, 1).Style.Font.SetBold();
        var row = 2;
        if (!string.IsNullOrWhiteSpace(periodDescription))
        {
            kpis.Cell(row, 1).Value = "Periodo";
            SetExcelText(kpis.Cell(row, 2), periodDescription);
            row++;
        }

        if (!string.IsNullOrWhiteSpace(filtersDescription))
        {
            kpis.Cell(row, 1).Value = "Filtros";
            SetExcelText(kpis.Cell(row, 2), filtersDescription);
            row++;
        }

        kpis.Cell(row, 1).Value = "Agrupación";
        SetExcelText(kpis.Cell(row, 2), BuildGroupingLabel(result));
        row++;
        kpis.Cell(row, 1).Value = "Registros";
        kpis.Cell(row, 2).Value = result.TotalRecords;
        row++;
        kpis.Cell(row, 1).Value = "Suma neps";
        kpis.Cell(row, 2).Value = result.SumNeps;
        row++;
        kpis.Cell(row, 1).Value = "Promedio neps";
        kpis.Cell(row, 2).Value = result.AverageNeps;
        row++;
        kpis.Cell(row, 1).Value = "Total mts";
        kpis.Cell(row, 2).Value = result.TotalMts;
        row++;
        kpis.Cell(row, 1).Value = "Índice calidad %";
        kpis.Cell(row, 2).Value = result.QualityIndex;
        row++;
        kpis.Cell(row, 1).Value = "OK / Mención / Crít %";
        kpis.Cell(row, 2).Value =
            $"{FormatDecimal(result.NormalPercent)} / {FormatDecimal(result.WarningPercent)} / {FormatDecimal(result.CriticalPercent)}";
        kpis.Columns().AdjustToContents();

        var groups = workbook.Worksheets.Add("Grupos");
        WriteReportBuilderGroupHeader(groups);
        var gr = 2;
        foreach (var g in result.Groups)
        {
            WriteReportBuilderGroupRow(groups, gr++, g);
        }

        groups.Columns().AdjustToContents();

        WriteTelarRankSheet(workbook, "Mejores telares", result.BestTelars);
        WriteTelarRankSheet(workbook, "Peores telares", result.WorstTelars);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public byte[] BuildReportBuilderPdf(
        ReportBuilderResult result,
        string? periodDescription = null,
        string? filtersDescription = null,
        IReadOnlyList<AnalyticsChartImage>? chartImages = null)
    {
        var navy = "#1F2A44";
        var execBox = "#F5F0E6";
        var tableHeader = Color.FromHex("#E8EEF5");
        var zebra = Color.FromHex("#FAFAFA");

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Content().Column(col =>
                {
                    col.Spacing(8);
                    col.Item().Background(navy).Padding(12).Column(h =>
                    {
                        h.Item().Text("Informe profesional — Constructor RegNeps VICUNHA")
                            .FontColor(Colors.White).SemiBold().FontSize(14);
                        h.Item().PaddingTop(2).Text(BuildGroupingLabel(result)).FontColor(Colors.Grey.Lighten2).FontSize(10);
                        if (!string.IsNullOrWhiteSpace(periodDescription))
                        {
                            h.Item().Text(periodDescription).FontColor(Colors.Grey.Lighten2).FontSize(9);
                        }

                        h.Item().Text($"Generado: {FormatDate(DateTime.UtcNow)}").FontColor(Colors.Grey.Lighten3).FontSize(8);
                    });

                    if (!string.IsNullOrWhiteSpace(filtersDescription))
                    {
                        col.Item().Text($"Filtros: {filtersDescription}").FontSize(9);
                    }

                    col.Item().Background(execBox).Padding(10).Column(box =>
                    {
                        box.Item().Text("Resumen ejecutivo").SemiBold().FontSize(11);
                        box.Item().Text(
                            $"Registros: {result.TotalRecords} | Suma neps: {FormatDecimal(result.SumNeps)} | " +
                            $"Promedio: {FormatDecimal(result.AverageNeps)} | Mts: {FormatMts(result.TotalMts)}");
                        box.Item().Text(
                            $"Min/Max neps: {FormatDecimal(result.MinNeps)} – {FormatDecimal(result.MaxNeps)} | " +
                            $"Calidad: {FormatDecimal(result.QualityIndex)}%");
                        box.Item().Text(
                            $"OK {FormatDecimal(result.NormalPercent)}% · Mención {FormatDecimal(result.WarningPercent)}% · " +
                            $"Crítico/2da {FormatDecimal(result.CriticalPercent)}%");
                    });

                    if (chartImages is { Count: > 0 })
                    {
                        col.Item().PaddingTop(4).Text("Visualizaciones").SemiBold().FontSize(11);
                        foreach (var img in chartImages)
                        {
                            if (img.PngBytes.Length == 0)
                            {
                                continue;
                            }

                            col.Item().PaddingTop(6).Text(img.Title).SemiBold().FontSize(9);
                            col.Item().MaxHeight(220).Image(img.PngBytes).FitArea();
                        }
                    }

                    col.Item().PaddingTop(6).Text("Detalle por grupos").SemiBold().FontSize(11);
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn(3);
                            c.RelativeColumn(1);
                            c.RelativeColumn(1);
                            c.RelativeColumn(1);
                            c.RelativeColumn(1);
                            c.RelativeColumn(1);
                        });
                        t.Header(h =>
                        {
                            h.Cell().Element(c => ReportHeaderCell(c, tableHeader)).Text("Grupo");
                            h.Cell().Element(c => ReportHeaderCell(c, tableHeader)).Text("Regs");
                            h.Cell().Element(c => ReportHeaderCell(c, tableHeader)).Text("Prom.");
                            h.Cell().Element(c => ReportHeaderCell(c, tableHeader)).Text("Mts");
                            h.Cell().Element(c => ReportHeaderCell(c, tableHeader)).Text("Calidad %");
                            h.Cell().Element(c => ReportHeaderCell(c, tableHeader)).Text("Crít.");
                        });

                        var i = 0;
                        foreach (var g in result.Groups.Take(40))
                        {
                            var alt = i++ % 2 == 1 ? zebra : Color.FromHex("#FFFFFF");
                            t.Cell().Element(c => ReportBodyCell(c, alt)).Text(TruncatePdf(g.DisplayLabel, 48));
                            t.Cell().Element(c => ReportBodyCell(c, alt)).Text(g.Count.ToString(Inv));
                            t.Cell().Element(c => ReportBodyCell(c, alt)).Text(FormatDecimal(g.AverageNeps));
                            t.Cell().Element(c => ReportBodyCell(c, alt)).Text(FormatMts(g.TotalMts));
                            t.Cell().Element(c => ReportBodyCell(c, alt)).Text(FormatDecimal(g.QualityIndex));
                            t.Cell().Element(c => ReportBodyCell(c, alt)).Text(g.CriticalCount.ToString(Inv));
                        }
                    });

                    if (result.BestTelars.Count > 0)
                    {
                        col.Item().PaddingTop(8).Text("Mejores telares (menor neps/m²)").SemiBold();
                        col.Item().Element(c => WritePdfTelarRankTable(c, result.BestTelars, tableHeader, zebra));
                    }

                    if (result.WorstTelars.Count > 0)
                    {
                        col.Item().PaddingTop(6).Text("Peores telares (mayor neps/m²)").SemiBold();
                        col.Item().Element(c => WritePdfTelarRankTable(c, result.WorstTelars, tableHeader, zebra));
                    }
                });
            });
        });

        return document.GeneratePdf();
    }

    private static string BuildGroupingLabel(ReportBuilderResult result)
    {
        var primary = ReportBuilderService.DimensionDisplayName(result.PrimaryDimension);
        if (result.SecondaryDimension is not { } sec)
        {
            return primary;
        }

        return $"{primary} + {ReportBuilderService.DimensionDisplayName(sec)}";
    }

    private static void WriteReportBuilderGroupHeader(IXLWorksheet sheet)
    {
        var headers = new[]
        {
            "Grupo", "Registros", "Suma neps", "Promedio", "Mts", "Min", "Max",
            "OK", "Mención", "Crítico/2da", "OK %", "Men %", "Crít %", "Calidad %", "Neps/m²"
        };
        for (var c = 0; c < headers.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = headers[c];
        }

        sheet.Row(1).Style.Font.SetBold();
    }

    private static void WriteReportBuilderGroupRow(IXLWorksheet sheet, int row, ReportBuilderGroupRow g)
    {
        SetExcelText(sheet.Cell(row, 1), g.DisplayLabel);
        sheet.Cell(row, 2).Value = g.Count;
        sheet.Cell(row, 3).Value = g.SumNeps;
        sheet.Cell(row, 4).Value = g.AverageNeps;
        sheet.Cell(row, 5).Value = g.TotalMts;
        sheet.Cell(row, 6).Value = g.MinNeps;
        sheet.Cell(row, 7).Value = g.MaxNeps;
        sheet.Cell(row, 8).Value = g.NormalCount;
        sheet.Cell(row, 9).Value = g.WarningCount;
        sheet.Cell(row, 10).Value = g.CriticalCount;
        sheet.Cell(row, 11).Value = g.NormalPercent;
        sheet.Cell(row, 12).Value = g.WarningPercent;
        sheet.Cell(row, 13).Value = g.CriticalPercent;
        sheet.Cell(row, 14).Value = g.QualityIndex;
        sheet.Cell(row, 15).Value = g.NepsPerM2;
    }

    private static void WriteTelarRankSheet(
        XLWorkbook workbook,
        string name,
        IReadOnlyList<ReportBuilderTelarRank> ranks)
    {
        var sheet = workbook.Worksheets.Add(name);
        sheet.Cell(1, 1).Value = "Telar";
        sheet.Cell(1, 2).Value = "Promedio neps";
        sheet.Cell(1, 3).Value = "Neps/m²";
        sheet.Cell(1, 4).Value = "Registros";
        sheet.Cell(1, 5).Value = "Mts";
        sheet.Row(1).Style.Font.SetBold();
        var row = 2;
        foreach (var t in ranks)
        {
            SetExcelText(sheet.Cell(row, 1), t.Telar);
            sheet.Cell(row, 2).Value = t.AverageNeps;
            sheet.Cell(row, 3).Value = t.NepsPerM2;
            sheet.Cell(row, 4).Value = t.RecordCount;
            sheet.Cell(row, 5).Value = t.TotalMts;
            row++;
        }

        sheet.Columns().AdjustToContents();
    }

    private static void WritePdfTelarRankTable(
        IContainer container,
        IReadOnlyList<ReportBuilderTelarRank> ranks,
        Color headerBg,
        Color zebra)
    {
        container.Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.RelativeColumn(2);
                c.RelativeColumn(1);
                c.RelativeColumn(1);
                c.RelativeColumn(1);
            });
            t.Header(h =>
            {
                h.Cell().Element(c => ReportHeaderCell(c, headerBg)).Text("Telar");
                h.Cell().Element(c => ReportHeaderCell(c, headerBg)).Text("Neps/m²");
                h.Cell().Element(c => ReportHeaderCell(c, headerBg)).Text("Prom.");
                h.Cell().Element(c => ReportHeaderCell(c, headerBg)).Text("Regs");
            });
            var i = 0;
            foreach (var r in ranks.Take(10))
            {
                var alt = i++ % 2 == 1 ? zebra : Color.FromHex("#FFFFFF");
                t.Cell().Element(c => ReportBodyCell(c, alt)).Text(r.Telar);
                t.Cell().Element(c => ReportBodyCell(c, alt)).Text(FormatMts(r.NepsPerM2));
                t.Cell().Element(c => ReportBodyCell(c, alt)).Text(FormatDecimal(r.AverageNeps));
                t.Cell().Element(c => ReportBodyCell(c, alt)).Text(r.RecordCount.ToString(Inv));
            }
        });
    }

    private static string TruncatePdf(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..(max - 1)] + "…";

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
        sheet.Cell(1, 5).Value = "Menciones";
        sheet.Cell(1, 6).Value = "Mts";
        sheet.Row(1).Style.Font.SetBold();
        var row = 2;
        foreach (var g in groups)
        {
            SetExcelText(sheet.Cell(row, 1), g.Key);
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

    private byte[] BuildClassicCsv(
        IReadOnlyList<NepRecord> records,
        AlertConfig config,
        IReadOnlyList<string>? columns = null)
    {
        var sorted = SortForReport(records);
        var cols = ReportColumnIds.Normalize(columns);
        var summary = Summarize(sorted);
        var sb = new StringBuilder();
        sb.AppendLine($"Formula utilizada: Mts calculados = Neps / {NepsConstants.TestLengthM.ToString(Inv)}");
        sb.AppendLine(string.Join(',', cols.Select(id => Escape(ReportColumnIds.Labels[id]))));

        for (var i = 0; i < sorted.Count; i++)
        {
            var r = sorted[i];
            var level = AlertEvaluator.GetLevel(r.Neps, config);
            sb.AppendLine(string.Join(',', cols.Select(id => EscapeCsvColumn(id, GetColumnValue(id, i + 1, r, level)))));
        }

        sb.AppendLine($"TOTAL REGISTROS,{sorted.Count}");
        sb.AppendLine($"TOTAL NEPS,{FormatDecimal(summary.TotalNeps)}");
        sb.AppendLine($"PROMEDIO NEPS,{FormatMts(summary.AverageNeps)}");

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private byte[] BuildClassicExcel(
        IReadOnlyList<NepRecord> records,
        AlertConfig config,
        string title,
        IReadOnlyList<string>? columns = null)
    {
        var sorted = SortForReport(records);
        var cols = ReportColumnIds.Normalize(columns);
        var summary = Summarize(sorted);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Informe");

        sheet.Cell(1, 1).Value = "VICUNHA jeansidentity";
        sheet.Cell(1, 1).Style.Font.SetBold().Font.FontSize = 14;
        sheet.Cell(2, 1).Value = $"Formula utilizada: Mts calculados = Neps / {NepsConstants.TestLengthM.ToString(Inv)}";
        sheet.Cell(3, 1).Value = title;

        for (var i = 0; i < cols.Count; i++)
            sheet.Cell(5, i + 1).Value = ReportColumnIds.Labels[cols[i]];
        sheet.Row(5).Style.Font.SetBold().Fill.BackgroundColor = XLColor.FromHtml("#3C4043");
        sheet.Row(5).Style.Font.FontColor = XLColor.White;

        for (var i = 0; i < sorted.Count; i++)
        {
            var r = sorted[i];
            var level = AlertEvaluator.GetLevel(r.Neps, config);
            var row = i + 6;
            for (var c = 0; c < cols.Count; c++)
            {
                var cell = sheet.Cell(row, c + 1);
                SetExcelCellValue(cell, cols[c], i + 1, r, level);
                if (cols[c] is ReportColumnIds.Estado or ReportColumnIds.Recomendacion)
                    ApplyAlertFill(cell, level);
            }
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
        string? filtersDescription,
        IReadOnlyList<string>? columns = null)
    {
        var sorted = SortForReport(records);
        var cols = ReportColumnIds.Normalize(columns);
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

                    col.Item().Element(c => WritePdfRecordsTable(c, sorted, config, cols, navy, Colors.White));

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
        string title,
        IReadOnlyList<string> columns)
    {
        var sheet = workbook.Worksheets.Add("Registros");
        sheet.Cell(1, 1).Value = title;
        sheet.Range(1, 1, 1, Math.Max(columns.Count, 1)).Merge().Style.Font.SetBold().Font.FontSize = 14;
        sheet.Cell(2, 1).Value = $"Fórmula: Mts calculados = Neps / {NepsConstants.TestLengthM.ToString(Inv)}";

        for (var i = 0; i < columns.Count; i++)
            sheet.Cell(4, i + 1).Value = ReportColumnIds.Labels[columns[i]];
        sheet.Row(4).Style.Font.SetBold().Fill.BackgroundColor = XLColor.FromHtml("#1F2A2E");
        sheet.Row(4).Style.Font.FontColor = XLColor.FromHtml("#F7EAC5");

        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var level = AlertEvaluator.GetLevel(r.Neps, config);
            var row = i + 5;
            for (var c = 0; c < columns.Count; c++)
            {
                var cell = sheet.Cell(row, c + 1);
                SetExcelCellValue(cell, columns[c], i + 1, r, level);
                if (columns[c] is ReportColumnIds.Estado or ReportColumnIds.Recomendacion)
                    ApplyAlertFill(cell, level);
            }
        }

        var summary = Summarize(records);
        var totalRow = records.Count + 5;
        sheet.Cell(totalRow, 1).Value = "TOTALES";
        var nepsCol = columns.ToList().FindIndex(id => id == ReportColumnIds.Neps);
        if (nepsCol >= 0)
        {
            sheet.Cell(totalRow, nepsCol + 1).Value = summary.TotalNeps;
            sheet.Cell(totalRow + 1, 1).Value = "PROMEDIO NEPS";
            sheet.Cell(totalRow + 1, nepsCol + 1).Value = Math.Round(summary.AverageNeps, MidpointRounding.AwayFromZero);
        }
        else
        {
            sheet.Cell(totalRow + 1, 1).Value = $"PROMEDIO NEPS: {FormatMts(summary.AverageNeps)}";
        }

        sheet.Row(totalRow).Style.Font.SetBold();
        sheet.Row(totalRow + 1).Style.Font.SetBold();
        sheet.Columns().AdjustToContents();
    }

    private static void WriteGroupSheet(XLWorkbook workbook, string name, IReadOnlyList<GroupSummary> groups)
    {
        var sheet = workbook.Worksheets.Add(name);
        var headers = new[] { "Clave", "Registros", "Total neps", "Promedio neps", "Críticos", "Menciones" };
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
        Func<AlertLevel, bool> match)
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

        var alerts = SortForReport(all.Where(r => match(AlertEvaluator.GetLevel(r.Neps, config))).ToList());
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

    private static void WritePdfRecordsTable(
        IContainer container,
        IReadOnlyList<NepRecord> records,
        AlertConfig config,
        IReadOnlyList<string> columns,
        Color? headerColor = null,
        Color? zebraColor = null)
    {
        var headerBg = headerColor ?? Color.FromHex("#1F2A2E");
        var zebra = zebraColor ?? Colors.White;
        var cols = columns.Count == 0 ? ReportColumnIds.All : columns;

        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                foreach (var id in cols)
                {
                    if (id == ReportColumnIds.Nro)
                        c.ConstantColumn(24);
                    else if (id is ReportColumnIds.Neps or ReportColumnIds.Telar)
                        c.RelativeColumn(0.55f);
                    else if (id is ReportColumnIds.Mts or ReportColumnIds.Estado)
                        c.RelativeColumn(0.75f);
                    else
                        c.RelativeColumn(1f);
                }
            });

            table.Header(h =>
            {
                foreach (var id in cols)
                    h.Cell().Element(c => ReportHeaderCell(c, headerBg)).Text(ReportColumnIds.Labels[id]);
            });

            for (var i = 0; i < records.Count; i++)
            {
                var r = records[i];
                var level = AlertEvaluator.GetLevel(r.Neps, config);
                var rowBg = i % 2 == 1 ? zebra : Color.FromHex("#FFFFFF");
                IContainer Cell(IContainer c) => ReportBodyCell(c, rowBg);

                foreach (var id in cols)
                    table.Cell().Element(Cell).Text(GetColumnValue(id, i + 1, r, level));
            }
        });
    }

    private static string GetColumnValue(string columnId, int rowNumber, NepRecord record, AlertLevel level) =>
        columnId switch
        {
            ReportColumnIds.Nro => rowNumber.ToString(Inv),
            ReportColumnIds.Fecha => FormatDate(record.CreatedAt),
            ReportColumnIds.Lote => record.LoteTrama ?? string.Empty,
            ReportColumnIds.Tela => record.Tela ?? string.Empty,
            ReportColumnIds.Telar => record.Telar ?? string.Empty,
            ReportColumnIds.Neps => FormatDecimal(record.Neps),
            ReportColumnIds.Mts => FormatMts(record.MtsCalculados),
            ReportColumnIds.Estado => level.ToDisplayLabel(),
            ReportColumnIds.Observacion => record.Observacion ?? string.Empty,
            ReportColumnIds.Recomendacion => string.Join(' ', AlertEvaluator.GetRecommendations(level)),
            _ => string.Empty
        };

    private static void SetExcelCellValue(IXLCell cell, string columnId, int rowNumber, NepRecord record, AlertLevel level)
    {
        switch (columnId)
        {
            case ReportColumnIds.Nro:
                cell.Value = rowNumber;
                break;
            case ReportColumnIds.Fecha:
                cell.Value = SpreadsheetFormulaGuard.NeutralizeText(FormatDate(record.CreatedAt));
                break;
            case ReportColumnIds.Lote:
                cell.Value = SpreadsheetFormulaGuard.NeutralizeText(record.LoteTrama);
                break;
            case ReportColumnIds.Tela:
                cell.Value = SpreadsheetFormulaGuard.NeutralizeText(record.Tela);
                break;
            case ReportColumnIds.Telar:
                cell.Value = SpreadsheetFormulaGuard.NeutralizeText(record.Telar);
                break;
            case ReportColumnIds.Neps:
                cell.Value = record.Neps;
                break;
            case ReportColumnIds.Mts:
                cell.Value = Math.Round(record.MtsCalculados, MidpointRounding.AwayFromZero);
                break;
            case ReportColumnIds.Estado:
                cell.Value = SpreadsheetFormulaGuard.NeutralizeText(level.ToDisplayLabel());
                break;
            case ReportColumnIds.Observacion:
                cell.Value = SpreadsheetFormulaGuard.NeutralizeText(record.Observacion);
                break;
            case ReportColumnIds.Recomendacion:
                cell.Value = SpreadsheetFormulaGuard.NeutralizeText(
                    string.Join(' ', AlertEvaluator.GetRecommendations(level)));
                break;
            default:
                cell.Value = string.Empty;
                break;
        }
    }

    private static void WritePdfAlertTable(
        IContainer container,
        IReadOnlyList<NepRecord> alerts,
        AlertConfig config,
        Color? headerColor = null,
        Color? zebraColor = null)
    {
        var headerBg = headerColor ?? Color.FromHex("#1F2A2E");
        var zebra = zebraColor ?? Colors.White;

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
                    h.Cell().Element(c => ReportHeaderCell(c, headerBg)).Text(title);
            });

            for (var i = 0; i < alerts.Count; i++)
            {
                var r = alerts[i];
                var level = AlertEvaluator.GetLevel(r.Neps, config);
                var rowBg = i % 2 == 1 ? zebra : Color.FromHex("#FFFFFF");
                IContainer Cell(IContainer c) => ReportBodyCell(c, rowBg);

                table.Cell().Element(Cell).Text(FormatDate(r.CreatedAt));
                table.Cell().Element(Cell).Text(r.Telar);
                table.Cell().Element(Cell).Text(r.Tela);
                table.Cell().Element(Cell).Text(r.LoteTrama);
                table.Cell().Element(Cell).Text(FormatDecimal(r.Neps));
                table.Cell().Element(Cell).Text(level.ToDisplayLabel());
            }
        });
    }

    private static void WritePdfGroupTable(
        IContainer container,
        string[] headers,
        List<string[]> rows,
        Color? headerColor = null,
        Color? zebraColor = null)
    {
        var headerBg = headerColor ?? Color.FromHex("#1F2A2E");
        var zebra = zebraColor ?? Colors.White;

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
                    h.Cell().Element(c => ReportHeaderCell(c, headerBg)).Text(title);
            });

            for (var i = 0; i < rows.Count; i++)
            {
                var rowBg = i % 2 == 1 ? zebra : Color.FromHex("#FFFFFF");
                IContainer Cell(IContainer c) => ReportBodyCell(c, rowBg);
                foreach (var cell in rows[i])
                    table.Cell().Element(Cell).Text(cell);
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
                    list.Count(x => NepsQualityCriteria.IsCriticalNotificationLevel(
                        AlertEvaluator.GetLevel(x.Neps, config))),
                    list.Count(x => AlertEvaluator.GetLevel(x.Neps, config) == AlertLevel.Mention));
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
            AlertLevel.Ok => XLColor.FromHtml("#C8E6C9"),
            AlertLevel.Mention => XLColor.FromHtml("#FFE0B2"),
            AlertLevel.CriticalAdjustment => XLColor.FromHtml("#FFCDD2"),
            AlertLevel.SecondQuality => XLColor.FromHtml("#EF9A9A"),
            _ => XLColor.NoColor
        };
    }

    private static IContainer HeaderCell(IContainer container) =>
        ReportHeaderCell(container, Color.FromHex("#1F2A2E"));

    private static IContainer BodyCell(IContainer container) =>
        ReportBodyCell(container, Colors.White);

    private static IContainer ReportHeaderCell(IContainer container, Color background) =>
        container.DefaultTextStyle(x => x.SemiBold().FontSize(8).FontColor(Colors.White))
            .PaddingVertical(4)
            .PaddingHorizontal(3)
            .Background(background)
            .BorderBottom(0.5f)
            .BorderColor(Colors.Grey.Lighten1);

    private static IContainer ReportBodyCell(IContainer container, Color background) =>
        container.DefaultTextStyle(x => x.FontSize(7))
            .PaddingVertical(3)
            .PaddingHorizontal(3)
            .Background(background)
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

    private static bool IsNumericExportColumn(string columnId) =>
        columnId is ReportColumnIds.Nro or ReportColumnIds.Neps or ReportColumnIds.Mts;

    private static string EscapeCsvColumn(string columnId, string? value) =>
        IsNumericExportColumn(columnId)
            ? Escape(value)
            : EscapeText(value);

    /// <summary>CSV de texto: neutraliza fórmulas y escapa comillas/comas.</summary>
    private static string EscapeText(string? value) =>
        Escape(SpreadsheetFormulaGuard.NeutralizeText(value));

    private static void SetExcelText(IXLCell cell, string? value) =>
        cell.Value = SpreadsheetFormulaGuard.NeutralizeText(value);

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
