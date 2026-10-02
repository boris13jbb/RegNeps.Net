using System.Collections.Immutable;
using RegNeps.Domain.Permissions;

namespace RegNeps.Application.Permissions;

public sealed record RoleMatrixEntry(
    Guid Id,
    string Code,
    string Name,
    bool IsActive,
    bool IsSystem,
    bool SeesAllRecords,
    ImmutableHashSet<AppPermission> Permissions);
