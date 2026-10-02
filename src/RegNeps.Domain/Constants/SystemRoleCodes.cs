using RegNeps.Domain.Enums;

namespace RegNeps.Domain.Constants;

/// <summary>Códigos de los cinco roles de sistema alineados con <see cref="AppUserRole"/>.</summary>
public static class SystemRoleCodes
{
    public const string Operario = nameof(AppUserRole.Operario);
    public const string Supervisor = nameof(AppUserRole.Supervisor);
    public const string Admin = nameof(AppUserRole.Admin);
    public const string Gerencia = nameof(AppUserRole.Gerencia);
    public const string SuperAdmin = nameof(AppUserRole.SuperAdmin);

    public static string FromEnum(AppUserRole role) => role.ToString();

    public static bool TryParseEnum(string? code, out AppUserRole role)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            role = AppUserRole.Operario;
            return false;
        }

        return Enum.TryParse(code, ignoreCase: false, out role);
    }

    public static bool IsSuperAdminCode(string? code) =>
        string.Equals(code, SuperAdmin, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<(string Code, string Name, bool IsSystem, bool SeesAllRecords)> Definitions { get; } =
    [
        (Operario, "Operario", true, false),
        (Supervisor, "Supervisor", true, true),
        (Admin, "Administrador", true, true),
        (Gerencia, "Gerencia", true, true),
        (SuperAdmin, "Super administrador", true, true)
    ];
}
