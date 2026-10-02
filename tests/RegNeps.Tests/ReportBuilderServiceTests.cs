using RegNeps.Application.Reports;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;

namespace RegNeps.Tests;

public class ReportBuilderServiceTests
{
    private static AlertConfig DefaultConfig() => new()
    {
        LimiteNormalMax = 30,
        LimiteAdvertenciaMax = 60
    };

    [Fact]
    public void Build_Groups_By_Telar_With_Stats()
    {
        var config = DefaultConfig();
        var records = new List<NepRecord>
        {
            MakeRecord("003", 30, AlertLevel.Normal),
            MakeRecord("003", 50, AlertLevel.Advertencia),
            MakeRecord("104", 70, AlertLevel.Critico),
        };

        var result = ReportBuilderService.Build(records, config, new ReportBuilderOptions
        {
            Primary = ReportBuilderDimension.Telar
        });

        Assert.Equal(3, result.TotalRecords);
        Assert.Equal(2, result.Groups.Count);

        var g003 = result.Groups.Single(g => g.PrimaryKey == "003");
        Assert.Equal(2, g003.Count);
        Assert.Equal(80, g003.SumNeps);
        Assert.Equal(40, g003.AverageNeps);
        Assert.Equal(1, g003.NormalCount);
        Assert.Equal(1, g003.WarningCount);
        Assert.Equal(0, g003.CriticalCount);
        Assert.Equal(50, g003.NormalPercent);
        Assert.Equal(50, g003.WarningPercent);
        Assert.Equal(100, g003.QualityIndex);

        var g104 = result.Groups.Single(g => g.PrimaryKey == "104");
        Assert.Equal(1, g104.CriticalCount);
        Assert.Equal(0, g104.QualityIndex);
    }

    [Fact]
    public void Build_Dual_Dimension_Telar_And_Turno()
    {
        var config = DefaultConfig();
        var records = new List<NepRecord>
        {
            MakeRecord("003", 20, AlertLevel.Normal, turno: "A"),
            MakeRecord("003", 40, AlertLevel.Advertencia, turno: "B"),
            MakeRecord("104", 30, AlertLevel.Normal, turno: "A"),
        };

        var result = ReportBuilderService.Build(records, config, new ReportBuilderOptions
        {
            Primary = ReportBuilderDimension.Telar,
            Secondary = ReportBuilderDimension.Turno
        });

        Assert.Equal(3, result.Groups.Count);
        Assert.All(result.Groups, g => Assert.NotNull(g.SecondaryKey));
        Assert.Contains(result.Groups, g => g.PrimaryKey == "003" && g.SecondaryKey == "A");
        Assert.Contains(result.Groups, g => g.PrimaryKey == "003" && g.SecondaryKey == "B");
    }

    [Fact]
    public void Build_QualityIndex_Overall_And_PerGroup()
    {
        var config = DefaultConfig();
        var records = new List<NepRecord>
        {
            MakeRecord("003", 70, AlertLevel.Critico),
            MakeRecord("003", 70, AlertLevel.Critico),
            MakeRecord("104", 20, AlertLevel.Normal),
            MakeRecord("104", 20, AlertLevel.Normal),
        };

        var result = ReportBuilderService.Build(records, config, new ReportBuilderOptions
        {
            Primary = ReportBuilderDimension.Telar
        });

        Assert.Equal(50, result.QualityIndex);
        Assert.Equal(0, result.Groups.Single(g => g.PrimaryKey == "003").QualityIndex);
        Assert.Equal(100, result.Groups.Single(g => g.PrimaryKey == "104").QualityIndex);
    }

    [Fact]
    public void Build_Telar_Ranking_By_Lower_NepsPerM2()
    {
        var config = DefaultConfig();
        var records = new List<NepRecord>
        {
            MakeRecord("003", 18, AlertLevel.Normal),
            MakeRecord("104", 54, AlertLevel.Advertencia),
            MakeRecord("801", 36, AlertLevel.Normal),
        };

        var result = ReportBuilderService.Build(records, config, new ReportBuilderOptions { TelarRankingTake = 3 });

        Assert.Equal("003", result.BestTelars[0].Telar);
        Assert.Equal("801", result.BestTelars[1].Telar);
        Assert.Equal("104", result.WorstTelars[0].Telar);

        var expectedBestNepsM2 = 18 / NepsConstants.TestLengthM;
        Assert.Equal(expectedBestNepsM2, result.BestTelars[0].NepsPerM2, 3);
    }

    [Fact]
    public void Build_Uses_MtsCalculados_From_Records()
    {
        var config = DefaultConfig();
        var records = new List<NepRecord> { MakeRecord("003", 9, AlertLevel.Normal) };

        var result = ReportBuilderService.Build(records, config, new ReportBuilderOptions
        {
            Primary = ReportBuilderDimension.Telar
        });

        var expectedMts = 9 / NepsConstants.TestLengthM;
        Assert.Equal(expectedMts, result.TotalMts, 3);
        Assert.Equal(expectedMts, result.Groups[0].TotalMts, 3);
    }

    [Fact]
    public void Build_Rejects_Duplicate_Dimensions()
    {
        var config = DefaultConfig();
        var records = new List<NepRecord> { MakeRecord("003", 9, AlertLevel.Normal) };

        Assert.Throws<ArgumentException>(() =>
            ReportBuilderService.Build(records, config, new ReportBuilderOptions
            {
                Primary = ReportBuilderDimension.Tela,
                Secondary = ReportBuilderDimension.Tela
            }));
    }

    private static NepRecord MakeRecord(
        string telar,
        double neps,
        AlertLevel _,
        string turno = "A")
    {
        return new NepRecord
        {
            Telar = telar,
            Neps = neps,
            Tela = "TelaX",
            LoteTrama = "L1",
            Turno = turno,
            Operario = "Op1",
            CreatedAt = DateTime.UtcNow
        };
    }
}
