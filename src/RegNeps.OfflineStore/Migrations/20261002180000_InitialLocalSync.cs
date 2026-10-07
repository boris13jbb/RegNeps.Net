using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RegNeps.OfflineStore.Migrations;

public partial class InitialLocalSync : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LocalCatalogItems",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Kind = table.Column<int>(type: "INTEGER", nullable: false),
                Code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_LocalCatalogItems", x => x.Id));

        migrationBuilder.CreateTable(
            name: "LocalNepRecords",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ClientOperationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                CaptureSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                Telar = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Neps = table.Column<double>(type: "REAL", nullable: false),
                Tela = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                LoteTrama = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Turno = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Operario = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                LineaProduccion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Observacion = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                SyncStatus = table.Column<int>(type: "INTEGER", nullable: false),
                ServerRecordId = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_LocalNepRecords", x => x.Id));

        migrationBuilder.CreateTable(
            name: "LocalSessions",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                RoleCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                PermissionsCsv = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                CapturedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                ServerBaseUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                HasSecureAuthMaterial = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_LocalSessions", x => x.Id));

        migrationBuilder.CreateTable(
            name: "SyncStates",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                LastSuccessfulSyncUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastPulledSequence = table.Column<long>(type: "INTEGER", nullable: false),
                LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                LastConnectivityStatus = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                DeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_SyncStates", x => x.Id));

        migrationBuilder.CreateTable(
            name: "PendingOperations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ClientOperationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                OperationType = table.Column<int>(type: "INTEGER", nullable: false),
                PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                ProtocolVersion = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                DeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                CaptureSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                LocalNepRecordId = table.Column<Guid>(type: "TEXT", nullable: true),
                TargetServerRecordId = table.Column<Guid>(type: "TEXT", nullable: true),
                ExpectedConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PendingOperations", x => x.Id);
                table.ForeignKey(
                    name: "FK_PendingOperations_LocalNepRecords_LocalNepRecordId",
                    column: x => x.LocalNepRecordId,
                    principalTable: "LocalNepRecords",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_LocalCatalogItems_Kind_Code",
            table: "LocalCatalogItems",
            columns: new[] { "Kind", "Code" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_LocalNepRecords_ClientOperationId",
            table: "LocalNepRecords",
            column: "ClientOperationId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_LocalNepRecords_SyncStatus",
            table: "LocalNepRecords",
            column: "SyncStatus");

        migrationBuilder.CreateIndex(
            name: "IX_LocalNepRecords_UserId_CreatedAtUtc",
            table: "LocalNepRecords",
            columns: new[] { "UserId", "CreatedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_PendingOperations_ClientOperationId",
            table: "PendingOperations",
            column: "ClientOperationId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PendingOperations_LocalNepRecordId",
            table: "PendingOperations",
            column: "LocalNepRecordId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PendingOperations_Status_CreatedAtUtc",
            table: "PendingOperations",
            columns: new[] { "Status", "CreatedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "LocalCatalogItems");
        migrationBuilder.DropTable(name: "LocalSessions");
        migrationBuilder.DropTable(name: "PendingOperations");
        migrationBuilder.DropTable(name: "SyncStates");
        migrationBuilder.DropTable(name: "LocalNepRecords");
    }
}
