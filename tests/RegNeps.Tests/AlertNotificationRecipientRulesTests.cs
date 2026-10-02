using RegNeps.Application.Alerts;
using Xunit;

namespace RegNeps.Tests;

public sealed class AlertNotificationRecipientRulesTests
{
    [Fact]
    public void ShouldReceive_Requires_AlertasActivas()
    {
        var ok = AlertNotificationRecipientRules.ShouldReceiveCriticalAlert(
            alertasActivas: false,
            isActive: true,
            hasViewAlerts: true,
            seesAllRecords: true,
            "user-1",
            null,
            "owner-1");

        Assert.False(ok);
    }

    [Fact]
    public void ShouldReceive_Requires_ViewAlerts()
    {
        var ok = AlertNotificationRecipientRules.ShouldReceiveCriticalAlert(
            alertasActivas: true,
            isActive: true,
            hasViewAlerts: false,
            seesAllRecords: true,
            "user-1",
            null,
            "owner-1");

        Assert.False(ok);
    }

    [Fact]
    public void ShouldReceive_SeesAll_With_ViewAlerts()
    {
        var ok = AlertNotificationRecipientRules.ShouldReceiveCriticalAlert(
            alertasActivas: true,
            isActive: true,
            hasViewAlerts: true,
            seesAllRecords: true,
            "sup-1",
            null,
            "other-owner");

        Assert.True(ok);
    }

    [Fact]
    public void ShouldReceive_Without_SeesAll_Only_Own_Record()
    {
        Assert.True(AlertNotificationRecipientRules.ShouldReceiveCriticalAlert(
            alertasActivas: true,
            isActive: true,
            hasViewAlerts: true,
            seesAllRecords: false,
            "op-1",
            null,
            "op-1"));

        Assert.False(AlertNotificationRecipientRules.ShouldReceiveCriticalAlert(
            alertasActivas: true,
            isActive: true,
            hasViewAlerts: true,
            seesAllRecords: false,
            "op-1",
            null,
            "op-2"));
    }

    [Fact]
    public void ShouldReceive_Matches_ExternalUserId()
    {
        Assert.True(AlertNotificationRecipientRules.ShouldReceiveCriticalAlert(
            alertasActivas: true,
            isActive: true,
            hasViewAlerts: true,
            seesAllRecords: false,
            "guid-local",
            "firebase-uid",
            "firebase-uid"));
    }

    [Fact]
    public void ShouldReceive_Inactive_User_Is_False()
    {
        Assert.False(AlertNotificationRecipientRules.ShouldReceiveCriticalAlert(
            alertasActivas: true,
            isActive: false,
            hasViewAlerts: true,
            seesAllRecords: true,
            "admin-1",
            null,
            "owner"));
    }
}
