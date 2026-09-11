namespace RegNeps.Domain.Permissions;

public enum PermissionCategory
{
    General = 0,
    Records = 1,
    Alerts = 2,
    Catalogs = 3,
    Reports = 4,
    Security = 5,
    Settings = 6
}

/// <summary>Definición de un permiso del catálogo (etiqueta, descripción y reglas de edición).</summary>
public sealed record PermissionDefinition(
    AppPermission Permission,
    PermissionCategory Category,
    string Label,
    string Description,
    bool Sensitive = false,
    bool ExclusiveToSuperAdmin = false);

/// <summary>
/// Catálogo único de permisos. La UI y el seeding leen de aquí; no duplicar etiquetas en las páginas.
/// </summary>
public static class PermissionCatalog
{
    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(AppPermission.ViewDashboard, PermissionCategory.General, "Ver dashboard",
            "Permite consultar el panel de inicio y el análisis general."),

        new(AppPermission.CaptureRecords, PermissionCategory.Records, "Capturar registros",
            "Permite registrar nuevas mediciones de neps."),
        new(AppPermission.ViewRecords, PermissionCategory.Records, "Ver registros",
            "Permite consultar registros existentes."),
        new(AppPermission.EditRecords, PermissionCategory.Records, "Editar registros",
            "Permite corregir registros ya capturados."),
        new(AppPermission.DeleteRecords, PermissionCategory.Records, "Eliminar registros",
            "Permite eliminar registros individuales.", Sensitive: true),
        new(AppPermission.ClearAllRecords, PermissionCategory.Records, "Limpiar todos los registros",
            "Permite vaciar el historial de registros.", Sensitive: true),

        new(AppPermission.ViewAlerts, PermissionCategory.Alerts, "Ver alertas",
            "Permite consultar alertas de calidad."),
        new(AppPermission.ApplyCorrectiveAction, PermissionCategory.Alerts, "Aplicar acciones correctivas",
            "Permite registrar acciones correctivas sobre alertas."),
        new(AppPermission.EditAlertConfig, PermissionCategory.Alerts, "Editar configuración de alertas",
            "Permite modificar umbrales y reglas de alerta."),

        new(AppPermission.ManageFabrics, PermissionCategory.Catalogs, "Administrar telas",
            "Permite mantener el catálogo de telas y lotes de trama."),

        new(AppPermission.ManageReports, PermissionCategory.Reports, "Administrar reportes",
            "Permite crear, consultar y guardar informes."),
        new(AppPermission.ExportReports, PermissionCategory.Reports, "Exportar reportes",
            "Permite descargar reportes y exportaciones."),

        new(AppPermission.ManageUsers, PermissionCategory.Security, "Administrar usuarios",
            "Permite crear usuarios y cambiar su estado.", Sensitive: true),
        new(AppPermission.DeleteUsers, PermissionCategory.Security, "Eliminar usuarios",
            "Permite dar de baja cuentas de usuario.", Sensitive: true),
        new(AppPermission.ChangeRoles, PermissionCategory.Security, "Cambiar roles",
            "Permite asignar roles a otros usuarios.", Sensitive: true),
        new(AppPermission.ManageRoles, PermissionCategory.Security, "Administrar roles y permisos",
            "Permite modificar la matriz de permisos. Es exclusivo del Super Administrador.",
            Sensitive: true, ExclusiveToSuperAdmin: true),

        new(AppPermission.ViewSettings, PermissionCategory.Settings, "Ver configuración",
            "Permite consultar la configuración del sistema."),
        new(AppPermission.ManageSettings, PermissionCategory.Settings, "Administrar configuración",
            "Permite modificar la configuración general del sistema.", Sensitive: true)
    ];

    public static IReadOnlyList<PermissionCategory> Categories { get; } =
        All.Select(x => x.Category).Distinct().ToArray();

    public static PermissionDefinition Get(AppPermission permission) =>
        All.First(x => x.Permission == permission);

    public static string CategoryLabel(PermissionCategory category) => category switch
    {
        PermissionCategory.General => "General",
        PermissionCategory.Records => "Registros",
        PermissionCategory.Alerts => "Alertas",
        PermissionCategory.Catalogs => "Catálogos",
        PermissionCategory.Reports => "Reportes",
        PermissionCategory.Security => "Usuarios y seguridad",
        PermissionCategory.Settings => "Configuración",
        _ => category.ToString()
    };

    public static bool IsKnown(AppPermission permission) =>
        All.Any(x => x.Permission == permission);
}
