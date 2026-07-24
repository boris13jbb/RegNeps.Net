using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Mvc;
using RegNeps.Application.Abstractions;
using RegNeps.Application.Analytics;
using RegNeps.Application.Auth;
using RegNeps.Application.Reports;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Filters;
using RegNeps.Domain.Permissions;
using RegNeps.Infrastructure;
using RegNeps.Infrastructure.Migration;
using RegNeps.Web.Auth;
using RegNeps.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Charts/PDF: capturas JPEG pueden superar el límite SignalR por defecto (32 KB).
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 15 * 1024 * 1024;
});
builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(options =>
{
    options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(2);
});
builder.Services.AddSingleton<RegNeps.Web.Export.TempExportStore>();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, RegNepsRevalidatingAuthStateProvider>();
builder.Services.AddScoped<CurrentUserService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/api/logout";
        options.AccessDeniedPath = "/login";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.Cookie.Name = "RegNeps.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // Intranet suele usar HTTP; SameAsRequest exige Secure solo cuando la petición es HTTPS.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();

var connectionString = builder.Configuration.GetConnectionString("RegNeps");
var useSqlServer = builder.Configuration.GetValue("Database:UseSqlServer", false);

// SQLite relativo → carpeta estable bajo ContentRoot (no depende del cwd).
if (!useSqlServer && !string.IsNullOrWhiteSpace(connectionString) &&
    connectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase))
{
    var dataSource = connectionString.Split("Data Source=", 2, StringSplitOptions.TrimEntries)[1]
        .Split(';', 2)[0]
        .Trim();
    if (!Path.IsPathRooted(dataSource))
    {
        var dataDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dataDir);
        var absolute = Path.Combine(dataDir, dataSource);
        connectionString = $"Data Source={absolute}";
        Console.WriteLine($"[RegNeps] SQLite: {absolute}");
    }
}

builder.Services.AddRegNepsInfrastructure(connectionString, useSqlServer);

var urls = builder.Configuration["Urls"] ?? "http://0.0.0.0:5080";
builder.WebHost.UseUrls(urls);

var app = builder.Build();

await app.Services.EnsureDatabaseCreatedAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapPost("/api/login", async (
    HttpContext http,
    AuthService auth,
    [FromForm] string username,
    [FromForm] string password) =>
{
    // #region agent log
    DebugSessionLog.Write("H1", "Program.cs:login", "login_attempt", new { hasUser = !string.IsNullOrWhiteSpace(username) });
    // #endregion
    try
    {
        var user = await auth.LoginAsync(username, password);
        await AuthClaims.SignInAsync(http, user);
        // #region agent log
        DebugSessionLog.Write("H1", "Program.cs:login", "login_ok", new { role = user.EffectiveRole.ToString(), isSuper = user.IsSuperAdmin });
        // #endregion
        return Results.Redirect("/");
    }
    catch (Exception)
    {
        // #region agent log
        DebugSessionLog.Write("H1", "Program.cs:login", "login_fail", new { });
        // #endregion
        return Results.Redirect("/login?error=1");
    }
}).DisableAntiforgery().AllowAnonymous();

