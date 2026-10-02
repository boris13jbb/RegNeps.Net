using RegNeps.Application.Abstractions;
using RegNeps.Application.Alerts;
using RegNeps.Application.Permissions;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Permissions;
using Xunit;

namespace RegNeps.Tests;

public sealed class AlertCriticalPublisherTests
{
    [Fact]
    public async Task Publish_When_AlertasActivas_False_Does_Not_Notify()
    {
        var notifier = new CapturingNotifier();
        var publisher = CreatePublisher(
            alertasActivas: false,
            users: [ActiveUser("u1", "custom_qa", isSuper: false)],
            viewAlertsRoles: ["custom_qa"],
            seesAllRoles: ["custom_qa"],
            notifier);

        await publisher.PublishNewCriticalAsync(RecordOwnedBy("other"));

        Assert.Empty(notifier.RecipientBatches);
    }

    [Fact]
    public async Task Publish_CustomRole_With_ViewAlerts_Receives()
    {
        var user = ActiveUser("u-custom", "custom_qa", isSuper: false);
        var notifier = new CapturingNotifier();
        var publisher = CreatePublisher(
            alertasActivas: true,
            users: [user],
            viewAlertsRoles: ["custom_qa"],
            seesAllRoles: ["custom_qa"],
            notifier);

        await publisher.PublishNewCriticalAsync(RecordOwnedBy("anyone"));

        Assert.Single(notifier.RecipientBatches);
        Assert.Contains(user.Id.ToString(), notifier.RecipientBatches[0]);
    }

    [Fact]
    public async Task Publish_Without_ViewAlerts_Does_Not_Notify()
    {
        var user = ActiveUser("u-noalert", "custom_qa", isSuper: false);
        var notifier = new CapturingNotifier();
        var publisher = CreatePublisher(
            alertasActivas: true,
            users: [user],
            viewAlertsRoles: [],
            seesAllRoles: ["custom_qa"],
            notifier);

        await publisher.PublishNewCriticalAsync(RecordOwnedBy(user.Id.ToString()));

        Assert.Empty(notifier.RecipientBatches);
    }

    private static AlertCriticalPublisher CreatePublisher(
        bool alertasActivas,
        IReadOnlyList<AppUser> users,
        IReadOnlyCollection<string> viewAlertsRoles,
        IReadOnlyCollection<string> seesAllRoles,
        IAlertRealtimeNotifier notifier) =>
        new(
            new FakeUsers(users),
            new FakeAlertConfig(alertasActivas),
            new FakePermissions(viewAlertsRoles, seesAllRoles),
            notifier);

    private static AppUser ActiveUser(string name, string roleCode, bool isSuper) => new()
    {
        Id = Guid.NewGuid(),
        Username = name,
        DisplayName = name,
        RoleCode = roleCode,
        Role = AppUserRole.Operario,
        IsActive = true,
        IsSuperAdmin = isSuper
    };

    private static NepRecord RecordOwnedBy(string createdBy) => new()
    {
        Id = Guid.NewGuid(),
        Telar = "T1",
        Neps = 99,
        CreatedByUserId = createdBy,
        CreatedAt = DateTime.UtcNow
    };

    private sealed class CapturingNotifier : IAlertRealtimeNotifier
    {
        public List<IReadOnlyList<string>> RecipientBatches { get; } = [];

