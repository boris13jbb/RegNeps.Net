using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RegNeps.OfflineStore;

/// <summary>Factory para <c>dotnet ef migrations</c>.</summary>
public sealed class LocalSyncDbContextFactory : IDesignTimeDbContextFactory<LocalSyncDbContext>
{
    public LocalSyncDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LocalSyncDbContext>()
            .UseSqlite("Data Source=regneps_local_design.db")
            .Options;
        return new LocalSyncDbContext(options);
    }
}
