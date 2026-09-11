namespace RegNeps.Domain.Permissions;

/// <summary>
/// Permisos granulares. Los valores numéricos están fijos: se guardan en RolePermissions
/// y no deben reordenarse.
/// </summary>
public enum AppPermission
{
    ViewDashboard = 0,
    CaptureRecords = 1,
    ViewRecords = 2,
    EditRecords = 3,
    DeleteRecords = 4,
    ClearAllRecords = 5,
    ViewAlerts = 6,
    ApplyCorrectiveAction = 7,
    ManageFabrics = 8,
    ManageReports = 9,
    ExportReports = 10,
    EditAlertConfig = 11,
    ManageUsers = 12,
    DeleteUsers = 13,
    ChangeRoles = 14,
    ViewSettings = 15,
    /// <summary>
    /// Reservado. La configuración actual solo tiene umbrales de alerta, cubiertos por
    /// EditAlertConfig. No se ofrece en el catálogo para no simular una operación que no existe.
    /// El valor 16 se conserva para no reinterpretar filas históricas.
    /// </summary>
    ManageSettings = 16,
    /// <summary>Administrar la matriz de roles y permisos. Reservado al Super Administrador.</summary>
    ManageRoles = 17
}
