using System.Collections.Concurrent;
using System.Data.SqlTypes;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using RegNeps.Application.Common;
using RegNeps.Application.Records;
using RegNeps.Application.Sync;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Filters;
using RegNeps.Domain.Services;
using RegNeps.Domain.Sync;
using RegNeps.Infrastructure.Persistence;
using RegNeps.Infrastructure.Repositories;
using Xunit.Abstractions;

namespace RegNeps.Tests.SqlServer;

/// <summary>
/// FASE 2J — escenarios funcionales de sync/registros ejecutados físicamente en SQL Server
/// (servicios con el wiring productivo de <c>AddRegNepsInfrastructure</c>). Cada prueba usa su propia BD.
/// </summary>
public sealed class SqlServerSync2JTests
{
    private const string FailPrefix = "2J-FAIL-";
    private readonly ITestOutputHelper _output;

    public SqlServerSync2JTests(ITestOutputHelper output) => _output = output;

    // ---------- Parte E: NEPS y calidad ----------

    [SqlServerFact]
    public async Task Neps_Boundaries_Quality_And_Formula_Persist_On_SqlServer()
    {
        await using var db = await StartAsync("neps");
        var actor = await db.CreateActorAsync("neps_admin", AppUserRole.Admin);
        var sync = db.Sync();

        (double Q, string Label)[] cases =
        [
            (18, "OK"), (19, "Mención"), (45, "Mención"),
            (46, "Crítico - Realizar Ajuste"), (54, "Crítico - Realizar Ajuste"), (55, "2da Calidad")
        ];

        // Q=0: OK en el evaluador, pero la captura exige Neps > 0 (regla existente, independiente del proveedor).
        Assert.Equal("OK", AlertEvaluator.GetLevel(0).ToDisplayLabel());
        var zero = Assert.Single((await sync.PushAsync(PushCreate(NewOp(), 0), actor, "zero")).Results);
        Assert.Equal(nameof(SyncOperationResult.Invalid), zero.Result);
        _output.WriteLine($"Q=0 → evaluador OK; Push Create {zero.Result} {zero.ErrorCode}: {zero.Message}");
        Assert.Equal(0, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM NepRecords WHERE CreatedByUserId = N'{actor.UserId}'"));

        await AssertQualityAsync(db, sync, actor, cases, "base");

        // AlertasActivas y límites legacy no participan en la calificación.
        await db.ExecuteAsync("UPDATE AlertConfigs SET AlertasActivas = 0, LimiteNormalMax = 1, LimiteAdvertenciaMax = 2");
        await AssertQualityAsync(db, db.Sync(), actor, cases, "alertas-off");
    }

    private async Task AssertQualityAsync(
        SqlServerValidationDatabase db,
        SyncAppService sync,
        RecordActor actor,
        (double Q, string Label)[] cases,
        string tag)
    {
        foreach (var (q, label) in cases)
        {
            var response = await sync.PushAsync(PushCreate($"{tag}-{q}-{Guid.NewGuid():N}", q), actor, "neps");
            var result = Assert.Single(response.Results);
            Assert.Equal(nameof(SyncOperationResult.Accepted), result.Result);
            Assert.Equal(label, result.QualityLabel);

            await using var ctx = await db.ContextFactory.CreateDbContextAsync();
            var stored = await ctx.NepRecords.AsNoTracking().SingleAsync(r => r.Id == result.EntityId);
            Assert.Equal(q, stored.Neps);
            Assert.Equal(label, AlertEvaluator.GetLevel(stored.Neps).ToDisplayLabel());
            Assert.Equal(q / NepsConstants.TestLengthM, stored.MtsCalculados, 9);

            var log = await ctx.SyncChangeLogs.AsNoTracking().SingleAsync(c => c.Sequence == result.ChangeSequence);
            using var payload = JsonDocument.Parse(log.PayloadJson);
            Assert.Equal(label, payload.RootElement.GetProperty("qualityLabel").GetString());
            Assert.Equal(q / 0.09, payload.RootElement.GetProperty("mtsCalculados").GetDouble(), 9);
            _output.WriteLine($"[{tag}] Q={q} → {label}; NEPS/m={stored.MtsCalculados:F4}");
        }
    }

    // ---------- Parte F: Create online + ChangeLog ----------

    [SqlServerFact]
    public async Task Online_Create_Writes_Record_And_ChangeLog_Together()
    {
        await using var db = await StartAsync("create");
        var actor = await db.CreateActorAsync("create_op", AppUserRole.Operario);
        var opId = "online-" + Guid.NewGuid().ToString("N");

        var created = await db.Records().CreateAsync(new CreateNepRecordRequest
        {
            Telar = "ON-1",
            Neps = 20,
            Tela = "Denim",
            LoteTrama = "L2J",
            ClientOperationId = opId,
            CaptureSessionId = "sess-2j"
        }, actor);

        Assert.Equal(1, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM NepRecords WHERE Id = '{created.Id}'"));
        var log = await db.StringsAsync(
            $"""
            SELECT EntityType + '|' + ChangeType + '|' + ActorUserId + '|' + OwnerUserId + '|' + ISNULL(ClientOperationId, '')
            FROM SyncChangeLogs WHERE EntityId = '{created.Id}'
            """);
        Assert.Equal(
            $"{SyncConstants.EntityNepRecord}|{SyncConstants.ChangeRecordUpserted}|{actor.UserId}|{actor.UserId}|{opId}",
            Assert.Single(log));
        Assert.Equal("sess-2j", (await db.StringsAsync($"SELECT CaptureSessionId FROM NepRecords WHERE Id = '{created.Id}'")).Single());
        await AssertNoOrphansAsync(db);
    }

    [SqlServerFact]
    public async Task Create_With_Failing_ChangeLog_Rolls_Back_Record_Online_And_Push()
    {
        await using var db = await StartAsync("create_fail");
        var actor = await db.CreateActorAsync("fail_admin", AppUserRole.Admin);
        await ArmChangeLogRejectionAsync(db);

        var recordsBefore = await db.ScalarAsync<int>("SELECT COUNT(*) FROM NepRecords");
        var logsBefore = await db.ScalarAsync<int>("SELECT COUNT(*) FROM SyncChangeLogs");

        var onlineOp = FailPrefix + Guid.NewGuid().ToString("N");
        var onlineError = await Assert.ThrowsAnyAsync<Exception>(() => db.Records().CreateAsync(new CreateNepRecordRequest
        {
            Telar = "FAIL-ON",
            Neps = 10,
            ClientOperationId = onlineOp
        }, actor));
        _output.WriteLine("Online create con ChangeLog rechazado: " + onlineError.GetType().Name);

        var pushOp = FailPrefix + Guid.NewGuid().ToString("N");
        SyncOperationResultDto? pushResult = null;
        Exception? pushError = null;
        try
        {
            pushResult = Assert.Single((await db.Sync().PushAsync(PushCreate(pushOp, 10), actor, "fail")).Results);
        }
        catch (Exception ex)
        {
            pushError = ex;
        }

        _output.WriteLine($"Push create con ChangeLog rechazado: {pushResult?.Result ?? pushError?.GetType().Name} {pushResult?.ErrorCode}");
        Assert.NotEqual(nameof(SyncOperationResult.Accepted), pushResult?.Result);

        Assert.Equal(recordsBefore, await db.ScalarAsync<int>("SELECT COUNT(*) FROM NepRecords"));
        Assert.Equal(logsBefore, await db.ScalarAsync<int>("SELECT COUNT(*) FROM SyncChangeLogs"));
        Assert.Equal(0, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM NepRecords WHERE ClientOperationId LIKE N'{FailPrefix}%'"));
        await AssertNoOrphansAsync(db);

        await DisarmChangeLogRejectionAsync(db);
        var ok = Assert.Single((await db.Sync().PushAsync(PushCreate(pushOp, 10), actor, "retry")).Results);
        Assert.Equal(nameof(SyncOperationResult.Accepted), ok.Result);
        await AssertNoOrphansAsync(db);
    }

    // ---------- Parte G: Idempotencia ----------

    [SqlServerFact]
    public async Task Push_Create_Same_ClientOperationId_Is_Duplicate_Without_New_Rows()
    {
        await using var db = await StartAsync("idem");
        var actor = await db.CreateActorAsync("idem_op", AppUserRole.Operario);
        var sync = db.Sync();
        var opId = "X-" + Guid.NewGuid().ToString("N");

        var first = Assert.Single((await sync.PushAsync(PushCreate(opId, 12), actor, "p1")).Results);
        Assert.Equal(nameof(SyncOperationResult.Accepted), first.Result);
        var records = await db.ScalarAsync<int>("SELECT COUNT(*) FROM NepRecords");
        var logs = await db.ScalarAsync<int>("SELECT COUNT(*) FROM SyncChangeLogs");

        var second = Assert.Single((await sync.PushAsync(PushCreate(opId, 12), actor, "p2")).Results);
        Assert.Equal(nameof(SyncOperationResult.Duplicate), second.Result);
        Assert.Equal(first.EntityId, second.EntityId);
        Assert.Equal(first.ChangeSequence, second.ChangeSequence);

        Assert.Equal(records, await db.ScalarAsync<int>("SELECT COUNT(*) FROM NepRecords"));
        Assert.Equal(logs, await db.ScalarAsync<int>("SELECT COUNT(*) FROM SyncChangeLogs"));
        Assert.Equal(1, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM NepRecords WHERE ClientOperationId = N'{opId}'"));

        // Garantía física: el índice único filtrado rechaza un segundo ChangeLog con el mismo (actor, op).
        var ex = await Assert.ThrowsAsync<SqlException>(() => db.ExecuteAsync(
            $$"""
            INSERT INTO SyncChangeLogs (EntityType, EntityId, ChangeType, OccurredAtUtc, ActorUserId, OwnerUserId, ClientOperationId, DeviceId, PayloadJson)
            VALUES (N'NepRecord', NEWID(), N'RecordUpserted', SYSUTCDATETIME(), N'{{actor.UserId}}', N'{{actor.UserId}}', N'{{opId}}', NULL, N'{}')
            """));
        Assert.Contains(ex.Number, new[] { 2601, 2627 });
    }

    // ---------- Parte H: Update + ConcurrencyStamp ----------

    [SqlServerFact]
    public async Task Update_Correct_Stamp_Accepted_Then_Old_Stamp_Conflict_Without_Mutation()
    {
        await using var db = await StartAsync("update");
        var actor = await db.CreateActorAsync("upd_admin", AppUserRole.Admin);
        var sync = db.Sync();
        var (id, stampX) = await CreateViaPushAsync(sync, actor, 12);

        var upd = Assert.Single((await sync.PushAsync(PushUpdate(NewOp(), id, stampX, 40, "T-UPD"), actor, "u1")).Results);
        Assert.Equal(nameof(SyncOperationResult.Accepted), upd.Result);
        var stampY = upd.ConcurrencyStamp!;
        Assert.NotEqual(stampX, stampY);

        await using (var ctx = await db.ContextFactory.CreateDbContextAsync())
        {
            var row = await ctx.NepRecords.AsNoTracking().SingleAsync(r => r.Id == id);
            Assert.Equal(stampY, row.ConcurrencyStamp);
            Assert.NotNull(row.UpdatedAt);
            Assert.Equal(40, row.Neps);
            var log = await ctx.SyncChangeLogs.AsNoTracking().SingleAsync(c => c.Sequence == upd.ChangeSequence);
            Assert.Equal(SyncConstants.ChangeRecordUpserted, log.ChangeType);
        }

        var logsBefore = await db.ScalarAsync<int>("SELECT COUNT(*) FROM SyncChangeLogs");
        var conflict = Assert.Single((await sync.PushAsync(PushUpdate(NewOp(), id, stampX, 999, "T-STALE"), actor, "u2")).Results);
        Assert.Equal(nameof(SyncOperationResult.Conflict), conflict.Result);
        Assert.Equal(stampY, conflict.ServerConcurrencyStamp);
        Assert.NotNull(conflict.ServerSnapshot);
        Assert.Equal(40, conflict.ServerSnapshot!.Value.GetProperty("neps").GetDouble());
        Assert.Equal(stampY, conflict.ServerSnapshot!.Value.GetProperty("concurrencyStamp").GetString());

        Assert.Equal(logsBefore, await db.ScalarAsync<int>("SELECT COUNT(*) FROM SyncChangeLogs"));
        Assert.Equal($"T-UPD|40|{stampY}", (await db.StringsAsync(
            $"SELECT Telar + '|' + CONVERT(nvarchar(20), Neps) + '|' + ConcurrencyStamp FROM NepRecords WHERE Id = '{id}'")).Single());
    }

    // ---------- Parte I: Delete + tombstone ----------

    [SqlServerFact]
    public async Task Delete_Writes_Tombstone_Atomically_Pull_Sees_It_And_Retry_Is_Idempotent()
    {
        await using var db = await StartAsync("delete");
        var actor = await db.CreateActorAsync("del_admin", AppUserRole.Admin);
        var sync = db.Sync();
        var (id, stamp) = await CreateViaPushAsync(sync, actor, 15);
        var cursorBefore = await MaxSequenceAsync(db) - 1;

        var opId = NewOp();
        var del = Assert.Single((await sync.PushAsync(PushDelete(opId, id, stamp), actor, "d1")).Results);
        Assert.Equal(nameof(SyncOperationResult.Accepted), del.Result);
        Assert.Equal(0, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM NepRecords WHERE Id = '{id}'"));
        Assert.Equal(1, await db.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM SyncChangeLogs WHERE EntityId = '{id}' AND ChangeType = N'{SyncConstants.ChangeRecordDeleted}'"));

        var retry = Assert.Single((await sync.PushAsync(PushDelete(opId, id, stamp), actor, "d2")).Results);
        Assert.Equal(nameof(SyncOperationResult.Duplicate), retry.Result);
        Assert.Equal(del.ChangeSequence, retry.ChangeSequence);
        Assert.Equal(1, await db.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM SyncChangeLogs WHERE EntityId = '{id}' AND ChangeType = N'{SyncConstants.ChangeRecordDeleted}'"));

        var pull = await sync.PullAsync(Pull(cursorBefore, 100), actor, "pull");
        var forEntity = pull.Changes.Where(c => c.EntityId == id).OrderBy(c => c.Sequence).ToList();
        Assert.Equal(
            new[] { SyncConstants.ChangeRecordUpserted, SyncConstants.ChangeRecordDeleted },
            forEntity.Select(c => c.ChangeType).ToArray());
        var tomb = forEntity[^1].Payload;
        Assert.Equal(id, tomb.GetProperty("id").GetGuid());
        Assert.Equal(stamp, tomb.GetProperty("lastConcurrencyStamp").GetString());

        // Operación tardía sobre el tombstone: no resucita.
        var lateUpdate = Assert.Single((await sync.PushAsync(PushUpdate(NewOp(), id, stamp, 30), actor, "late")).Results);
        Assert.Equal(nameof(SyncOperationResult.Invalid), lateUpdate.Result);
        Assert.Equal("ENTITY_DELETED", lateUpdate.ErrorCode);
        Assert.Equal(0, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM NepRecords WHERE Id = '{id}'"));
        await AssertNoOrphansAsync(db);
    }

    // ---------- Parte J: ApplyCorrective ----------

    [SqlServerFact]
    public async Task ApplyCorrective_Updates_Review_Fields_Only_And_Stale_Stamp_Conflicts()
    {
        await using var db = await StartAsync("corrective");
        var owner = await db.CreateActorAsync("corr_owner", AppUserRole.Operario);
        var admin = await db.CreateActorAsync("corr_admin", AppUserRole.Admin);
        var sync = db.Sync();

        var create = Assert.Single((await sync.PushAsync(new SyncPushRequest
        {
            ProtocolVersion = SyncProtocol.Version,
            DeviceId = "dev-2j",
            Operations =
            [
                new SyncOperationDto
                {
                    ClientOperationId = NewOp(),
                    OperationType = SyncConstants.OperationCreateRecord,
                    CaptureSessionId = "sess-corr",
                    Payload = JsonSerializer.SerializeToElement(new SyncCreateRecordPayload
                    {
                        Telar = "T-CORR", Neps = 50, Tela = "Denim", LoteTrama = "LCORR", Turno = "A",
                        Operario = "op-orig", LineaProduccion = "L1", Observacion = "obs-orig"
                    })
                }
            ]
        }, owner, "c")).Results);
        var id = create.EntityId!.Value;
        var stamp = create.ConcurrencyStamp!;
        var before = await RowFingerprintAsync(db, id);

        var corr = Assert.Single((await sync.PushAsync(PushCorrective(NewOp(), id, stamp, "Ajuste trama", "Sup 2J"), admin, "corr")).Results);
        Assert.Equal(nameof(SyncOperationResult.Accepted), corr.Result);
        Assert.Equal("Crítico - Realizar Ajuste", corr.QualityLabel);

        await using (var ctx = await db.ContextFactory.CreateDbContextAsync())
        {
            var row = await ctx.NepRecords.AsNoTracking().Include(r => r.HistorialAcciones).SingleAsync(r => r.Id == id);
            Assert.Equal("Ajuste trama", row.AccionCorrectiva);
            Assert.Equal("Sup 2J", row.ResponsableRevision);
            Assert.True(row.RevisadoPorSupervisor);
            Assert.NotNull(row.FechaRevision);
            Assert.NotNull(row.UpdatedAt);
            Assert.NotEqual(stamp, row.ConcurrencyStamp);
            Assert.Equal(corr.ConcurrencyStamp, row.ConcurrencyStamp);
            Assert.Single(row.HistorialAcciones);
            var log = await ctx.SyncChangeLogs.AsNoTracking().SingleAsync(c => c.Sequence == corr.ChangeSequence);
            Assert.Equal(SyncConstants.ChangeRecordUpserted, log.ChangeType);
            Assert.Equal(admin.UserId, log.ActorUserId);
            Assert.Equal(owner.UserId, log.OwnerUserId);
        }

        // Campos de captura, propietario y sesión intactos.
        Assert.Equal(before, await RowFingerprintAsync(db, id));

        var logsBefore = await db.ScalarAsync<int>("SELECT COUNT(*) FROM SyncChangeLogs");
        var stale = Assert.Single((await sync.PushAsync(PushCorrective(NewOp(), id, stamp, "Stale", "X"), admin, "stale")).Results);
        Assert.Equal(nameof(SyncOperationResult.Conflict), stale.Result);
        Assert.Equal(corr.ConcurrencyStamp, stale.ServerConcurrencyStamp);
        Assert.NotNull(stale.ServerSnapshot);
        Assert.Equal(logsBefore, await db.ScalarAsync<int>("SELECT COUNT(*) FROM SyncChangeLogs"));
        Assert.Equal(1, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM CorrectiveActions WHERE NepRecordId = '{id}'"));
        Assert.Equal("Ajuste trama", (await db.StringsAsync($"SELECT AccionCorrectiva FROM NepRecords WHERE Id = '{id}'")).Single());

        // Fallo del ChangeLog → ni correctiva ni stamp nuevo.
        await ArmChangeLogRejectionAsync(db);
        var currentStamp = corr.ConcurrencyStamp!;
        SyncOperationResultDto? failed = null;
        Exception? failedEx = null;
        try
        {
            failed = Assert.Single((await sync.PushAsync(
                PushCorrective(FailPrefix + Guid.NewGuid().ToString("N"), id, currentStamp, "No debe quedar", "X"), admin, "fail")).Results);
        }
        catch (Exception ex)
        {
            failedEx = ex;
        }

        _output.WriteLine($"ApplyCorrective con ChangeLog rechazado: {failed?.Result ?? failedEx?.GetType().Name} {failed?.ErrorCode}");
        Assert.NotEqual(nameof(SyncOperationResult.Accepted), failed?.Result);
        await DisarmChangeLogRejectionAsync(db);
        Assert.Equal(1, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM CorrectiveActions WHERE NepRecordId = '{id}'"));
        Assert.Equal($"Ajuste trama|{currentStamp}", (await db.StringsAsync(
            $"SELECT AccionCorrectiva + '|' + ConcurrencyStamp FROM NepRecords WHERE Id = '{id}'")).Single());
    }

    // ---------- Parte K: ClearAll con tombstones ----------

    [SqlServerFact]
    public async Task ClearAll_Emits_One_Tombstone_Per_Record_Pull_Paginates_And_New_Create_Survives()
    {
        await using var db = await StartAsync("clearall");
        var admin = await db.CreateActorAsync("clr_admin", AppUserRole.Admin);
        var op = await db.CreateActorAsync("clr_op", AppUserRole.Operario);
        var supervisor = await db.CreateActorAsync("clr_sup", AppUserRole.Supervisor);
        var records = db.Records();

        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var r = await records.CreateAsync(new CreateNepRecordRequest { Telar = $"A{i}", Neps = 10 + i, ClientOperationId = NewOp() }, admin);
            await records.ApplyCorrectiveAsync(new CorrectiveActionRequest { RecordId = r.Id, Accion = "Acc", Responsable = "R", MarcarRevisado = true }, admin);
            ids.Add(r.Id);
        }

        var opIds = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var r = await records.CreateAsync(new CreateNepRecordRequest { Telar = $"O{i}", Neps = 30, ClientOperationId = NewOp() }, op);
            opIds.Add(r.Id);
        }

        // Permisos: Operario y Supervisor (SeesAll sin ClearAllRecords) rechazados en el servicio.
        Assert.True(supervisor.SeesAllRecords);
        await Assert.ThrowsAsync<UnauthorizedRecordAccessException>(() => db.Records().ClearAllAsync(op));
        await Assert.ThrowsAsync<UnauthorizedRecordAccessException>(() => db.Records().ClearAllAsync(supervisor));
        Assert.Equal(7, await db.ScalarAsync<int>("SELECT COUNT(*) FROM NepRecords"));

        // Rollback: si un tombstone falla, no se borra nada ni quedan tombstones parciales.
        var tombstonesBefore = await CountTombstonesAsync(db);
        await db.ExecuteAsync(
            $"""
            ALTER TABLE SyncChangeLogs WITH NOCHECK ADD CONSTRAINT CK_2J_RejectClearAll
            CHECK (ChangeType <> N'{SyncConstants.ChangeRecordDeleted}' OR ClientOperationId IS NOT NULL)
            """);
        await Assert.ThrowsAnyAsync<Exception>(() => db.Records().ClearAllAsync(admin));
        await db.ExecuteAsync("ALTER TABLE SyncChangeLogs DROP CONSTRAINT CK_2J_RejectClearAll");
        Assert.Equal(7, await db.ScalarAsync<int>("SELECT COUNT(*) FROM NepRecords"));
        Assert.Equal(5, await db.ScalarAsync<int>("SELECT COUNT(*) FROM CorrectiveActions"));
        Assert.Equal(tombstonesBefore, await CountTombstonesAsync(db));

        var cursorX = await MaxSequenceAsync(db);
        await db.Records().ClearAllAsync(admin);
        var cursorY = await MaxSequenceAsync(db);

        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM NepRecords"));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM CorrectiveActions"));
        var tombs = await db.StringsAsync(
            $"SELECT CONVERT(nvarchar(36), EntityId) FROM SyncChangeLogs WHERE Sequence > {cursorX} AND ChangeType = N'{SyncConstants.ChangeRecordDeleted}'");
        Assert.Equal(7, tombs.Count);
        Assert.Equal(7, tombs.Distinct().Count());
        Assert.Equal(7, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM SyncChangeLogs WHERE Sequence > {cursorX} AND Sequence <= {cursorY}"));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM SyncChangeLogs WHERE ChangeType = N'RecordsCleared'"));
        Assert.Equal(2, await db.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM SyncChangeLogs WHERE Sequence > {cursorX} AND OwnerUserId = N'{op.UserId}'"));

        // Pull X → Y paginado (PageSize 3) por admin: los 7 tombstones, sin duplicados.
        var sync = db.Sync();
        var adminChanges = await DrainAsync(sync, admin, cursorX, 3);
        Assert.Equal(ids.Concat(opIds).OrderBy(g => g).ToList(),
            adminChanges.Where(c => c.ChangeType == SyncConstants.ChangeRecordDeleted).Select(c => c.EntityId).OrderBy(g => g).ToList());

        // Operario solo recibe los tombstones de sus registros.
        var opChanges = await DrainAsync(sync, op, cursorX, 3);
        Assert.Equal(opIds.OrderBy(g => g).ToList(),
            opChanges.Where(c => c.EntityType == SyncConstants.EntityNepRecord).Select(c => c.EntityId).OrderBy(g => g).ToList());

        // ClearAll → Create nuevo: el nuevo no es afectado.
        var fresh = Assert.Single((await sync.PushAsync(PushCreate(NewOp(), 22), admin, "new")).Results);
        Assert.Equal(nameof(SyncOperationResult.Accepted), fresh.Result);
        var afterY = await DrainAsync(sync, admin, cursorY, 3);
        var nep = afterY.Where(c => c.EntityType == SyncConstants.EntityNepRecord).ToList();
        Assert.Equal(SyncConstants.ChangeRecordUpserted, Assert.Single(nep).ChangeType);
        Assert.Equal(fresh.EntityId, nep[0].EntityId);
        Assert.Equal(1, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM NepRecords WHERE Id = '{fresh.EntityId}'"));

        var fromX = await DrainAsync(sync, admin, cursorX, 500);
        var lastByEntity = fromX.Where(c => c.EntityType == SyncConstants.EntityNepRecord)
            .GroupBy(c => c.EntityId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Sequence).Last().ChangeType);
        Assert.Equal(SyncConstants.ChangeRecordUpserted, lastByEntity[fresh.EntityId!.Value]);
        Assert.All(ids.Concat(opIds), id => Assert.Equal(SyncConstants.ChangeRecordDeleted, lastByEntity[id]));

        // Segundo ClearAll: borra solo el nuevo (1 tombstone).
        await db.Records().ClearAllAsync(admin);
        Assert.Equal(fresh.EntityId.ToString(), (await db.StringsAsync(
            $"SELECT CONVERT(nvarchar(36), EntityId) FROM SyncChangeLogs WHERE Sequence > {cursorY} AND ChangeType = N'{SyncConstants.ChangeRecordDeleted}'")).Single(),
            ignoreCase: true);
        await AssertNoOrphansAsync(db);
    }

    // ---------- Parte L: Pull / cursor ----------

    [SqlServerFact]
    public async Task Pull_Cursor_Pages_Are_Ascending_Without_Loss_Or_Duplicates()
    {
        await using var db = await StartAsync("pull");
        var admin = await db.CreateActorAsync("pull_admin", AppUserRole.Admin);
        var sync = db.Sync();
        for (var i = 0; i < 7; i++)
        {
            await CreateViaPushAsync(sync, admin, 10 + i);
        }

        var all = (await db.StringsAsync("SELECT CONVERT(nvarchar(20), Sequence) FROM SyncChangeLogs ORDER BY Sequence"))
            .Select(long.Parse).ToList();

        // Cursor 0, PageSize pequeño, Pull consecutivos hasta HasMore=false.
        var seen = new List<long>();
        long cursor = 0;
        var pages = 0;
        while (true)
        {
            var page = await sync.PullAsync(Pull(cursor, 4), admin, $"p{pages}");
            pages++;
            Assert.True(page.Changes.Count <= 4);
            Assert.Equal(page.Changes.Select(c => c.Sequence).OrderBy(s => s), page.Changes.Select(c => c.Sequence));
            Assert.All(page.Changes, c => Assert.True(c.Sequence > cursor));
            if (page.Changes.Count > 0)
            {
                Assert.Equal(page.Changes[^1].Sequence, page.NextCursor);
            }

            seen.AddRange(page.Changes.Select(c => c.Sequence));
            cursor = page.NextCursor;
            if (!page.HasMore)
            {
                break;
            }

            Assert.True(pages < 100);
        }

        Assert.Equal(all, seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        var empty = await sync.PullAsync(Pull(cursor, 4), admin, "end");
        Assert.Empty(empty.Changes);
        Assert.False(empty.HasMore);
        Assert.Equal(cursor, empty.NextCursor);

        // Cursor N intermedio.
        var mid = all[all.Count / 2];
        var fromMid = await DrainAsync(sync, admin, mid, 2);
        Assert.Equal(all.Where(s => s > mid).ToList(), fromMid.Select(c => c.Sequence).ToList());

        // X → Update → Delete → Pull.
        var (id, stamp) = await CreateViaPushAsync(sync, admin, 33);
        var x = await MaxSequenceAsync(db);
        var upd = Assert.Single((await sync.PushAsync(PushUpdate(NewOp(), id, stamp, 44), admin, "u")).Results);
        Assert.Single((await sync.PushAsync(PushDelete(NewOp(), id, upd.ConcurrencyStamp!), admin, "d")).Results);
        var tail = await DrainAsync(sync, admin, x, 1);
        Assert.Equal(
            new[] { SyncConstants.ChangeRecordUpserted, SyncConstants.ChangeRecordDeleted },
            tail.Select(c => c.ChangeType).ToArray());
        Assert.Equal(44, tail[0].Payload.GetProperty("neps").GetDouble());
        Assert.True(tail[0].Sequence < tail[1].Sequence);
    }

    /// <summary>
    /// IDENTITY puede asignar Sequence N a una TX aún abierta mientras N+1 ya está confirmada.
    /// EnsureCreated crea la base con READ_COMMITTED_SNAPSHOT ON: el Pull debe esperar a N en
    /// lugar de devolver N+1 y avanzar el cursor más allá de N (pérdida permanente).
    /// </summary>
    [SqlServerFact]
    public async Task Pull_Does_Not_Skip_Uncommitted_Lower_Sequence_With_Read_Committed_Snapshot()
    {
        await using var db = await StartAsync("pull_gap");
        var admin = await db.CreateActorAsync("gap_admin", AppUserRole.Admin);
        Assert.True(await db.ScalarAsync<bool>("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()"));
        var cursor = await MaxSequenceAsync(db);

        await using var slow = new SqlConnection(db.ConnectionString);
        await slow.OpenAsync();
        await using var tx = (SqlTransaction)await slow.BeginTransactionAsync();
        var lower = await InsertTombstoneAsync(slow, tx, admin.UserId);
        var higher = await InsertTombstoneAsync(db.ConnectionString, admin.UserId);
        Assert.True(higher > lower);

        var pullTask = db.Sync().PullAsync(Pull(cursor, 100), admin, "gap");
        var finishedEarly = await Task.WhenAny(pullTask, Task.Delay(TimeSpan.FromSeconds(3))) == pullTask;
        _output.WriteLine($"Pull terminó con TX abierta: {finishedEarly}");

        await tx.CommitAsync();
        var page = await pullTask.WaitAsync(TimeSpan.FromSeconds(30));
        var after = await db.Sync().PullAsync(Pull(page.NextCursor, 100), admin, "gap-next");
        var sequences = page.Changes.Concat(after.Changes).Select(c => c.Sequence).ToList();
        _output.WriteLine($"lower={lower} higher={higher} page=[{string.Join(',', page.Changes.Select(c => c.Sequence))}] " +
                          $"next={page.NextCursor} after=[{string.Join(',', after.Changes.Select(c => c.Sequence))}]");
        Assert.Contains(lower, sequences);
        Assert.Contains(higher, sequences);
        Assert.True(sequences.IndexOf(lower) < sequences.IndexOf(higher));
        Assert.False(finishedEarly);
    }

    // ---------- Parte M: paginación server-side ----------

    [SqlServerFact]
    public async Task Records_Server_Side_Pagination_Filters_Order_Ownership_And_SeesAll()
    {
        await using var db = await StartAsync("paging");
        var userA = Guid.NewGuid().ToString();
        var userB = Guid.NewGuid().ToString();
        var baseUtc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        await using (var ctx = await db.ContextFactory.CreateDbContextAsync())
        {
            for (var i = 0; i < 120; i++)
            {
                ctx.NepRecords.Add(new NepRecord
                {
                    Id = Guid.NewGuid(),
                    Telar = $"T-{i:D3}",
                    Neps = i % 10 == 0 ? 55 : 10,
                    CreatedAt = baseUtc.AddSeconds(-(i / 3)), // empates de CreatedAt de a 3
                    CreatedByUserId = userA,
                    CreatedByRole = "Operario",
                    ConcurrencyStamp = Guid.NewGuid().ToString("N")
                });
            }

            for (var i = 0; i < 30; i++)
            {
                ctx.NepRecords.Add(new NepRecord
                {
                    Id = Guid.NewGuid(),
                    Telar = $"BX-{i:D3}",
                    Neps = 12,
                    CreatedAt = baseUtc.AddMinutes(i),
                    CreatedByUserId = userB,
                    CreatedByRole = "Operario",
                    ConcurrencyStamp = Guid.NewGuid().ToString("N")
                });
            }

            await ctx.SaveChangesAsync();
        }

        var sql = new ConcurrentQueue<string>();
        var options = new DbContextOptionsBuilder<RegNepsDbContext>()
            .UseSqlServer(db.ConnectionString)
            .LogTo(sql.Enqueue, new[] { RelationalEventId.CommandExecuted })
            .Options;
        var repo = new NepRecordRepository(new PooledDbContextFactory<RegNepsDbContext>(options));

        var seenA = new List<Guid>();
        for (var p = 1; p <= 3; p++)
        {
            var page = await repo.QueryPagedAsync(new RecordFilters(), userA, viewerSeesAll: false, p, 50);
            Assert.Equal(120, page.TotalCount);
            Assert.Equal(3, page.TotalPages);
            Assert.Equal(p == 3 ? 20 : 50, page.Items.Count);
            Assert.All(page.Items, r => Assert.Equal(userA, r.CreatedByUserId));
            for (var i = 0; i < page.Items.Count - 1; i++)
            {
                var a = page.Items[i];
                var b = page.Items[i + 1];
                Assert.True(a.CreatedAt > b.CreatedAt
                            || (a.CreatedAt == b.CreatedAt && new SqlGuid(a.Id).CompareTo(new SqlGuid(b.Id)) > 0));
            }

            seenA.AddRange(page.Items.Select(r => r.Id));
        }

        Assert.Equal(120, seenA.Distinct().Count());

        var commands = sql.Where(s => s.Contains("[NepRecords]", StringComparison.Ordinal)).ToList();
        foreach (var cmd in commands)
        {
            _output.WriteLine(cmd);
        }

        Assert.Contains(commands, s => s.Contains("OFFSET", StringComparison.Ordinal)
                                       && s.Contains("FETCH NEXT", StringComparison.Ordinal)
                                       && s.Contains("ORDER BY", StringComparison.Ordinal)
                                       && s.Contains("[CreatedAt] DESC", StringComparison.Ordinal)
                                       && s.Contains("[Id] DESC", StringComparison.Ordinal));
        Assert.Contains(commands, s => s.Contains("COUNT(*)", StringComparison.Ordinal));

        var clamp = await repo.QueryPagedAsync(new RecordFilters(), userA, false, 1, 500_000);
        Assert.Equal(RecordPaging.MaxPageSize, clamp.PageSize);
        Assert.Equal(100, clamp.Items.Count);

        var critical = await repo.QueryPagedAsync(new RecordFilters { AlertLevel = AlertLevel.SecondQuality }, userA, false, 1, 50);
        Assert.Equal(12, critical.TotalCount);
        Assert.All(critical.Items, r => Assert.Equal(55, r.Neps));
        Assert.Equal(critical.TotalCount, await repo.CountFilteredAsync(
            new RecordFilters { AlertLevel = AlertLevel.SecondQuality }, userA, false));

        var byTelar = await repo.QueryPagedAsync(new RecordFilters { Telar = "T-000" }, userA, false, 1, 50);
        Assert.Equal(1, byTelar.TotalCount);

        var seesAll = await repo.QueryPagedAsync(new RecordFilters(), userA, viewerSeesAll: true, 1, 100);
        Assert.Equal(150, seesAll.TotalCount);
        Assert.Contains(seesAll.Items, r => r.CreatedByUserId == userB);

        var pageB = await repo.QueryPagedAsync(new RecordFilters(), userB, false, 1, 100);
        Assert.Equal(30, pageB.TotalCount);
        Assert.DoesNotContain(pageB.Items, r => r.CreatedByUserId == userA);

        var failClosed = await repo.QueryPagedAsync(new RecordFilters(), viewerUserId: null, viewerSeesAll: false, 1, 50);
        Assert.Equal(0, failClosed.TotalCount);
    }

    // ---------- Parte N: catálogos ----------

    [SqlServerFact]
    public async Task Fabric_And_Lote_Mutations_Emit_Catalog_ChangeLogs_And_Pull_Them()
    {
        await using var db = await StartAsync("catalog");
        var op = await db.CreateActorAsync("cat_op", AppUserRole.Operario);
        var fabrics = new FabricRepository(db.ContextFactory);
        var lotes = new LoteTramaRepository(db.ContextFactory);
        var cursor = await MaxSequenceAsync(db);

        // Comparación case-insensitive con collation del servidor: reutiliza la tela sembrada.
        var seeded = await fabrics.EnsureActiveByNameAsync("TELA ESTÁNDAR A");
        Assert.Equal("Tela estándar A", seeded.Name);
        Assert.Equal(cursor, await MaxSequenceAsync(db));

        var fabric = await fabrics.EnsureActiveByNameAsync("Tela 2J");
        await fabrics.UpdateAsync(new Fabric { Id = fabric.Id, Name = "Tela 2J Renombrada", Code = "T2J", IsActive = true });
        await fabrics.DeleteAsync(fabric.Id);

        var lote = await lotes.EnsureActiveByCodeAsync("lote2j");
        Assert.Equal("LOTE2J", lote.Code);
        await lotes.UpdateAsync(new LoteTramaItem { Id = lote.Id, Code = "LOTE2J", IsActive = false });
        await lotes.DeleteAsync(lote.Id);

        var changes = await DrainAsync(db.Sync(), op, cursor, 2);
        var expected = new[]
        {
            SyncConstants.ChangeCatalogUpserted, SyncConstants.ChangeCatalogUpserted, SyncConstants.ChangeCatalogDeleted
        };
        Assert.All(changes, c => Assert.Equal(SyncConstants.EntityCatalogItem, c.EntityType));
        var fabricChanges = changes.Where(c => c.EntityId == fabric.Id).ToList();
        Assert.Equal(expected, fabricChanges.Select(c => c.ChangeType).ToArray());
        Assert.Equal(expected, changes.Where(c => c.EntityId == lote.Id).Select(c => c.ChangeType).ToArray());
        Assert.Equal(6, changes.Count);
        Assert.Contains("Tela 2J Renombrada", fabricChanges[1].Payload.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM Fabrics WHERE Id = '{fabric.Id}'"));
        Assert.Equal(0, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM LoteTramaItems WHERE Id = '{lote.Id}'"));
    }

    // ---------- Parte O: concurrencia real ----------

    [SqlServerFact]
    public async Task Concurrent_Updates_With_Same_Stamp_Exactly_One_Accepted_Rest_Conflict()
    {
        await using var db = await StartAsync("concurrency");
        var admin = await db.CreateActorAsync("conc_admin", AppUserRole.Admin);
        var (id, stamp) = await CreateViaPushAsync(db.Sync(), admin, 12);
        var logsBefore = await db.ScalarAsync<int>("SELECT COUNT(*) FROM SyncChangeLogs");

        const int writers = 8;
        var services = Enumerable.Range(0, writers).Select(_ => db.Sync()).ToList();
        using var gate = new SemaphoreSlim(0);
        var tasks = services.Select((svc, i) => Task.Run(async () =>
        {
            await gate.WaitAsync();
            return Assert.Single((await svc.PushAsync(PushUpdate(NewOp(), id, stamp, 100 + i, $"W{i}"), admin, $"w{i}")).Results);
        })).ToList();
        gate.Release(writers);
        var results = await Task.WhenAll(tasks);

        foreach (var r in results)
        {
            _output.WriteLine($"{r.Result} {r.ErrorCode}");
        }

        var accepted = Assert.Single(results, r => r.Result == nameof(SyncOperationResult.Accepted));
        Assert.Equal(writers - 1, results.Count(r => r.Result == nameof(SyncOperationResult.Conflict)));
        Assert.All(results.Where(r => r.Result == nameof(SyncOperationResult.Conflict)),
            r => Assert.Equal(accepted.ConcurrencyStamp, r.ServerConcurrencyStamp));
        Assert.Equal(logsBefore + 1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM SyncChangeLogs"));
        Assert.Equal(accepted.ConcurrencyStamp, (await db.StringsAsync($"SELECT ConcurrencyStamp FROM NepRecords WHERE Id = '{id}'")).Single());
    }

    /// <summary>
    /// Regresión D1 (FASE 2J / 2J.1): reintentos concurrentes del mismo ClientOperationId. Por TCP + RCSI la
    /// TX ganadora puede confirmar después de la primera comprobación del opId; la perdedora no debe responder
    /// Conflict (stamp distinto) ni ENTITY_DELETED (Delete), sino Duplicate. Varias rondas por la naturaleza
    /// temporal de la carrera.
    /// </summary>
    [SqlServerTheory]
    [InlineData(SyncConstants.OperationUpdateRecord)]
    [InlineData(SyncConstants.OperationApplyCorrective)]
    [InlineData(SyncConstants.OperationDeleteRecord)]
    public async Task Concurrent_Retries_With_Same_ClientOperationId_One_Accepted_Others_Duplicate(string operationType)
    {
        await using var db = await StartAsync("concurrency_op");
        var admin = await db.CreateActorAsync("conc_op_admin", AppUserRole.Admin);
        const int rounds = 5;
        const int writers = 8;

        for (var round = 1; round <= rounds; round++)
        {
            var (id, stamp) = await CreateViaPushAsync(db.Sync(), admin, 12);
            var opId = NewOp();
            var logsBefore = await db.ScalarAsync<int>("SELECT COUNT(*) FROM SyncChangeLogs");
            var request = RetryRequest(operationType, opId, id, stamp);

            var services = Enumerable.Range(0, writers).Select(_ => db.Sync()).ToList();
            using var gate = new SemaphoreSlim(0);
            var tasks = services.Select(svc => Task.Run(async () =>
            {
                await gate.WaitAsync();
                return Assert.Single((await svc.PushAsync(request, admin, "retry")).Results);
            })).ToList();
            gate.Release(writers);
            var results = await Task.WhenAll(tasks);
            _output.WriteLine($"{operationType} ronda {round}: " +
                              string.Join(", ", results.Select(r => $"{r.Result} {r.ErrorCode}".Trim())));

            var accepted = Assert.Single(results, r => r.Result == nameof(SyncOperationResult.Accepted));
            Assert.Equal(0, results.Count(r => r.Result == nameof(SyncOperationResult.Conflict)));
            Assert.Equal(writers - 1, results.Count(r => r.Result == nameof(SyncOperationResult.Duplicate)));

            var lateRetry = Assert.Single((await db.Sync().PushAsync(request, admin, "late")).Results);
            Assert.Equal(nameof(SyncOperationResult.Duplicate), lateRetry.Result);

            Assert.Equal(1, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM SyncChangeLogs WHERE ClientOperationId = N'{opId}'"));
            Assert.Equal(logsBefore + 1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM SyncChangeLogs"));
            await AssertSingleEffectAsync(db, operationType, id, accepted);

            // Control: otro ClientOperationId con el stamp ya obsoleto no se convierte en Duplicate.
            var control = Assert.Single((await db.Sync().PushAsync(
                RetryRequest(operationType, NewOp(), id, stamp), admin, "control")).Results);
            if (operationType == SyncConstants.OperationDeleteRecord)
            {
                Assert.Equal(nameof(SyncOperationResult.Invalid), control.Result);
                Assert.Equal("ENTITY_DELETED", control.ErrorCode);
            }
            else
            {
                Assert.Equal(nameof(SyncOperationResult.Conflict), control.Result);
                Assert.Equal(accepted.ConcurrencyStamp, control.ServerConcurrencyStamp);
            }

            Assert.Equal(logsBefore + 1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM SyncChangeLogs"));
        }
    }

    private static SyncPushRequest RetryRequest(string operationType, string opId, Guid id, string stamp) =>
        operationType switch
        {
            SyncConstants.OperationUpdateRecord => PushUpdate(opId, id, stamp, 30),
            SyncConstants.OperationApplyCorrective => PushCorrective(opId, id, stamp, "Ajuste concurrente", "QA"),
            SyncConstants.OperationDeleteRecord => PushDelete(opId, id, stamp),
            _ => throw new ArgumentOutOfRangeException(nameof(operationType), operationType, null)
        };

    private static async Task AssertSingleEffectAsync(
        SqlServerValidationDatabase db, string operationType, Guid id, SyncOperationResultDto accepted)
    {
        var records = await db.ScalarAsync<int>($"SELECT COUNT(*) FROM NepRecords WHERE Id = '{id}'");
        var tombstones = await db.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM SyncChangeLogs WHERE EntityId = '{id}' AND ChangeType = N'{SyncConstants.ChangeRecordDeleted}'");
        var correctives = await db.ScalarAsync<int>($"SELECT COUNT(*) FROM CorrectiveActions WHERE NepRecordId = '{id}'");

        switch (operationType)
        {
            case SyncConstants.OperationDeleteRecord:
                Assert.Equal(0, records);
                Assert.Equal(1, tombstones);
                break;
            case SyncConstants.OperationApplyCorrective:
                Assert.Equal(1, records);
                Assert.Equal(1, correctives);
                Assert.Equal(accepted.ConcurrencyStamp,
                    (await db.StringsAsync($"SELECT ConcurrencyStamp FROM NepRecords WHERE Id = '{id}'")).Single());
                break;
            default:
                Assert.Equal(1, records);
                Assert.Equal(0, correctives);
                Assert.Equal(30, await db.ScalarAsync<double>($"SELECT Neps FROM NepRecords WHERE Id = '{id}'"));
                Assert.Equal(accepted.ConcurrencyStamp,
                    (await db.StringsAsync($"SELECT ConcurrencyStamp FROM NepRecords WHERE Id = '{id}'")).Single());
                break;
        }
    }

    // ---------- Helpers ----------

    private static async Task<SqlServerValidationDatabase> StartAsync(string label)
    {
        var db = SqlServerValidationDatabase.Create(label);
        await db.StartAsync();
        return db;
    }

    private static string NewOp() => Guid.NewGuid().ToString("N");

    private static SyncPushRequest Push(SyncOperationDto op) =>
        new() { ProtocolVersion = SyncProtocol.Version, DeviceId = "dev-2j", Operations = [op] };

    private static SyncPushRequest PushCreate(string opId, double neps) =>
        Push(new SyncOperationDto
        {
            ClientOperationId = opId,
            OperationType = SyncConstants.OperationCreateRecord,
            CaptureSessionId = "sess-2j",
            Payload = JsonSerializer.SerializeToElement(new SyncCreateRecordPayload
            {
                Telar = "T-2J", Neps = neps, Tela = "Denim", LoteTrama = "L2J", Turno = "A", Operario = "op", LineaProduccion = "L1"
            })
        });

    private static SyncPushRequest PushUpdate(string opId, Guid id, string stamp, double neps, string telar = "T-2J-U") =>
        Push(new SyncOperationDto
        {
            ClientOperationId = opId,
            OperationType = SyncConstants.OperationUpdateRecord,
            ExpectedConcurrencyStamp = stamp,
            Payload = JsonSerializer.SerializeToElement(new SyncUpdateRecordPayload
            {
                EntityId = id, Telar = telar, Neps = neps, Tela = "Denim", LoteTrama = "L2J", Turno = "B", Operario = "op2", LineaProduccion = "L1"
            })
        });

    private static SyncPushRequest PushDelete(string opId, Guid id, string stamp) =>
        Push(new SyncOperationDto
        {
            ClientOperationId = opId,
            OperationType = SyncConstants.OperationDeleteRecord,
            ExpectedConcurrencyStamp = stamp,
            Payload = JsonSerializer.SerializeToElement(new SyncDeleteRecordPayload { EntityId = id })
        });

    private static SyncPushRequest PushCorrective(string opId, Guid id, string stamp, string accion, string responsable) =>
        Push(new SyncOperationDto
        {
            ClientOperationId = opId,
            OperationType = SyncConstants.OperationApplyCorrective,
            ExpectedConcurrencyStamp = stamp,
            Payload = JsonSerializer.SerializeToElement(new SyncApplyCorrectivePayload
            {
                EntityId = id, Accion = accion, Responsable = responsable, MarcarRevisado = true
            })
        });

    private static SyncPullRequest Pull(long cursor, int pageSize) =>
        new() { ProtocolVersion = SyncProtocol.Version, DeviceId = "dev-2j", Cursor = cursor, PageSize = pageSize };

    private static async Task<(Guid Id, string Stamp)> CreateViaPushAsync(SyncAppService sync, RecordActor actor, double neps)
    {
        var result = Assert.Single((await sync.PushAsync(PushCreate(NewOp(), neps), actor, "c")).Results);
        Assert.Equal(nameof(SyncOperationResult.Accepted), result.Result);
        return (result.EntityId!.Value, result.ConcurrencyStamp!);
    }

    /// <summary>Pull consecutivos desde <paramref name="cursor"/> hasta HasMore=false (sin duplicados).</summary>
    private static async Task<List<SyncChangeDto>> DrainAsync(SyncAppService sync, RecordActor actor, long cursor, int pageSize)
    {
        var all = new List<SyncChangeDto>();
        for (var guard = 0; guard < 1000; guard++)
        {
            var page = await sync.PullAsync(Pull(cursor, pageSize), actor, "drain");
            Assert.All(page.Changes, c => Assert.True(c.Sequence > cursor));
            all.AddRange(page.Changes);
            cursor = page.NextCursor;
            if (!page.HasMore)
            {
                break;
            }
        }

        Assert.Equal(all.Count, all.Select(c => c.Sequence).Distinct().Count());
        Assert.Equal(all.Select(c => c.Sequence).OrderBy(s => s), all.Select(c => c.Sequence));
        return all;
    }

    private static Task<long> MaxSequenceAsync(SqlServerValidationDatabase db) =>
        db.ScalarAsync<long>("SELECT ISNULL(MAX(Sequence), 0) FROM SyncChangeLogs");

    private static Task<int> CountTombstonesAsync(SqlServerValidationDatabase db) =>
        db.ScalarAsync<int>($"SELECT COUNT(*) FROM SyncChangeLogs WHERE ChangeType = N'{SyncConstants.ChangeRecordDeleted}'");

    private static async Task<string> RowFingerprintAsync(SqlServerValidationDatabase db, Guid id) =>
        (await db.StringsAsync(
            $"""
            SELECT Telar + '|' + CONVERT(nvarchar(20), Neps) + '|' + Tela + '|' + LoteTrama + '|' + Turno + '|' + Operario + '|'
                   + LineaProduccion + '|' + Observacion + '|' + ISNULL(CreatedByUserId, '') + '|' + ISNULL(CaptureSessionId, '') + '|'
                   + CONVERT(nvarchar(40), CreatedAt, 126)
            FROM NepRecords WHERE Id = '{id}'
            """)).Single();

    /// <summary>CHECK (error 547, no aborta el lote) para forzar el fallo del INSERT de ChangeLog dentro de la TX.</summary>
    private static Task ArmChangeLogRejectionAsync(SqlServerValidationDatabase db) =>
        db.ExecuteAsync(
            $"""
            ALTER TABLE SyncChangeLogs WITH NOCHECK ADD CONSTRAINT CK_2J_RejectChangeLog
            CHECK (ClientOperationId IS NULL OR ClientOperationId NOT LIKE N'{FailPrefix}%')
            """);

    private static Task DisarmChangeLogRejectionAsync(SqlServerValidationDatabase db) =>
        db.ExecuteAsync("ALTER TABLE SyncChangeLogs DROP CONSTRAINT CK_2J_RejectChangeLog");

    /// <summary>Ni NepRecord sin ChangeLog, ni último Upsert sin fila, ni tombstone con fila viva.</summary>
    private static async Task AssertNoOrphansAsync(SqlServerValidationDatabase db)
    {
        Assert.Equal(0, await db.ScalarAsync<int>(
            $"""
            SELECT COUNT(*) FROM NepRecords r
            WHERE NOT EXISTS (SELECT 1 FROM SyncChangeLogs c
                              WHERE c.EntityType = N'{SyncConstants.EntityNepRecord}' AND c.EntityId = r.Id
                                AND c.ChangeType = N'{SyncConstants.ChangeRecordUpserted}')
            """));
        Assert.Equal(0, await db.ScalarAsync<int>(
            $"""
            SELECT COUNT(*)
            FROM (SELECT EntityId, MAX(Sequence) AS LastSeq FROM SyncChangeLogs
                  WHERE EntityType = N'{SyncConstants.EntityNepRecord}' GROUP BY EntityId) l
            JOIN SyncChangeLogs c ON c.Sequence = l.LastSeq
            WHERE (c.ChangeType = N'{SyncConstants.ChangeRecordUpserted}' AND NOT EXISTS (SELECT 1 FROM NepRecords r WHERE r.Id = l.EntityId))
               OR (c.ChangeType = N'{SyncConstants.ChangeRecordDeleted}' AND EXISTS (SELECT 1 FROM NepRecords r WHERE r.Id = l.EntityId))
            """));
    }

    private static async Task<long> InsertTombstoneAsync(SqlConnection conn, SqlTransaction? tx, string ownerUserId)
    {
        var entityId = Guid.NewGuid();
        await using var cmd = new SqlCommand(
            """
            INSERT INTO SyncChangeLogs (EntityType, EntityId, ChangeType, OccurredAtUtc, ActorUserId, OwnerUserId, ClientOperationId, DeviceId, PayloadJson)
            OUTPUT INSERTED.Sequence
            VALUES (@type, @id, @change, SYSUTCDATETIME(), @owner, @owner, NULL, NULL, @payload)
            """,
            conn,
            tx);
        cmd.Parameters.AddWithValue("@type", SyncConstants.EntityNepRecord);
        cmd.Parameters.AddWithValue("@id", entityId);
        cmd.Parameters.AddWithValue("@change", SyncConstants.ChangeRecordDeleted);
        cmd.Parameters.AddWithValue("@owner", ownerUserId);
        cmd.Parameters.AddWithValue("@payload", JsonSerializer.Serialize(new
        {
            id = entityId,
            ownerUserId,
            lastConcurrencyStamp = "gap",
            deletedAtUtc = DateTime.UtcNow
        }));
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<long> InsertTombstoneAsync(string connectionString, string ownerUserId)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        return await InsertTombstoneAsync(conn, null, ownerUserId);
    }
}
