using RegNeps.Domain.Constants;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;

namespace RegNeps.Application.Records;

/// <summary>
/// Identidad autenticada para operaciones de registros.
/// Se construye en el servidor a partir de la sesión; no se confía en datos del cliente.
/// </summary>
public sealed record RecordActor(
    string UserId,
    string Username,
    string DisplayName,
    AppUserRole Role,
    bool IsSuperAdmin,
    string? ExternalUserId = null,
    string? RoleCode = null,
    bool? SeesAllRecordsOverride = null)
{
    public AppUserRole EffectiveRole =>
        IsSuperAdmin ? AppUserRole.SuperAdmin : Role;

    public string EffectiveRoleCode =>
        IsSuperAdmin
            ? SystemRoleCodes.SuperAdmin
            : string.IsNullOrWhiteSpace(RoleCode)
                ? SystemRoleCodes.FromEnum(Role)
                : RoleCode.Trim();

    /// <summary>
    /// Consulta la matriz inicial. Los servicios de aplicación deben validar con
    /// <c>IPermissionService</c> para respetar la configuración persistida.
    /// </summary>
    public bool Has(AppPermission permission) =>
        RolePermissions.Has(Role, IsSuperAdmin, true, permission);

    public bool SeesAllRecords =>
        SeesAllRecordsOverride
        ?? EffectiveRole is not AppUserRole.Operario;

    public static RecordActor Create(
        string? userId,
        string username,
        string displayName,
        AppUserRole role,
        bool isSuperAdmin,
        string? externalUserId = null,
        string? roleCode = null,
        bool? seesAllRecords = null)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new UnauthorizedRecordAccessException(
                "Se requiere un usuario autenticado con identificador válido.");
        }

        return new RecordActor(
            userId.Trim(),
            username ?? "",
            displayName ?? username ?? "",
            role,
            isSuperAdmin,
            string.IsNullOrWhiteSpace(externalUserId) ? null : externalUserId.Trim(),
            string.IsNullOrWhiteSpace(roleCode) ? null : roleCode.Trim(),
            seesAllRecords);
    }
}

public enum RecordQueryScope
{
    /// <summary>Usa SeesAllRecords del actor (pantallas de consulta).</summary>
    Default = 0,
    /// <summary>Forzar solo registros propios (captura / sesión personal).</summary>
    PersonalOnly = 1
}

public sealed class UnauthorizedRecordAccessException : InvalidOperationException
{
    public UnauthorizedRecordAccessException(string message) : base(message)
    {
    }
}

public sealed class RecordConcurrencyConflictException : InvalidOperationException
{
    public RecordConcurrencyConflictException(string message) : base(message)
    {
    }
}

public enum RecordSaveStatus
{
    Saved = 0,
    ValidationFailed = 1,
    PersistenceFailed = 2,
    AlreadySaved = 3,
    Unauthorized = 4
}

public sealed class RecordSaveResult
{
    public RecordSaveStatus Status { get; init; }
    public Domain.Entities.NepRecord? Record { get; init; }
    public string? Error { get; init; }
    public Domain.Enums.AlertLevel? AlertLevel { get; init; }
    public bool AlertEvaluationFailed { get; init; }
    public bool ReloadFailed { get; init; }

    public bool IsSuccess =>
        Status is RecordSaveStatus.Saved or RecordSaveStatus.AlreadySaved;
}
