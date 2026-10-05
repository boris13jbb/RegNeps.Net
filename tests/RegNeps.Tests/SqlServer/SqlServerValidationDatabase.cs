using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RegNeps.Application.Permissions;
using RegNeps.Application.Records;
using RegNeps.Application.Sync;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Infrastructure;
using RegNeps.Infrastructure.Persistence;

namespace RegNeps.Tests.SqlServer;

/// <summary>
/// FASE 2J — validación física contra SQL Server real.
/// La instancia se toma de <see cref="ServerVariable"/> (connection string sin base de datos, p. ej.
/// <c>Server=.\SA;Integrated Security=True;TrustServerCertificate=True</c>). Sin variable, las pruebas
/// quedan Skipped (nunca PASS sustituto con SQLite).
/// </summary>
internal static class SqlServerValidationEnv
{
    public const string ServerVariable = "REGNEPS_SQLSERVER_VALIDATION";

    /// <summary>Si vale <c>1</c>, conserva las bases creadas para inspección manual.</summary>
    public const string KeepVariable = "REGNEPS_SQLSERVER_VALIDATION_KEEP";

    public static string? ServerConnectionString =>
        Environment.GetEnvironmentVariable(ServerVariable);

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ServerConnectionString);

    public static string SkipReason =>
        $"SQL Server físico no configurado: defina {ServerVariable} (FASE 2J).";
}

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!SqlServerValidationEnv.IsConfigured)
        {
            Skip = SqlServerValidationEnv.SkipReason;
        }
    }
}

public sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute()
    {
        if (!SqlServerValidationEnv.IsConfigured)
        {
            Skip = SqlServerValidationEnv.SkipReason;
        }
    }
}

/// <summary>
/// Base SQL Server aislada (<c>RegNeps_Validation_2J_*</c>) con el wiring productivo
/// (<see cref="DependencyInjection.AddRegNepsInfrastructure"/> + <see cref="DatabaseInitializer"/>).
/// Cada <see cref="BootstrapAsync"/> equivale a un arranque Web nuevo.
/// </summary>
internal sealed class SqlServerValidationDatabase : IAsyncDisposable
{
    private readonly List<IServiceScope> _scopes = [];
    private ServiceProvider? _services;

    private SqlServerValidationDatabase(string name, string connectionString, string serverConnectionString)
    {
        Name = name;
        ConnectionString = connectionString;
        ServerConnectionString = serverConnectionString;
    }

    public string Name { get; }

    public string ConnectionString { get; }

    private string ServerConnectionString { get; }

    public ServiceProvider Services =>
        _services ?? throw new InvalidOperationException("Llame a StartAsync antes de usar servicios.");

    public static SqlServerValidationDatabase Create(string label)
    {
        var server = SqlServerValidationEnv.ServerConnectionString
            ?? throw new InvalidOperationException(SqlServerValidationEnv.SkipReason);
        var safeLabel = Regex.Replace(label, "[^A-Za-z0-9_]", "_");
        var name = $"RegNeps_Validation_2J_{safeLabel}_{Guid.NewGuid().ToString("N")[..12]}";

        var builder = new SqlConnectionStringBuilder(server) { InitialCatalog = name };
        var master = new SqlConnectionStringBuilder(server) { InitialCatalog = "master" };
        return new SqlServerValidationDatabase(name, builder.ConnectionString, master.ConnectionString);
    }

