using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RegNeps.OfflineStore;

#nullable disable

namespace RegNeps.OfflineStore.Migrations;

[DbContext(typeof(LocalSyncDbContext))]
partial class LocalSyncDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.HasAnnotation("ProductVersion", "8.0.17");

        modelBuilder.Entity("RegNeps.OfflineStore.Entities.LocalCatalogItem", b =>
        {
            b.Property<Guid>("Id").HasColumnType("TEXT");
            b.Property<string>("Code").IsRequired().HasMaxLength(64).HasColumnType("TEXT");
            b.Property<bool>("IsActive").HasColumnType("INTEGER");
            b.Property<int>("Kind").HasColumnType("INTEGER");
            b.Property<string>("Name").IsRequired().HasMaxLength(256).HasColumnType("TEXT");
            b.Property<DateTime>("UpdatedAtUtc").HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("Kind", "Code").IsUnique();
            b.ToTable("LocalCatalogItems");
        });

        modelBuilder.Entity("RegNeps.OfflineStore.Entities.LocalNepRecord", b =>
        {
            b.Property<Guid>("Id").HasColumnType("TEXT");
            b.Property<string>("CaptureSessionId").HasMaxLength(64).HasColumnType("TEXT");
            b.Property<string>("ClientOperationId").IsRequired().HasMaxLength(64).HasColumnType("TEXT");
            b.Property<DateTime>("CreatedAtUtc").HasColumnType("TEXT");
            b.Property<string>("LineaProduccion").IsRequired().HasMaxLength(64).HasColumnType("TEXT");
            b.Property<string>("LoteTrama").IsRequired().HasMaxLength(64).HasColumnType("TEXT");
            b.Property<double>("Neps").HasColumnType("REAL");
            b.Property<string>("Observacion").IsRequired().HasMaxLength(1000).HasColumnType("TEXT");
            b.Property<string>("Operario").IsRequired().HasMaxLength(128).HasColumnType("TEXT");
            b.Property<Guid?>("ServerRecordId").HasColumnType("TEXT");
            b.Property<int>("SyncStatus").HasColumnType("INTEGER");
            b.Property<string>("Tela").IsRequired().HasMaxLength(128).HasColumnType("TEXT");
            b.Property<string>("Telar").IsRequired().HasMaxLength(64).HasColumnType("TEXT");
            b.Property<string>("Turno").IsRequired().HasMaxLength(16).HasColumnType("TEXT");
            b.Property<string>("UserId").IsRequired().HasMaxLength(64).HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("ClientOperationId").IsUnique();
            b.HasIndex("SyncStatus");
            b.HasIndex("UserId", "CreatedAtUtc");
            b.ToTable("LocalNepRecords");
        });

        modelBuilder.Entity("RegNeps.OfflineStore.Entities.LocalSession", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<DateTime>("CapturedAtUtc").HasColumnType("TEXT");
            b.Property<DateTime>("ExpiresAtUtc").HasColumnType("TEXT");
            b.Property<bool>("HasSecureAuthMaterial").HasColumnType("INTEGER");
            b.Property<string>("PermissionsCsv").IsRequired().HasMaxLength(2000).HasColumnType("TEXT");
            b.Property<string>("RoleCode").IsRequired().HasMaxLength(64).HasColumnType("TEXT");
            b.Property<string>("ServerBaseUrl").HasMaxLength(512).HasColumnType("TEXT");
            b.Property<string>("UserId").IsRequired().HasMaxLength(64).HasColumnType("TEXT");
            b.Property<string>("Username").IsRequired().HasMaxLength(256).HasColumnType("TEXT");
            b.HasKey("Id");
            b.ToTable("LocalSessions");
        });

        modelBuilder.Entity("RegNeps.OfflineStore.Entities.PendingOperation", b =>
        {
            b.Property<Guid>("Id").HasColumnType("TEXT");
            b.Property<int>("AttemptCount").HasColumnType("INTEGER");
            b.Property<string>("CaptureSessionId").HasMaxLength(64).HasColumnType("TEXT");
            b.Property<string>("ClientOperationId").IsRequired().HasMaxLength(64).HasColumnType("TEXT");
            b.Property<DateTime>("CreatedAtUtc").HasColumnType("TEXT");
            b.Property<string>("DeviceId").IsRequired().HasMaxLength(64).HasColumnType("TEXT");
            b.Property<string>("ExpectedConcurrencyStamp").HasMaxLength(64).HasColumnType("TEXT");
            b.Property<DateTime?>("LastAttemptAtUtc").HasColumnType("TEXT");
            b.Property<string>("LastError").HasMaxLength(2000).HasColumnType("TEXT");
            b.Property<Guid?>("LocalNepRecordId").HasColumnType("TEXT");
            b.Property<int>("OperationType").HasColumnType("INTEGER");
            b.Property<string>("PayloadJson").IsRequired().HasColumnType("TEXT");
            b.Property<int>("ProtocolVersion").HasColumnType("INTEGER");
            b.Property<int>("Status").HasColumnType("INTEGER");
            b.Property<Guid?>("TargetServerRecordId").HasColumnType("TEXT");
            b.Property<string>("UserId").IsRequired().HasMaxLength(64).HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("ClientOperationId").IsUnique();
            b.HasIndex("LocalNepRecordId").IsUnique();
            b.HasIndex("Status", "CreatedAtUtc");
            b.ToTable("PendingOperations");
        });

        modelBuilder.Entity("RegNeps.OfflineStore.Entities.SyncState", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<string>("DeviceId").IsRequired().HasMaxLength(64).HasColumnType("TEXT");
            b.Property<string>("LastConnectivityStatus").HasMaxLength(64).HasColumnType("TEXT");
            b.Property<string>("LastError").HasMaxLength(2000).HasColumnType("TEXT");
            b.Property<long>("LastPulledSequence").HasColumnType("INTEGER");
            b.Property<DateTime?>("LastSuccessfulSyncUtc").HasColumnType("TEXT");
            b.Property<DateTime?>("UpdatedAtUtc").HasColumnType("TEXT");
            b.HasKey("Id");
            b.ToTable("SyncStates");
        });

        modelBuilder.Entity("RegNeps.OfflineStore.Entities.PendingOperation", b =>
        {
            b.HasOne("RegNeps.OfflineStore.Entities.LocalNepRecord", "LocalNepRecord")
                .WithOne("PendingOperation")
                .HasForeignKey("RegNeps.OfflineStore.Entities.PendingOperation", "LocalNepRecordId")
                .OnDelete(DeleteBehavior.Cascade);
            b.Navigation("LocalNepRecord");
        });

        modelBuilder.Entity("RegNeps.OfflineStore.Entities.LocalNepRecord", b =>
        {
            b.Navigation("PendingOperation");
        });
#pragma warning restore 612, 618
    }
}
