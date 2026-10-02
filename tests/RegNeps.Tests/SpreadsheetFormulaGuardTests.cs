using System.Text;
using ClosedXML.Excel;
using RegNeps.Application.Analytics;
using RegNeps.Application.Export;
using RegNeps.Application.Reports;
using RegNeps.Domain.Entities;
using RegNeps.Infrastructure.Export;
using Xunit;

namespace RegNeps.Tests;

public sealed class SpreadsheetFormulaGuardTests
{
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tTAB", "'\tTAB")]
    [InlineData("\rCR", "'\rCR")]
    [InlineData("safe", "safe")]
    [InlineData("", "")]
    public void NeutralizeText_Prefixes_Risky_Cells(string input, string expected) =>
        Assert.Equal(expected, SpreadsheetFormulaGuard.NeutralizeText(input));

    [Fact]
    public void Csv_Escapes_Formula_Text_But_Keeps_Negative_Neps()
    {
        var config = new AlertConfig { LimiteNormalMax = 18, LimiteAdvertenciaMax = 45 };
        var records = new List<NepRecord>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Telar = "=CMD()",
                Tela = "+evil",
                LoteTrama = "@x",
                Neps = -12.5,
                Observacion = "-not-a-number-text",
                CreatedAt = DateTime.UtcNow,
                Turno = "A",
                Operario = "Op"
            }
        };

        var bytes = new ExportFileService().BuildCsv(
            records,
            config,
            columns: [ReportColumnIds.Telar, ReportColumnIds.Tela, ReportColumnIds.Neps, ReportColumnIds.Lote, ReportColumnIds.Observacion]);
        var csv = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');

        Assert.Contains("'=CMD()", csv, StringComparison.Ordinal);
        Assert.Contains("'+evil", csv, StringComparison.Ordinal);
        Assert.Contains("'@x", csv, StringComparison.Ordinal);
        Assert.Contains("'-not-a-number-text", csv, StringComparison.Ordinal);
        Assert.Contains("-12.5", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("'-12.5", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Numeric_Negative_Is_Not_Treated_As_Text_Injection_Risk_For_Neps_Column()
    {
        // El guard de texto sí marca '-', pero EscapeCsvColumn no lo aplica a Neps/Mts.
        Assert.True(SpreadsheetFormulaGuard.IsFormulaInjectionRisk("-12.5"));
        Assert.Equal("'-12.5", SpreadsheetFormulaGuard.NeutralizeText("-12.5"));
    }

    [Fact]
    public void Excel_Keeps_Negative_Neps_And_Mts_As_Numbers()
    {
        var config = new AlertConfig { LimiteNormalMax = 18, LimiteAdvertenciaMax = 45 };
        var records = new List<NepRecord>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Telar = "=HACK()",
                Tela = "Denim",
                LoteTrama = "L1",
                Neps = -9,
                CreatedAt = DateTime.UtcNow,
                Turno = "A",
                Operario = "Op"
            }
        };

        var bytes = new ExportFileService().BuildExcel(
            records,
            config,
            columns: [ReportColumnIds.Telar, ReportColumnIds.Neps, ReportColumnIds.Mts]);

        using var stream = new MemoryStream(bytes);
        using var wb = new XLWorkbook(stream);
        var sheet = wb.Worksheet("Registros");
        // Fila 4 = cabeceras; fila 5 = primer dato.
        var telarCell = sheet.Cell(5, 1);
        var nepsCell = sheet.Cell(5, 2);
        var mtsCell = sheet.Cell(5, 3);

        // ClosedXML/Excel tratan la comilla inicial como marcador de texto: GetString omite el '.
        Assert.Equal(XLDataType.Text, telarCell.DataType);
        Assert.False(telarCell.HasFormula);
        Assert.Equal("=HACK()", telarCell.GetString());
        Assert.Equal(XLDataType.Number, nepsCell.DataType);
        Assert.Equal(-9d, nepsCell.GetDouble());
        Assert.Equal(XLDataType.Number, mtsCell.DataType);
        Assert.Equal(Math.Round(-9 / 0.09, MidpointRounding.AwayFromZero), mtsCell.GetDouble());
    }

    [Fact]
    public void Fabrics_And_Analytics_Csv_Neutralize_Text_Keys()
    {
        var files = new ExportFileService();
        var fabricsCsv = Encoding.UTF8.GetString(files.BuildFabricsCsv(
        [
            new Fabric { Name = "=FAB()", Code = "+CODE", IsActive = true, CreatedAt = DateTime.UtcNow }
        ])).TrimStart('\uFEFF');
        Assert.Contains("'=FAB()", fabricsCsv, StringComparison.Ordinal);
        Assert.Contains("'+CODE", fabricsCsv, StringComparison.Ordinal);

        var summary = new AnalyticsSummary
        {
            TotalRecords = 1,
            ByTelar =
            [
                new GroupSummary("=TELAR()", TotalNeps: 1, TotalMts: 1, RecordCount: 1, AverageNeps: 1, CriticalCount: 0, WarningCount: 0)
            ],
            ByTela = [],
            ByDay = []
        };
        var analyticsCsv = Encoding.UTF8.GetString(files.BuildAnalyticsCsv(summary)).TrimStart('\uFEFF');
        Assert.Contains("'=TELAR()", analyticsCsv, StringComparison.Ordinal);
    }
}
