using RegNeps.Domain.Entities;
using RegNeps.Domain.Permissions;

namespace RegNeps.Application.Reports;

/// <summary>
/// Actor para operaciones de informes guardados (identidad + flags de visibilidad/permisos).
/// </summary>
public sealed record ReportAccessActor(
    string? UserId,
    bool SeesAllRecords,
    bool HasManageReports,
    bool HasExportReports)
{
    public static ReportAccessActor Create(
        string? userId,
        bool seesAllRecords,
        bool hasManageReports,
        bool hasExportReports) =>
        new(userId, seesAllRecords, hasManageReports, hasExportReports);
}

/// <summary>
/// Regla única de acceso a informes guardados (snapshot, ver datos, export).
/// Autor del informe, o SeesAllRecords con ManageReports/ExportReports.
/// </summary>
public static class SavedReportAccess
{
    public const string NotFoundMessage = "Informe no encontrado.";

    public static bool CanAccess(
        string? reportAuthorUserId,
        string? viewerUserId,
        bool viewerSeesAllRecords,
        bool hasManageReports,
        bool hasExportReports)
    {
        if (!string.IsNullOrWhiteSpace(reportAuthorUserId)
            && !string.IsNullOrWhiteSpace(viewerUserId)
            && string.Equals(reportAuthorUserId.Trim(), viewerUserId.Trim(), StringComparison.Ordinal))
        {
            return true;
        }

        if (viewerSeesAllRecords && (hasManageReports || hasExportReports))
        {
            return true;
        }

        return false;
    }

    public static bool CanAccess(SavedReport report, ReportAccessActor actor)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(actor);
        return CanAccess(
            report.CreatedByUserId,
            actor.UserId,
            actor.SeesAllRecords,
            actor.HasManageReports,
            actor.HasExportReports);
    }

    public static void EnsureCanAccess(SavedReport report, ReportAccessActor actor)
    {
        if (!CanAccess(report, actor))
        {
            throw new InvalidOperationException(NotFoundMessage);
        }
    }

    public static void EnsureCanManageReports(bool hasManageReports)
    {
        if (!hasManageReports)
        {
            throw new UnauthorizedAccessException("No tiene permiso para gestionar informes (ManageReports).");
        }
    }

    public static bool HasReportPermission(AppPermission permission, ReportAccessActor actor) =>
        permission switch
        {
            AppPermission.ManageReports => actor.HasManageReports,
            AppPermission.ExportReports => actor.HasExportReports,
            _ => false
        };
}
