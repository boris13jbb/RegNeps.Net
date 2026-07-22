namespace RegNeps.Domain.Enums;

/// <summary>Roles alineados con Firebase custom claims de la app Flutter.</summary>
public enum AppUserRole
{
    Operario = 0,
    Supervisor = 1,
    Admin = 2,
    Gerencia = 3,
    SuperAdmin = 4
}

public static class AppUserRoleExtensions
{
    public static string ToDisplayLabel(this AppUserRole role) => role switch
    {
        AppUserRole.Operario => "Operario",
        AppUserRole.Supervisor => "Supervisor",
        AppUserRole.Admin => "Administrador",
        AppUserRole.Gerencia => "Gerencia",
        AppUserRole.SuperAdmin => "Super administrador",
        _ => role.ToString()
    };
}
