using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;
using RegNeps.Domain.Services;

namespace RegNeps.Tests;

public class AlertEvaluatorTests
{
    private static AlertConfig DefaultConfig() => new()
    {
        LimiteNormalMax = 30,
        LimiteAdvertenciaMax = 60,
        AlertasActivas = true
    };

    [Theory]
    [InlineData(0, AlertLevel.Normal)]
    [InlineData(30, AlertLevel.Normal)]
    [InlineData(31, AlertLevel.Advertencia)]
    [InlineData(60, AlertLevel.Advertencia)]
    [InlineData(61, AlertLevel.Critico)]
    [InlineData(100, AlertLevel.Critico)]
    public void GetLevel_Uses_Thresholds_30_And_60(double neps, AlertLevel expected)
    {
        var level = AlertEvaluator.GetLevel(neps, DefaultConfig());
        Assert.Equal(expected, level);
    }

    [Fact]
    public void GetLevel_When_Alerts_Disabled_Returns_Normal()
    {
        var cfg = DefaultConfig();
        cfg.AlertasActivas = false;
        Assert.Equal(AlertLevel.Normal, AlertEvaluator.GetLevel(999, cfg));
    }

    [Fact]
    public void HasCriticalRecurrence_Requires_Configured_Count()
    {
        var cfg = DefaultConfig();
        cfg.CantidadReincidenciasCriticas = 3;
        cfg.DiasParaReincidencia = 1;
        var now = DateTime.UtcNow;
        var records = new List<NepRecord>
        {
            new() { Telar = "T1", Neps = 70, CreatedAt = now.AddHours(-2) },
            new() { Telar = "T1", Neps = 80, CreatedAt = now.AddHours(-1) },
            new() { Telar = "T1", Neps = 90, CreatedAt = now.AddMinutes(-10) },
        };

        Assert.True(AlertEvaluator.HasCriticalRecurrence(records, "T1", cfg, now));
        Assert.False(AlertEvaluator.HasCriticalRecurrence(records, "T2", cfg, now));
    }
}

public class NepRecordFormulaTests
{
    [Theory]
    [InlineData(9, 100)]
    [InlineData(0.09, 1)]
    [InlineData(4.5, 50)]
    public void MtsCalculados_Is_Neps_Divided_By_TestLength(double neps, double expectedMts)
    {
        var record = new NepRecord { Neps = neps };
        Assert.Equal(NepsConstants.TestLengthM, 0.09);
        Assert.Equal(expectedMts, record.MtsCalculados, precision: 6);
    }
}

public class RolePermissionsTests
{
    [Fact]
    public void Admin_Can_Edit_Alert_Config()
    {
        Assert.True(RolePermissions.Has(AppUserRole.Admin, AppPermission.EditAlertConfig));
    }

    [Fact]
    public void Operario_Cannot_Export()
    {
        Assert.False(RolePermissions.Has(AppUserRole.Operario, AppPermission.ExportReports));
    }

    [Fact]
    public void Only_SuperAdmin_Has_ManageUsers()
    {
        Assert.True(RolePermissions.Has(AppUserRole.SuperAdmin, AppPermission.ManageUsers));
        Assert.False(RolePermissions.Has(AppUserRole.Admin, AppPermission.ManageUsers));
    }
}
