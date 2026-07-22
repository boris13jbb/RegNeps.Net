using Microsoft.EntityFrameworkCore;
using RegNeps.Domain.Entities;

namespace RegNeps.Infrastructure.Persistence;

public sealed class RegNepsDbContext : DbContext
{
    public RegNepsDbContext(DbContextOptions<RegNepsDbContext> options) : base(options)
    {
    }

    public DbSet<NepRecord> NepRecords => Set<NepRecord>();
    public DbSet<CorrectiveActionEntry> CorrectiveActions => Set<CorrectiveActionEntry>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Fabric> Fabrics => Set<Fabric>();
    public DbSet<AlertConfig> AlertConfigs => Set<AlertConfig>();
    public DbSet<SavedReport> SavedReports => Set<SavedReport>();
    public DbSet<LoteTramaItem> LoteTramaItems => Set<LoteTramaItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NepRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Telar).HasMaxLength(64).IsRequired();
            e.Property(x => x.Tela).HasMaxLength(128);
            e.Property(x => x.LoteTrama).HasMaxLength(64);
            e.Property(x => x.Turno).HasMaxLength(32);
            e.Property(x => x.Operario).HasMaxLength(128);
            e.Property(x => x.LineaProduccion).HasMaxLength(64);
            e.Property(x => x.Observacion).HasMaxLength(2000);
            e.Property(x => x.AccionCorrectiva).HasMaxLength(2000);
            e.Property(x => x.ResponsableRevision).HasMaxLength(128);
            e.Property(x => x.CreatedByUserId).HasMaxLength(64);
            e.Property(x => x.CreatedByEmail).HasMaxLength(256);
            e.Property(x => x.CreatedByRole).HasMaxLength(64);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.Telar);
            e.HasMany(x => x.HistorialAcciones)
                .WithOne(x => x.NepRecord!)
                .HasForeignKey(x => x.NepRecordId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.MtsCalculados);
        });

        modelBuilder.Entity<CorrectiveActionEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Accion).HasMaxLength(2000).IsRequired();
            e.Property(x => x.Responsable).HasMaxLength(128);
        });

        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Username).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(128);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.PasswordHash).HasMaxLength(512);
            e.Property(x => x.ExternalUserId).HasMaxLength(128);
            e.HasIndex(x => x.ExternalUserId);
            e.Property(x => x.DeletedAt);
        });

        modelBuilder.Entity<Fabric>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Code).HasMaxLength(64);
            e.HasIndex(x => x.Name);
        });

        modelBuilder.Entity<AlertConfig>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.LimiteNormalMax);
            e.Property(x => x.LimiteAdvertenciaMax);
            e.Property(x => x.CantidadReincidenciasCriticas);
            e.Property(x => x.DiasParaReincidencia);
            e.Property(x => x.AlertasActivas);
            e.Ignore(x => x.LimiteCriticoMin);
            e.Ignore(x => x.VentanaReincidenciasHoras);
        });

        modelBuilder.Entity<SavedReport>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.CreatedByUserId).HasMaxLength(64);
            e.Property(x => x.CreatedByName).HasMaxLength(128);
            e.Property(x => x.FiltersJson).HasMaxLength(8000);
            e.Property(x => x.SummaryText).HasMaxLength(2000);
            e.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<LoteTramaItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
        });
    }
}
