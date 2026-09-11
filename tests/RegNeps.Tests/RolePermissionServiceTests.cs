using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Abstractions;
using RegNeps.Application.Permissions;
using RegNeps.Application.Records;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;

namespace RegNeps.Tests;

public class RolePermissionServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RegNepsDbContext> _options;

    public RolePermissionServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<RegNepsDbContext>()
            .UseSqlite(_connection)
            .Options;
    }

    public async Task InitializeAsync()
    {
        await using var db = new RegNepsDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DbSeeder.SeedAsync(db);
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SuperAdmin_Always_Has_Every_Permission()
    {
        await using (var db = new RegNepsDbContext(_options))
        {
            var tampered = await db.RolePermissions.FirstAsync(x =>
                x.Role == AppUserRole.SuperAdmin && x.Permission == AppPermission.ManageRoles);
            tampered.IsEnabled = false;
            await db.SaveChangesAsync();
        }

        var permissions = CreateService();
        await permissions.EnsureLoadedAsync();

        foreach (var definition in PermissionCatalog.All)
        {
            Assert.True(permissions.HasPermission(AppUserRole.Admin, true, true, definition.Permission));
            Assert.True(await permissions.HasPermissionAsync(
                AppUserRole.SuperAdmin, false, true, definition.Permission));
        }
    }

    [Fact]
    public async Task SuperAdmin_Cannot_Lose_ManageRoles()
    {
        var permissions = CreateService();
        var actor = await SuperActorAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            permissions.UpdateRolePermissionsAsync(
                AppUserRole.SuperAdmin,
                new Dictionary<AppPermission, bool> { [AppPermission.ManageRoles] = false },
                actor));

        Assert.Contains("no pueden modificarse", error.Message);
        Assert.True(permissions.HasPermission(AppUserRole.SuperAdmin, true, true, AppPermission.ManageRoles));
    }

    [Fact]
    public async Task Supervisor_Gains_And_Loses_A_Granted_Permission()
    {
        var permissions = CreateService();
        var actor = await SuperActorAsync();
        Assert.False(await permissions.HasPermissionAsync(
            AppUserRole.Supervisor, false, true, AppPermission.CaptureRecords));

        await permissions.UpdateRolePermissionAsync(
            AppUserRole.Supervisor, AppPermission.CaptureRecords, true, actor);

        Assert.True(permissions.HasPermission(AppUserRole.Supervisor, false, true, AppPermission.CaptureRecords));

        await permissions.UpdateRolePermissionAsync(
            AppUserRole.Supervisor, AppPermission.CaptureRecords, false, actor);

        Assert.False(permissions.HasPermission(AppUserRole.Supervisor, false, true, AppPermission.CaptureRecords));
    }

    [Fact]
    public async Task Operario_Cannot_Administer_Roles()
    {
        var permissions = CreateService();
        await permissions.EnsureLoadedAsync();
        Assert.False(permissions.CanAdministerRoles(AppUserRole.Operario, false, true));
    }

    [Fact]
    public async Task Admin_Cannot_Modify_The_Permission_Matrix()
    {
        var permissions = CreateService();
        var admin = new CallerContext(Guid.NewGuid(), AppUserRole.Admin, false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            permissions.UpdateRolePermissionAsync(
                AppUserRole.Supervisor, AppPermission.CaptureRecords, true, admin));
    }

    [Fact]
    public async Task Permission_Changes_Survive_A_New_Service_Instance()
    {
        var first = CreateService();
        var actor = await SuperActorAsync();
        await first.UpdateRolePermissionAsync(
            AppUserRole.Gerencia, AppPermission.EditRecords, true, actor);

        var restarted = CreateService();
        Assert.True(await restarted.HasPermissionAsync(
            AppUserRole.Gerencia, false, true, AppPermission.EditRecords));
    }

    [Fact]
    public async Task Seeder_Does_Not_Duplicate_Role_Permissions()
    {
        var expected = PermissionCatalog.All.Count * 5;
        await using (var db = new RegNepsDbContext(_options))
        {
            Assert.Equal(expected, await db.RolePermissions.CountAsync());
            await DbSeeder.SeedAsync(db);
            Assert.Equal(expected, await db.RolePermissions.CountAsync());
        }
    }

    [Fact]
    public async Task Existing_Roles_Keep_The_Initial_Permission_Matrix()
    {
        var permissions = CreateService();
        var supervisor = await permissions.GetPermissionsForRoleAsync(AppUserRole.Supervisor);
        var expected = RolePermissions.ForRole(AppUserRole.Supervisor).ToHashSet();
        Assert.True(expected.SetEquals(supervisor));
        Assert.False(supervisor.Contains(AppPermission.ManageUsers));
        Assert.False(await permissions.HasPermissionAsync(AppUserRole.Admin, false, true, AppPermission.ManageUsers));
    }

    [Fact]
    public async Task Navigation_Permission_Check_Uses_The_Updated_Matrix()
    {
        var permissions = CreateService();
        var actor = await SuperActorAsync();
        Assert.False(permissions.HasPermission(AppUserRole.Operario, false, true, AppPermission.ViewDashboard));

        await permissions.UpdateRolePermissionAsync(
            AppUserRole.Operario, AppPermission.ViewDashboard, true, actor);

        Assert.True(permissions.HasPermission(AppUserRole.Operario, false, true, AppPermission.ViewDashboard));
    }

    [Fact]
    public async Task Revoked_Permission_Blocks_The_Record_Operation()
    {
        var actor = await SuperActorAsync();
        var factory = new TestDbFactory(_options);
        await using var db = factory.CreateDbContext();
        var permissions = new PermissionService(new RolePermissionRepository(db), new PermissionMatrix());
        await permissions.UpdateRolePermissionAsync(
            AppUserRole.Supervisor, AppPermission.CaptureRecords, true, actor);

        var service = new NepRecordService(new NepRecordRepository(factory), new AlertConfigRepository(factory.CreateDbContext()), permissions);
        var supervisor = RecordActor.Create(Guid.NewGuid().ToString(), "sup", "Supervisor", AppUserRole.Supervisor, false);
        var request = new CreateNepRecordRequest { Telar = "T1", Neps = 2, Tela = "A", Turno = "A", Operario = "op" };

        var saved = await service.CreateAsync(request, supervisor);
        Assert.NotEqual(Guid.Empty, saved.Id);

        await permissions.UpdateRolePermissionAsync(
            AppUserRole.Supervisor, AppPermission.CaptureRecords, false, actor);

        await Assert.ThrowsAsync<UnauthorizedRecordAccessException>(() =>
            service.CreateAsync(request, supervisor));
        Assert.Equal(1, await db.NepRecords.CountAsync());
    }

    [Fact]
    public async Task Protected_SuperAdmin_Role_Cannot_Be_Edited()
    {
        var permissions = CreateService();
        var actor = await SuperActorAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            permissions.UpdateRolePermissionsAsync(
                AppUserRole.SuperAdmin,
                PermissionCatalog.All.ToDictionary(x => x.Permission, _ => false),
                actor));
    }

    [Fact]
    public async Task Catalog_Covers_Every_Permission()
    {
        var catalog = PermissionCatalog.All.Select(x => x.Permission).ToHashSet();
        foreach (var permission in Enum.GetValues<AppPermission>())
        {
            Assert.Contains(permission, catalog);
        }
    }

    [Fact]
    public async Task Audit_Records_Who_Changed_A_Permission()
    {
        var permissions = CreateService();
        var actor = await SuperActorAsync();
        await permissions.UpdateRolePermissionAsync(
            AppUserRole.Supervisor, AppPermission.DeleteRecords, true, actor);

        await using var db = new RegNepsDbContext(_options);
        var audit = await db.RolePermissionAudits.SingleAsync(x =>
            x.Role == AppUserRole.Supervisor && x.Permission == AppPermission.DeleteRecords);
        Assert.False(audit.PreviousValue);
        Assert.True(audit.NewValue);
        Assert.Equal(actor.UserId, audit.ModifiedByUserId);
    }

    private sealed class TestDbFactory : IDbContextFactory<RegNepsDbContext>
    {
        private readonly DbContextOptions<RegNepsDbContext> _options;
        public TestDbFactory(DbContextOptions<RegNepsDbContext> options) => _options = options;
        public RegNepsDbContext CreateDbContext() => new(_options);
    }

    private PermissionService CreateService()
    {
        var db = new RegNepsDbContext(_options);
        return new PermissionService(new RolePermissionRepository(db), new PermissionMatrix());
    }

    private async Task<CallerContext> SuperActorAsync()
    {
        await using var db = new RegNepsDbContext(_options);
        var admin = await db.Users.SingleAsync(u => u.Username == "admin");
        return new CallerContext(admin.Id, AppUserRole.SuperAdmin, true);
    }
}
