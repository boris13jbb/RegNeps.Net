using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RegNeps.OfflineStore.Migrations;

/// <summary>FASE 2D.2: campos de réplica/conflicto para SyncEngine (aditivo).</summary>
public partial class AddSyncEngineFields : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ConcurrencyStamp",
            table: "LocalNepRecords",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "UpdatedAtUtc",
            table: "LocalNepRecords",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IsDeleted",
            table: "LocalNepRecords",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "AccionCorrectiva",
            table: "LocalNepRecords",
            type: "TEXT",
            maxLength: 500,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "ResponsableRevision",
            table: "LocalNepRecords",
            type: "TEXT",
            maxLength: 128,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<bool>(
            name: "RevisadoPorSupervisor",
            table: "LocalNepRecords",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTime>(
            name: "FechaRevisionUtc",
            table: "LocalNepRecords",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_LocalNepRecords_ServerRecordId",
            table: "LocalNepRecords",
            column: "ServerRecordId");

        migrationBuilder.AddColumn<string>(
            name: "LastServerErrorCode",
            table: "PendingOperations",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ConflictServerConcurrencyStamp",
            table: "PendingOperations",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ConflictServerSnapshotJson",
            table: "PendingOperations",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_PendingOperations_UserId_Status_CreatedAtUtc",
            table: "PendingOperations",
            columns: new[] { "UserId", "Status", "CreatedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PendingOperations_UserId_Status_CreatedAtUtc",
            table: "PendingOperations");

        migrationBuilder.DropColumn(name: "LastServerErrorCode", table: "PendingOperations");
        migrationBuilder.DropColumn(name: "ConflictServerConcurrencyStamp", table: "PendingOperations");
        migrationBuilder.DropColumn(name: "ConflictServerSnapshotJson", table: "PendingOperations");

        migrationBuilder.DropIndex(
            name: "IX_LocalNepRecords_ServerRecordId",
            table: "LocalNepRecords");

        migrationBuilder.DropColumn(name: "ConcurrencyStamp", table: "LocalNepRecords");
        migrationBuilder.DropColumn(name: "UpdatedAtUtc", table: "LocalNepRecords");
        migrationBuilder.DropColumn(name: "IsDeleted", table: "LocalNepRecords");
        migrationBuilder.DropColumn(name: "AccionCorrectiva", table: "LocalNepRecords");
        migrationBuilder.DropColumn(name: "ResponsableRevision", table: "LocalNepRecords");
        migrationBuilder.DropColumn(name: "RevisadoPorSupervisor", table: "LocalNepRecords");
        migrationBuilder.DropColumn(name: "FechaRevisionUtc", table: "LocalNepRecords");
    }
}
