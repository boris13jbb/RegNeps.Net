using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RegNeps.Infrastructure.Persistence.Migrations;

/// <summary>
/// Migración formal FASE 2B — SyncChangeLogs.
/// El despliegue actual sigue usando EnsureCreated + DatabaseInitializer (parches aditivos idempotentes).
/// Este archivo documenta el esquema canónico; no se aplica con Migrate() automático en 2B
/// (conflicto de nombre con el namespace histórico <c>RegNeps.Infrastructure.Migration</c>).
/// </summary>
public partial class AddSyncChangeLog : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SyncChangeLogs",
            columns: table => new
            {
                Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                EntityType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                ChangeType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                ActorUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ClientOperationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                DeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SyncChangeLogs", x => x.Sequence);
            });

        migrationBuilder.CreateIndex(
            name: "IX_SyncChangeLogs_Sequence",
            table: "SyncChangeLogs",
            column: "Sequence");

        migrationBuilder.CreateIndex(
            name: "IX_SyncChangeLogs_EntityType_EntityId",
            table: "SyncChangeLogs",
            columns: new[] { "EntityType", "EntityId" });

        migrationBuilder.CreateIndex(
            name: "IX_SyncChangeLogs_Owner_Sequence",
            table: "SyncChangeLogs",
            columns: new[] { "OwnerUserId", "Sequence" });

        migrationBuilder.CreateIndex(
            name: "IX_SyncChangeLogs_ClientOperationId",
            table: "SyncChangeLogs",
            column: "ClientOperationId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "SyncChangeLogs");
    }
}