    public ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRegNepsInfrastructure(ConnectionString, useSqlServer: true);
        return services.BuildServiceProvider();
    }

    /// <summary>Arranque Web: <c>EnsureCreated → patches → seed → baseline → repair</c> con un proveedor nuevo.</summary>
    public async Task BootstrapAsync()
    {
        await using var sp = BuildServices();
        await sp.EnsureDatabaseCreatedAsync();
    }

    /// <summary>Bootstrap + proveedor de larga vida con la matriz de permisos cargada (como Program.cs).</summary>
    public async Task StartAsync()
    {
        await BootstrapAsync();
        _services = BuildServices();
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPermissionService>().EnsureLoadedAsync();
    }

    public T Get<T>() where T : notnull
    {
        var scope = Services.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    public NepRecordService Records() => Get<NepRecordService>();

    public SyncAppService Sync() => Get<SyncAppService>();

    public IDbContextFactory<RegNepsDbContext> ContextFactory =>
        Services.GetRequiredService<IDbContextFactory<RegNepsDbContext>>();

    public async Task<RecordActor> CreateActorAsync(string username, AppUserRole role)
    {
        var isSuper = role == AppUserRole.SuperAdmin;
        var roleCode = SystemRoleCodes.FromEnum(role);
        var user = new AppUser
        {
            Username = $"{username}_{Guid.NewGuid().ToString("N")[..8]}",
            DisplayName = username,
            PasswordHash = "validation-only",
            Role = role,
            RoleCode = roleCode,
            IsSuperAdmin = isSuper,
            IsActive = true
        };

        await using (var db = await ContextFactory.CreateDbContextAsync())
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        var permissions = Get<IPermissionService>();
        await permissions.EnsureLoadedAsync();
        var effective = isSuper ? SystemRoleCodes.SuperAdmin : roleCode;
        return RecordActor.Create(
            user.Id.ToString(),
            user.Username,
            user.DisplayName,
            role,
            isSuper,
            externalUserId: null,
            roleCode,
            permissions.SeesAllRecords(effective, isSuper));
    }

    public async Task<bool> DatabaseExistsAsync()
    {
        await using var conn = new SqlConnection(ServerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT DB_ID(@n)", conn);
        cmd.Parameters.AddWithValue("@n", Name);
        var result = await cmd.ExecuteScalarAsync();
        return result is not null and not DBNull;
    }

    public async Task<int> ExecuteAsync(string sql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T));
    }

    public async Task<List<string>> StringsAsync(string sql)
    {
        var rows = new List<string>();
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.IsDBNull(0) ? string.Empty : Convert.ToString(reader.GetValue(0))!);
        }

        return rows;
    }

    /// <summary>Firma física del esquema (columnas + índices) para comparar arranques/upgrade.</summary>
    public Task<List<string>> ColumnSignatureAsync() =>
        StringsAsync(
            """
            SELECT t.name COLLATE DATABASE_DEFAULT + '.' + c.name COLLATE DATABASE_DEFAULT + ':'
                   + ty.name COLLATE DATABASE_DEFAULT + '(' + CAST(c.max_length AS varchar(10)) + ')'
                   + CASE c.is_nullable WHEN 1 THEN ':NULL' ELSE ':NOTNULL' END
                   + CASE c.is_identity WHEN 1 THEN ':IDENTITY' ELSE '' END
            FROM sys.columns c
            JOIN sys.tables t ON c.object_id = t.object_id
            JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            ORDER BY t.name, c.name
            """);

    public Task<List<string>> IndexSignatureAsync() =>
        StringsAsync(
            """
            SELECT t.name COLLATE DATABASE_DEFAULT + '.' + i.name COLLATE DATABASE_DEFAULT + ':'
                   + CASE i.is_unique WHEN 1 THEN 'U' ELSE 'N' END
                   + ':' + i.type_desc COLLATE DATABASE_DEFAULT + ':'
                   + ISNULL(i.filter_definition COLLATE DATABASE_DEFAULT, '') + ':'
                   + (SELECT STRING_AGG(c.name COLLATE DATABASE_DEFAULT, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                      FROM sys.index_columns ic
                      JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                      WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0)
            FROM sys.indexes i
            JOIN sys.tables t ON i.object_id = t.object_id
            WHERE i.name IS NOT NULL
            ORDER BY t.name, i.name
            """);

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (Environment.GetEnvironmentVariable(SqlServerValidationEnv.KeepVariable) == "1")
        {
            return;
        }

        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(ServerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $"""
            IF DB_ID(N'{Name}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{Name}];
            END
            """,
            conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
