using Microsoft.EntityFrameworkCore;
using RegNeps.OfflineStore.Entities;
using RegNeps.OfflineStore.Enums;

namespace RegNeps.OfflineStore;

public sealed class LocalSyncDbContext : DbContext
{
    public LocalSyncDbContext(DbContextOptions<LocalSyncDbContext> options)
        : base(options)
    {
    }

    public DbSet<PendingOperation> PendingOperations => Set<PendingOperation>();
    public DbSet<LocalNepRecord> LocalNepRecords => Set<LocalNepRecord>();
    public DbSet<LocalCatalogItem> LocalCatalogItems => Set<LocalCatalogItem>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();
    public DbSet<LocalSession> LocalSessions => Set<LocalSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var pending = modelBuilder.Entity<PendingOperation>();
        pending.HasKey(x => x.Id);
        pending.Property(x => x.ClientOperationId).HasMaxLength(64).IsRequired();
        pending.HasIndex(x => x.ClientOperationId).IsUnique();
        pending.Property(x => x.PayloadJson).IsRequired();
        pending.Property(x => x.UserId).HasMaxLength(64).IsRequired();
        pending.Property(x => x.DeviceId).HasMaxLength(64).IsRequired();
        pending.Property(x => x.CaptureSessionId).HasMaxLength(64);
        pending.Property(x => x.LastError).HasMaxLength(2000);
        pending.Property(x => x.ExpectedConcurrencyStamp).HasMaxLength(64);
        pending.Property(x => x.LastServerErrorCode).HasMaxLength(64);
        pending.Property(x => x.ConflictServerConcurrencyStamp).HasMaxLength(64);
        pending.Property(x => x.ConflictServerSnapshotJson);
        pending.Property(x => x.OperationType).HasConversion<int>();
        pending.Property(x => x.Status).HasConversion<int>();
        pending.HasIndex(x => new { x.Status, x.CreatedAtUtc });
        pending.HasIndex(x => new { x.UserId, x.Status, x.CreatedAtUtc });
        pending.HasOne(x => x.LocalNepRecord)
            .WithOne(x => x.PendingOperation)
            .HasForeignKey<PendingOperation>(x => x.LocalNepRecordId)
            .OnDelete(DeleteBehavior.Cascade);

        var record = modelBuilder.Entity<LocalNepRecord>();
        record.HasKey(x => x.Id);
        record.Property(x => x.ClientOperationId).HasMaxLength(64).IsRequired();
        record.HasIndex(x => x.ClientOperationId).IsUnique();
        record.Property(x => x.CaptureSessionId).HasMaxLength(64);
        record.Property(x => x.Telar).HasMaxLength(64).IsRequired();
        record.Property(x => x.Tela).HasMaxLength(128);
        record.Property(x => x.LoteTrama).HasMaxLength(64);
        record.Property(x => x.Turno).HasMaxLength(16);
        record.Property(x => x.Operario).HasMaxLength(128);
        record.Property(x => x.LineaProduccion).HasMaxLength(64);
        record.Property(x => x.Observacion).HasMaxLength(1000);
        record.Property(x => x.UserId).HasMaxLength(64).IsRequired();
        record.Property(x => x.ConcurrencyStamp).HasMaxLength(64);
        record.Property(x => x.AccionCorrectiva).HasMaxLength(500);
        record.Property(x => x.ResponsableRevision).HasMaxLength(128);
        record.Property(x => x.SyncStatus).HasConversion<int>();
        record.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
        record.HasIndex(x => x.SyncStatus);
        record.HasIndex(x => x.ServerRecordId);

        var catalog = modelBuilder.Entity<LocalCatalogItem>();
        catalog.HasKey(x => x.Id);
        catalog.Property(x => x.Code).HasMaxLength(64).IsRequired();
        catalog.Property(x => x.Name).HasMaxLength(256);
        catalog.Property(x => x.Kind).HasConversion<int>();
        catalog.HasIndex(x => new { x.Kind, x.Code }).IsUnique();

        var sync = modelBuilder.Entity<SyncState>();
        sync.HasKey(x => x.Id);
        sync.Property(x => x.DeviceId).HasMaxLength(64);
        sync.Property(x => x.LastError).HasMaxLength(2000);
        sync.Property(x => x.LastConnectivityStatus).HasMaxLength(64);

        var session = modelBuilder.Entity<LocalSession>();
        session.HasKey(x => x.Id);
        session.Property(x => x.UserId).HasMaxLength(64).IsRequired();
        session.Property(x => x.Username).HasMaxLength(256);
        session.Property(x => x.RoleCode).HasMaxLength(64);
        session.Property(x => x.PermissionsCsv).HasMaxLength(2000);
        session.Property(x => x.ServerBaseUrl).HasMaxLength(512);
        // Garantía explícita: no hay columnas de password/token en el modelo.
    }
}