app.MapPost("/api/logout", async (HttpContext http) =>
{
    // #region agent log
    DebugSessionLog.Write("H2", "Program.cs:logout", "logout_post", new { auth = http.User.Identity?.IsAuthenticated == true });
    // #endregion
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).DisableAntiforgery().RequireAuthorization();

// Compatibilidad con enlaces antiguos; no cierra sesión sin autenticación activa.
app.MapGet("/api/logout", async (HttpContext http) =>
{
    // #region agent log
    DebugSessionLog.Write("H2", "Program.cs:logout", "logout_get", new { auth = http.User.Identity?.IsAuthenticated == true });
    // #endregion
    if (http.User.Identity?.IsAuthenticated == true)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    return Results.Redirect("/login");
}).AllowAnonymous();

app.MapGet("/api/export/{format}", async (
    string format,
    HttpContext http,
    ReportExportAppService export,
    [FromQuery] string? telar,
    [FromQuery] string? tela,
    [FromQuery] string? from,
    [FromQuery] string? to,
    [FromQuery] string? style) =>
{
    if (http.User.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }

    if (!HasPermission(http.User, AppPermission.ExportReports))
    {
        return Results.Forbid();
    }

    if (!ReportDateRange.TryParseQuery(from, to, out var range, out var rangeError))
    {
        return Results.BadRequest(rangeError);
    }

    var session = SessionFrom(http.User);
    var filters = new RecordFilters
    {
        Telar = string.IsNullOrWhiteSpace(telar) ? null : telar.Trim(),
        Tela = string.IsNullOrWhiteSpace(tela) ? null : tela.Trim()
    };
    range.ApplyTo(filters);

    try
    {
        var file = await export.ExportAsync(
            format, filters, session.UserId, session.SeesAllRecords, style ?? "completo");
        return Results.File(file.Bytes, file.ContentType, file.FileName);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status404NotFound);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).RequireAuthorization();

app.MapGet("/api/export/saved/{id:guid}/{format}", async (
    Guid id,
    string format,
    HttpContext http,
    ReportExportAppService export) =>
{
    if (http.User.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }

    if (!HasPermission(http.User, AppPermission.ManageReports)
        && !HasPermission(http.User, AppPermission.ExportReports))
    {
        return Results.Forbid();
    }

    try
    {
        var session = SessionFrom(http.User);
        var file = await export.ExportSavedAsync(id, format, session.UserId, session.SeesAllRecords);
        return Results.File(file.Bytes, file.ContentType, file.FileName);
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(ex.Message);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).RequireAuthorization();

app.MapGet("/api/export/analytics/{format}", async (
    string format,
    HttpContext http,
    AnalyticsService analytics,
    IExportFileService files,
    [FromQuery] int? days,
    [FromQuery] string? from,
    [FromQuery] string? to,
    [FromQuery] string? telar,
    [FromQuery] string? tela,
    [FromQuery] string? lote,
    [FromQuery] string? turno,
    [FromQuery] string? alerta,
    [FromQuery] string? operario,
    [FromQuery] string? grouping) =>
{
    if (http.User.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }

    if (!HasPermission(http.User, AppPermission.ViewDashboard))
    {
        return Results.Forbid();
    }

    var session = SessionFrom(http.User);
    var filters = new RecordFilters
    {
        Telar = string.IsNullOrWhiteSpace(telar) ? null : telar.Trim(),
        Tela = string.IsNullOrWhiteSpace(tela) ? null : tela.Trim(),
        LoteTrama = string.IsNullOrWhiteSpace(lote) ? null : lote.Trim(),
        Turno = string.IsNullOrWhiteSpace(turno) ? null : turno.Trim(),
        Operario = string.IsNullOrWhiteSpace(operario) ? null : operario.Trim()
    };

    if (!string.IsNullOrWhiteSpace(alerta) &&
        Enum.TryParse<AlertLevel>(alerta, ignoreCase: true, out var alertLevel))
    {
        filters.AlertLevel = alertLevel;
    }

    string periodDescription;
    if (days is > 0)
    {
        var toDay = DateTime.Today;
        var fromDay = toDay.AddDays(-(days.Value - 1));
        ReportDateRange.FromLocalCalendarDates(fromDay, toDay).ApplyTo(filters);
        periodDescription = $"Últimos {days} días";
    }
    else if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to))
    {
        if (!ReportDateRange.TryParseQuery(from, to, out var range, out var rangeError))
        {
            return Results.BadRequest(rangeError);
        }

        range.ApplyTo(filters);
        periodDescription = range.LabelLocal;
    }
    else
    {
        periodDescription = "Todo el histórico visible";
    }

    var chartGrouping = grouping?.Trim().ToLowerInvariant() switch
    {
        "week" or "semana" => ChartTimeGrouping.Week,
        "month" or "mes" => ChartTimeGrouping.Month,
        "year" or "año" or "anio" => ChartTimeGrouping.Year,
        _ => ChartTimeGrouping.Day
    };

    var summary = await analytics.BuildAsync(
        filters, session.UserId, session.SeesAllRecords, chartGrouping);
    format = format.ToLowerInvariant();
    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
    return format switch
    {
        "csv" => Results.File(files.BuildAnalyticsCsv(summary, periodDescription), "text/csv", $"graficas_analytics_{stamp}.csv"),
        "xlsx" or "excel" => Results.File(
            files.BuildAnalyticsExcel(summary, periodDescription),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"graficas_analytics_{stamp}.xlsx"),
        "pdf" => Results.File(files.BuildAnalyticsPdf(summary, periodDescription), "application/pdf", $"graficas_analytics_{stamp}.pdf"),
        _ => Results.BadRequest("Formato no soportado. Use csv, xlsx o pdf.")
    };
}).RequireAuthorization();

app.MapGet("/api/export/temp/{id:guid}", (
    Guid id,
    HttpContext http,
    RegNeps.Web.Export.TempExportStore store) =>
{
    if (http.User.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }

    if (!HasPermission(http.User, AppPermission.ExportReports) &&
        !HasPermission(http.User, AppPermission.ManageReports) &&
        !HasPermission(http.User, AppPermission.ViewDashboard))
    {
        return Results.Forbid();
    }

    var ownerId = http.User.FindFirstValue(AuthClaims.UserId);
    // #region agent log
    DebugSessionLog.Write("H3", "Program.cs:temp-export", "temp_take", new { hasOwner = !string.IsNullOrEmpty(ownerId) });
    // #endregion
    if (!store.TryTake(id, ownerId, out var entry) || entry is null)
    {
        return Results.NotFound("El archivo temporal expiró, no existe o no le pertenece. Genere el export de nuevo.");
    }

    return Results.File(entry.Bytes, entry.ContentType, entry.FileName);
}).RequireAuthorization();

app.MapGet("/api/export/fabrics/{format}", async (
    string format,
    HttpContext http,
    IFabricRepository fabrics,
    IExportFileService files) =>
{
    if (!HasPermission(http.User, AppPermission.ManageFabrics))
    {
        return Results.Forbid();
    }

    var list = await fabrics.GetAllAsync();
    format = format.ToLowerInvariant();
    if (format == "csv")
    {
        return Results.File(files.BuildFabricsCsv(list), "text/csv", "telas.csv");
    }

    return Results.File(
        files.BuildFabricsExcel(list),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "telas.xlsx");
}).RequireAuthorization();

app.MapGet("/api/export/lotes/{format}", async (
    string format,
    HttpContext http,
    ILoteTramaRepository lotes,
    IExportFileService files) =>
{
    if (!HasPermission(http.User, AppPermission.ManageFabrics))
    {
        return Results.Forbid();
    }

    var list = await lotes.GetAllAsync();
    format = format.ToLowerInvariant();
    if (format == "csv")
    {
        return Results.File(files.BuildLotesCsv(list), "text/csv", "lotes_trama.csv");
    }

    return Results.File(
        files.BuildLotesExcel(list),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "lotes_trama.xlsx");
}).RequireAuthorization();

app.MapGet("/api/import/template", (
    HttpContext http,
    IExportFileService files) =>
{
    if (!HasPermission(http.User, AppPermission.EditRecords))
    {
        return Results.Forbid();
    }

    return Results.File(
        files.BuildImportTemplate(),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "plantilla_importacion_neps.xlsx");
}).RequireAuthorization();

app.MapPost("/api/migration/import", async (
    HttpContext http,
    HistoricalDataMigrationService migration,
    IFormFile file,
    [FromForm] string? tempPassword) =>
{
    // Migración restringida a superadministrador (no solo ManageUsers).
    if (!IsSuperAdminUser(http.User))
    {
        // #region agent log
        DebugSessionLog.Write("H4", "Program.cs:migration", "migration_forbidden", new { });
        // #endregion
        return Results.Forbid();
    }

    if (file.Length == 0)
    {
        return Results.BadRequest("Archivo vacío.");
    }

    if (file.Length > 200 * 1024 * 1024)
    {
        return Results.BadRequest("El archivo supera el límite de 200 MB.");
    }

    var ext = Path.GetExtension(file.FileName);
    if (!string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest("Solo se aceptan archivos .json.");
    }

    // #region agent log
    DebugSessionLog.Write("H4", "Program.cs:migration", "migration_start", new { size = file.Length });
    // #endregion
    await using var stream = file.OpenReadStream();
    var result = await migration.ImportFromJsonAsync(stream, tempPassword);
    return Results.Json(result);
}).DisableAntiforgery().RequireAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static bool HasPermission(ClaimsPrincipal user, AppPermission permission)
{
    if (user.Identity?.IsAuthenticated != true)
    {
        return false;
    }

    if (IsSuperAdminUser(user))
    {
        return true;
    }

    return user.HasClaim("permission", permission.ToString());
}

static bool IsSuperAdminUser(ClaimsPrincipal user) =>
    string.Equals(user.FindFirstValue(AuthClaims.IsSuperAdmin), "true", StringComparison.OrdinalIgnoreCase);

static UserSession SessionFrom(ClaimsPrincipal user)
{
    var id = user.FindFirstValue(AuthClaims.UserId);
    var username = user.FindFirstValue(AuthClaims.Username) ?? "";
    var display = user.FindFirstValue(AuthClaims.DisplayName) ?? username;
    Enum.TryParse<AppUserRole>(user.FindFirstValue(AuthClaims.Role), out var role);
    var isSuper = IsSuperAdminUser(user);
    return new UserSession(id, username, display, role, isSuper);
}
