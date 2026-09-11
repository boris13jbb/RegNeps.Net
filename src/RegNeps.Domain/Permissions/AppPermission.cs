namespace RegNeps.Domain.Permissions;

/// <summary>Permisos granulares alineados con Flutter Permission.</summary>
public enum AppPermission
{
    ViewDashboard,
    CaptureRecords,
    ViewRecords,
    EditRecords,
    DeleteRecords,
    ClearAllRecords,
    ViewAlerts,
    ApplyCorrectiveAction,
    ManageFabrics,
    ManageReports,
    ExportReports,
    EditAlertConfig,
    ManageUsers,
    DeleteUsers,
    ChangeRoles,
    ViewSettings,
    ManageSettings,
    /// <summary>Administrar la matriz de roles y permisos. Reservado al Super Administrador.</summary>
    ManageRoles
}
