using RegNeps.Domain.Constants;
using RegNeps.Domain.Enums;

namespace RegNeps.Tests;

public class NepsQualityCriteriaTests
{
    [Theory]
    [InlineData(0, AlertLevel.Ok)]
    [InlineData(18, AlertLevel.Ok)]
    [InlineData(19, AlertLevel.Mention)]
    [InlineData(45, AlertLevel.Mention)]
    [InlineData(46, AlertLevel.CriticalAdjustment)]
    [InlineData(54, AlertLevel.CriticalAdjustment)]
    [InlineData(55, AlertLevel.SecondQuality)]
    public void ClassifyByScore_Exact_Q_Boundaries(int q, AlertLevel expected)
    {
        Assert.Equal(expected, NepsQualityCriteria.ClassifyByScore(q));
        Assert.Equal(expected, NepsQualityCriteria.ClassifyByNeps(q));
    }

    [Theory]
    [InlineData(200, AlertLevel.Ok)]
    [InlineData(200.0001, AlertLevel.Mention)]
    [InlineData(500, AlertLevel.Mention)]
    [InlineData(500.0001, AlertLevel.CriticalAdjustment)]
    [InlineData(600, AlertLevel.CriticalAdjustment)]
    [InlineData(600.0001, AlertLevel.SecondQuality)]
    public void ClassifyByNepsPerM2_Exact_Density_Boundaries(double nepsPerM2, AlertLevel expected)
    {
        Assert.Equal(expected, NepsQualityCriteria.ClassifyByNepsPerM2(nepsPerM2));
    }

    [Theory]
    [InlineData(18)]
    [InlineData(45)]
    [InlineData(54)]
    public void Integer_Q_Matches_Density_Bands(int q)
    {
        var byScore = NepsQualityCriteria.ClassifyByScore(q);
        var byDensity = NepsQualityCriteria.ClassifyByNepsPerM2(q / NepsConstants.TestLengthM);
        Assert.Equal(byScore, byDensity);
    }

    [Fact]
    public void Formula_Uses_TestLength_0_09()
    {
        Assert.Equal(0.09, NepsConstants.TestLengthM);
        Assert.Equal(200d, 18 / NepsConstants.TestLengthM, 6);
        Assert.Equal(500d, 45 / NepsConstants.TestLengthM, 6);
        Assert.Equal(600d, 54 / NepsConstants.TestLengthM, 6);
    }

    [Theory]
    [InlineData(AlertLevel.CriticalAdjustment, true)]
    [InlineData(AlertLevel.SecondQuality, true)]
    [InlineData(AlertLevel.Mention, false)]
    [InlineData(AlertLevel.Ok, false)]
    public void IsCriticalNotificationLevel_Both_Critical_Bands(AlertLevel level, bool expected)
    {
        Assert.Equal(expected, NepsQualityCriteria.IsCriticalNotificationLevel(level));
    }

    [Fact]
    public void Sql_Exclusive_Uppers_Align_With_Rounded_Q()
    {
        // Neps < 18.5 → Q ≤ 18; Neps < 45.5 → Q ≤ 45; Neps < 54.5 → Q ≤ 54
        Assert.Equal(18.5, NepsQualityCriteria.OkNepsExclusiveUpper);
        Assert.Equal(45.5, NepsQualityCriteria.MentionNepsExclusiveUpper);
        Assert.Equal(54.5, NepsQualityCriteria.CriticalAdjustmentNepsExclusiveUpper);

        Assert.Equal(AlertLevel.Ok, NepsQualityCriteria.ClassifyByNeps(18.49));
        Assert.Equal(AlertLevel.Mention, NepsQualityCriteria.ClassifyByNeps(18.5));
        Assert.Equal(AlertLevel.Mention, NepsQualityCriteria.ClassifyByNeps(45.49));
        Assert.Equal(AlertLevel.CriticalAdjustment, NepsQualityCriteria.ClassifyByNeps(45.5));
        Assert.Equal(AlertLevel.CriticalAdjustment, NepsQualityCriteria.ClassifyByNeps(54.49));
        Assert.Equal(AlertLevel.SecondQuality, NepsQualityCriteria.ClassifyByNeps(54.5));
    }
}
