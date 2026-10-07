using System.Security.Claims;
using RegNeps.Application.Permissions;
using RegNeps.Application.Records;
using RegNeps.Application.Sync;
using RegNeps.Domain.Enums;
using RegNeps.Web.Auth;

namespace RegNeps.Web.Sync;

public static class SyncApiEndpoints
{
    public static IEndpointRouteBuilder MapSyncApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sync").RequireAuthorization();

        group.MapPost("/push", PushAsync);
        group.MapPost("/pull", PullAsync);

        return app;
    }

    private static async Task<IResult> PushAsync(
        SyncPushRequest request,
        HttpContext http,
        SyncAppService sync,
        IPermissionService permissions,
        CancellationToken ct)
    {
        var actor = await TryCreateActorAsync(http.User, permissions, ct);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        var correlationId = http.TraceIdentifier;
        var response = await sync.PushAsync(request, actor, correlationId, ct);
        return Results.Json(response);
    }

    private static async Task<IResult> PullAsync(
        SyncPullRequest request,
        HttpContext http,
        SyncAppService sync,
        IPermissionService permissions,
        CancellationToken ct)
    {
        var actor = await TryCreateActorAsync(http.User, permissions, ct);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        var correlationId = http.TraceIdentifier;
        var response = await sync.PullAsync(request, actor, correlationId, ct);
        return Results.Json(response);
    }

    private static async Task<RecordActor?> TryCreateActorAsync(
        ClaimsPrincipal user,
        IPermissionService permissions,
        CancellationToken ct)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var id = user.FindFirstValue(AuthClaims.UserId);
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        await permissions.EnsureLoadedAsync(ct);

        var username = user.FindFirstValue(AuthClaims.Username) ?? "";
        var display = user.FindFirstValue(AuthClaims.DisplayName) ?? username;
        Enum.TryParse<AppUserRole>(user.FindFirstValue(AuthClaims.Role), out var role);
        var roleCode = user.FindFirstValue(AuthClaims.RoleCode);
        var isSuper = string.Equals(
            user.FindFirstValue(AuthClaims.IsSuperAdmin),
            "true",
            StringComparison.OrdinalIgnoreCase);
        var external = user.FindFirstValue(AuthClaims.ExternalUserId);

        var effectiveCode = isSuper
            ? Domain.Constants.SystemRoleCodes.SuperAdmin
            : string.IsNullOrWhiteSpace(roleCode)
                ? Domain.Constants.SystemRoleCodes.FromEnum(role)
                : roleCode.Trim();

        var seesAll = permissions.SeesAllRecords(effectiveCode, isSuper);

        return RecordActor.Create(
            id,
            username,
            display,
            role,
            isSuper,
            external,
            roleCode,
            seesAll);
    }
}
