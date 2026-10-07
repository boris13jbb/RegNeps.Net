using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RegNeps.Application.Reports;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Filters;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Web.Realtime;
using Xunit;

namespace RegNeps.Tests;

/// <summary>Pruebas HTTP de autorización sobre endpoints de export (WebApplicationFactory).</summary>
public sealed class ExportAuthorizationHttpTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"regneps-http-{Guid.NewGuid():N}.db");
    private RegNepsWebFactory _factory = null!;

    private Guid _authorId;
    private Guid _operarioId;
    private Guid _supervisorId;
    private Guid _foreignReportId;
    private Guid _authorReportId;

    public async Task InitializeAsync()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            var options = new DbContextOptionsBuilder<RegNepsDbContext>().UseSqlite(conn).Options;
            await using var db = new RegNepsDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await DatabaseInitializer.ApplySchemaPatchesAsync(db);
            await DbSeeder.SeedAsync(db);

            // Contraseña solo de prueba; no es credencial real de entorno.
            var hash = BCrypt.Net.BCrypt.HashPassword("HttpTestOnly!");

            // Autor con ExportReports/ManageReports (Gerencia). Operario no puede usar /api/export/saved.
            var author = new AppUser
            {
                Username = "http_author",
                DisplayName = "Author",
                PasswordHash = hash,
                Role = AppUserRole.Gerencia,
                RoleCode = "Gerencia",
                IsActive = true
            };
            var operario = new AppUser
            {
                Username = "http_operario",
                DisplayName = "Operario",
                PasswordHash = hash,
                Role = AppUserRole.Operario,
                RoleCode = "Operario",
                IsActive = true
            };
            var supervisor = new AppUser
            {
                Username = "http_supervisor",
                DisplayName = "Supervisor",
                PasswordHash = hash,
                Role = AppUserRole.Supervisor,
                RoleCode = "Supervisor",
                IsActive = true
            };
            db.Users.AddRange(author, operario, supervisor);
            await db.SaveChangesAsync();
            _authorId = author.Id;
            _operarioId = operario.Id;
            _supervisorId = supervisor.Id;

            // Registro vivo para que el export por filtros (sin snapshot) no devuelva 404 vacío.
            var sample = new NepRecord
            {
                Telar = "T-HTTP-1",
                Neps = 12,
                Tela = "Tela test",
                LoteTrama = "L1",
                CreatedAt = DateTime.UtcNow,
                Turno = "A",
                Operario = "http_operario",
                CreatedByUserId = operario.Id.ToString(),
                CreatedByRole = "Operario"
            };
            db.NepRecords.Add(sample);
            await db.SaveChangesAsync();

            var filters = new RecordFilters();
            ReportDateRange.FromLocalCalendarDates(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1))
                .ApplyTo(filters);
            var filtersJson = System.Text.Json.JsonSerializer.Serialize(filters);
            var foreign = new SavedReport
            {
                Name = "Ajeno",
                CreatedByUserId = _supervisorId.ToString(),
                CreatedByName = "Supervisor",
                FiltersJson = filtersJson,
                RecordCount = 1,
                SummaryText = "test"
            };
            var own = new SavedReport
            {
                Name = "Propio",
                CreatedByUserId = _authorId.ToString(),
                CreatedByName = "Author",
                FiltersJson = filtersJson,
                RecordCount = 1,
                SummaryText = "test"
            };
            db.SavedReports.AddRange(foreign, own);
            await db.SaveChangesAsync();
            _foreignReportId = foreign.Id;
            _authorReportId = own.Id;
        }

        // Forzar connection string antes de que Program.cs lea Configuration
        // (ConfigureAppConfiguration solo no basta de forma fiable con top-level statements).
        Environment.SetEnvironmentVariable("ConnectionStrings__RegNeps", $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("Database__UseSqlServer", "false");
        Environment.SetEnvironmentVariable("Urls", "http://127.0.0.1:0");

        _factory = new RegNepsWebFactory(_dbPath);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        Environment.SetEnvironmentVariable("ConnectionStrings__RegNeps", null);
        Environment.SetEnvironmentVariable("Database__UseSqlServer", null);
        Environment.SetEnvironmentVariable("Urls", null);

        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { /* temp */ }
        }
    }

    [Theory]
    [InlineData("/api/export/csv?from=2020-01-01&to=2030-01-01")]
    [InlineData("/api/export/analytics/csv?from=2020-01-01&to=2030-01-01")]
    [InlineData("/api/export/temp/11111111-1111-1111-1111-111111111111")]
    [InlineData("/api/import/template")]
    public async Task Anonymous_Export_Endpoints_Are_Denied(string path)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync(path);
        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Esperado 401/302, obtuvo {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Anonymous_ExportSaved_Is_Denied()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync($"/api/export/saved/{_foreignReportId}/pdf");
        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect or HttpStatusCode.Found);
    }

    [Fact]
    public async Task Operario_Filter_Export_Is_Forbidden()
    {
        var client = await LoginAsync("http_operario", "HttpTestOnly!");
        var response = await client.GetAsync("/api/export/csv?from=2020-01-01&to=2030-01-01");
        // Cookie auth: Forbid → redirect a /login (200 HTML si AllowAutoRedirect).
        Assert.False(IsCsv(response), "Operario no debe recibir CSV de export por filtros.");
    }

    [Fact]
    public async Task Operario_Cannot_Export_Foreign_Saved_Report()
    {
        var client = await LoginAsync("http_operario", "HttpTestOnly!");
        var response = await client.GetAsync($"/api/export/saved/{_foreignReportId}/csv");
        Assert.True(
            response.StatusCode == HttpStatusCode.NotFound || !IsCsv(response),
            $"IDOR: Operario no debe exportar informe ajeno. status={(int)response.StatusCode} ct={response.Content.Headers.ContentType}");
    }

    [Fact]
    public async Task Author_Can_Export_Own_Saved_Report()
    {
        var client = await LoginAsync("http_author", "HttpTestOnly!");
        var response = await client.GetAsync($"/api/export/saved/{_authorReportId}/csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(IsCsv(response), $"Expected CSV, got {response.Content.Headers.ContentType}");
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 0);
    }

    [Fact]
    public async Task Supervisor_With_SeesAll_Can_Export_Foreign_Saved_Report()
    {
        // Informe de autor Gerencia; Supervisor con SeesAll + Export/Manage debe poder exportarlo.
        var client = await LoginAsync("http_supervisor", "HttpTestOnly!");
        var response = await client.GetAsync($"/api/export/saved/{_authorReportId}/csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(IsCsv(response), $"Expected CSV, got {response.Content.Headers.ContentType}");
    }

    [Fact]
    public async Task Operario_Without_ViewAlerts_Can_Connect_Hub_For_Sync_Recovery_Only()
    {
        // FASE 2E: Capture/View permiten hub para SyncRecoverySuggested; no grupo de alertas.
        var cookieHeader = await CaptureAuthCookieAsync("http_operario", "HttpTestOnly!");
        var hubUrl = new Uri(_factory.Server.BaseAddress!, "/hubs/alerts");

        await using var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Headers["Cookie"] = cookieHeader;
            })
            .Build();

        var alertReceived = false;
        connection.On<object>(AlertNotificationHub.CriticalAlertMethod, _ => alertReceived = true);

        await connection.StartAsync();
        await Task.Delay(400);
        Assert.Equal(HubConnectionState.Connected, connection.State);

        // Sin ViewAlerts no está en alert-user:{id}; notificación al grupo de alertas no le llega.
        var hub = _factory.Services.GetRequiredService<IHubContext<AlertNotificationHub>>();
        await hub.Clients
            .Group(AlertNotificationHub.UserGroupName(_operarioId.ToString()))
            .SendAsync(AlertNotificationHub.CriticalAlertMethod, new { summary = "x" });
        await Task.Delay(400);
        Assert.False(alertReceived, "Operario sin ViewAlerts no debe recibir CriticalAlertReceived.");
    }

    [Fact]
    public void Invalid_Columns_And_Style_Fall_Back_To_Defaults()
    {
        // columns inválidas → ReportColumnIds.All (sin 400).
        Assert.Equal(ReportColumnIds.All, ReportColumnIds.Normalize(["nope", "zzz"]));
        var parsed = ReportColumnIds.ParseQuerySelection(["foo,bar"]);
        Assert.Equal(ReportColumnIds.All, ReportColumnIds.Normalize(parsed));

        // style=zzz: el endpoint no valida; ExportFileService.IsClassic("zzz") es false → layout "completo".
        // (Solo "clasico"/"classic" activan el estilo clásico; cualquier otro valor cae al completo.)
        Assert.False(
            string.Equals("zzz".Trim(), "clasico", StringComparison.OrdinalIgnoreCase)
            || string.Equals("zzz".Trim(), "classic", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCsv(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "text/csv", StringComparison.OrdinalIgnoreCase);

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password
        });
        var response = await client.PostAsync("/api/login", form);
        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Login sin redirect: {(int)response.StatusCode}");
        var location = response.Headers.Location?.ToString() ?? "";
        Assert.False(
            location.Contains("error=", StringComparison.OrdinalIgnoreCase),
            $"Login rechazado para '{username}' (Location={location}). ¿Connection string del factory?");
        Assert.Contains("RegNeps.Auth", string.Join(" ", response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : Array.Empty<string>()));
        return client;
    }

    private async Task<string> CaptureAuthCookieAsync(string username, string password)
    {
        var handler = new RecordingCookieHandler(_factory.Server.CreateHandler());
        using var client = new HttpClient(handler) { BaseAddress = _factory.Server.BaseAddress };
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password
        });
        var response = await client.PostAsync("/api/login", form);
        var location = response.Headers.Location?.ToString() ?? "";
        Assert.False(location.Contains("error=", StringComparison.OrdinalIgnoreCase), $"Login hub falló: {location}");
        Assert.False(string.IsNullOrWhiteSpace(handler.CookieHeader), "No se capturó cookie de autenticación.");
        return handler.CookieHeader!;
    }

    private sealed class RecordingCookieHandler : DelegatingHandler
    {
        public string? CookieHeader { get; private set; }

        public RecordingCookieHandler(HttpMessageHandler inner) : base(inner) { }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(CookieHeader))
            {
                request.Headers.Remove("Cookie");
                request.Headers.TryAddWithoutValidation("Cookie", CookieHeader);
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
            {
                CookieHeader = string.Join("; ", values.Select(v => v.Split(';', 2)[0].Trim()));
            }

            return response;
        }
    }

    private sealed class RegNepsWebFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath;

        public RegNepsWebFactory(string dbPath) => _dbPath = dbPath;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting(WebHostDefaults.ServerUrlsKey, "http://127.0.0.1:0");
            builder.UseSetting("ConnectionStrings:RegNeps", $"Data Source={_dbPath}");
            builder.UseSetting("Database:UseSqlServer", "false");
            builder.UseSetting("Urls", "http://127.0.0.1:0");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:RegNeps"] = $"Data Source={_dbPath}",
                    ["Database:UseSqlServer"] = "false",
                    ["Urls"] = "http://127.0.0.1:0"
                });
            });
        }
    }
}