        public Task NotifyCriticalAlertAsync(
            IReadOnlyList<string> userIds,
            CriticalAlertPushMessage message,
            CancellationToken ct = default)
        {
            RecipientBatches.Add(userIds.ToList());
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUsers : IUserRepository
    {
        private readonly IReadOnlyList<AppUser> _users;
        public FakeUsers(IReadOnlyList<AppUser> users) => _users = users;
        public Task<IReadOnlyList<AppUser>> ListAsync(bool includeDeleted = false, CancellationToken ct = default) =>
            Task.FromResult(_users);
        public Task<AppUser?> FindByUsernameAsync(string username, CancellationToken ct = default) =>
            Task.FromResult<AppUser?>(null);
        public Task<AppUser?> FindByEmailAsync(string email, CancellationToken ct = default) =>
            Task.FromResult<AppUser?>(null);
        public Task<AppUser?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(_users.FirstOrDefault(u => u.Id == id));
        public Task<AppUser> AddAsync(AppUser user, CancellationToken ct = default) => Task.FromResult(user);
        public Task UpdateAsync(AppUser user, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CountSuperAdminsAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeAlertConfig : IAlertConfigRepository
    {
        private readonly AlertConfig _config;
        public FakeAlertConfig(bool alertasActivas) =>
            _config = new AlertConfig { Id = 1, AlertasActivas = alertasActivas };
        public Task<AlertConfig> GetAsync(CancellationToken ct = default) => Task.FromResult(_config);
        public Task SaveAsync(AlertConfig config, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakePermissions : IPermissionService
    {
        private readonly HashSet<string> _viewAlerts;
        private readonly HashSet<string> _seesAll;

        public FakePermissions(IReadOnlyCollection<string> viewAlertsRoles, IReadOnlyCollection<string> seesAllRoles)
        {
            _viewAlerts = new HashSet<string>(viewAlertsRoles, StringComparer.OrdinalIgnoreCase);
            _seesAll = new HashSet<string>(seesAllRoles, StringComparer.OrdinalIgnoreCase);
        }

        public bool HasPermission(AppUserRole role, bool isSuperAdmin, bool isActive, AppPermission permission) =>
            isSuperAdmin || (isActive && permission == AppPermission.ViewAlerts && role != AppUserRole.Operario);

        public bool HasPermissionByRoleCode(string? roleCode, bool isSuperAdmin, bool isActive, AppPermission permission)
        {
            if (isSuperAdmin)
            {
                return true;
            }

            if (!isActive || permission != AppPermission.ViewAlerts || string.IsNullOrWhiteSpace(roleCode))
            {
                return false;
            }

            return _viewAlerts.Contains(roleCode.Trim());
        }

        public bool CanAdministerRoles(AppUserRole role, bool isSuperAdmin, bool isActive) => isSuperAdmin;
        public bool SeesAllRecords(string? roleCode, bool isSuperAdmin) =>
            isSuperAdmin || (!string.IsNullOrWhiteSpace(roleCode) && _seesAll.Contains(roleCode.Trim()));
        public Task EnsureLoadedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> HasPermissionAsync(
            AppUserRole role, bool isSuperAdmin, bool isActive, AppPermission permission, CancellationToken ct = default) =>
            Task.FromResult(HasPermission(role, isSuperAdmin, isActive, permission));
        public Task<IReadOnlySet<AppPermission>> GetPermissionsForRoleAsync(AppUserRole role, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<AppPermission>>(new HashSet<AppPermission>());
        public Task<IReadOnlySet<AppPermission>> GetPermissionsForRoleIdAsync(Guid roleId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<AppPermission>>(new HashSet<AppPermission>());
        public Task<IReadOnlyList<AppRole>> GetManageableRolesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AppRole>>(Array.Empty<AppRole>());
        public Task<IReadOnlyDictionary<AppUserRole, IReadOnlySet<AppPermission>>> GetRolePermissionsAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<AppUserRole, IReadOnlySet<AppPermission>>>(
                new Dictionary<AppUserRole, IReadOnlySet<AppPermission>>());
        public Task UpdateRolePermissionAsync(
            AppUserRole role, AppPermission permission, bool isEnabled, CallerContext actor, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<int> UpdateRolePermissionsAsync(
            AppUserRole role, IReadOnlyDictionary<AppPermission, bool> desired, CallerContext actor, CancellationToken ct = default) =>
            Task.FromResult(0);
        public Task<int> UpdateRolePermissionsForRoleIdAsync(
            Guid roleId, IReadOnlyDictionary<AppPermission, bool> desired, CallerContext actor, CancellationToken ct = default) =>
            Task.FromResult(0);
        public Task RefreshMatrixAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
