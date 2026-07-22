using RegNeps.Domain.Enums;

namespace RegNeps.Domain.Permissions;

/// <summary>Matriz rol → permisos (paridad con role_permissions.dart).</summary>
public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<AppUserRole, HashSet<AppPermission>> Matrix =
        new Dictionary<AppUserRole, HashSet<AppPermission>>
        {
            [AppUserRole.SuperAdmin] =
            [
                AppPermission.ViewDashboard, AppPermission.CaptureRecords, AppPermission.ViewRecords,
                AppPermission.EditRecords, AppPermission.DeleteRecords, AppPermission.ClearAllRecords,
                AppPermission.ViewAlerts, AppPermission.ApplyCorrectiveAction, AppPermission.ManageFabrics,
                AppPermission.ManageReports, AppPermission.ExportReports, AppPermission.EditAlertConfig,
                AppPermission.ManageUsers, AppPermission.DeleteUsers, AppPermission.ChangeRoles,
                AppPermission.ViewSettings, AppPermission.ManageSettings
            ],
            [AppUserRole.Admin] =
            [
                AppPermission.ViewDashboard, AppPermission.CaptureRecords, AppPermission.ViewRecords,
                AppPermission.EditRecords, AppPermission.DeleteRecords, AppPermission.ClearAllRecords,
                AppPermission.ViewAlerts, AppPermission.ApplyCorrectiveAction, AppPermission.ManageFabrics,
                AppPermission.ManageReports, AppPermission.ExportReports,
                AppPermission.ViewSettings, AppPermission.ManageSettings
            ],
            [AppUserRole.Supervisor] =
            [
                AppPermission.ViewDashboard, AppPermission.ViewRecords, AppPermission.EditRecords,
                AppPermission.ViewAlerts, AppPermission.ApplyCorrectiveAction,
                AppPermission.ExportReports, AppPermission.ManageReports
            ],
            [AppUserRole.Operario] =
            [
                AppPermission.CaptureRecords, AppPermission.ViewRecords
            ],
            [AppUserRole.Gerencia] =
            [
                AppPermission.ViewDashboard, AppPermission.ViewRecords, AppPermission.ViewAlerts,
                AppPermission.ExportReports, AppPermission.ManageReports
            ]
        };

    public static bool Has(AppUserRole role, AppPermission permission) =>
        Matrix.TryGetValue(role, out var set) && set.Contains(permission);

    public static bool Has(AppUserRole role, bool isSuperAdmin, bool isActive, AppPermission permission)
    {
        if (!isActive)
        {
            return false;
        }

        var effective = isSuperAdmin ? AppUserRole.SuperAdmin : role;
        return Has(effective, permission);
    }

    public static IReadOnlyCollection<AppPermission> ForRole(AppUserRole role) =>
        Matrix.TryGetValue(role, out var set) ? set : Array.Empty<AppPermission>();
}
