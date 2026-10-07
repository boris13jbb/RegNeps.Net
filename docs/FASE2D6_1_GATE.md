# FASE 2D.6.1 — Gate post-Delete offline

## Alcance

Auditoría/gate **sin capacidades nuevas** tras FASE 2D.6 (`a277eb5` / `6d22406`).

Verifica que Delete offline mantiene atomicidad, idempotencia, `ConcurrencyStamp`, tombstones, Pull, permisos, SeesAll/EntityId, sin LWW ni auto-resolución de Conflict.

## Arquitectura auditada (sin cambios de protocolo)

```text
UI → OfflineCaptureService.DeleteRecordAsync
  → TX: LocalNepRecord.IsDeleted + PendingOperation DeleteRecord
  → SyncEngine → /api/sync/push|pull
  → SyncAppService / SyncPersistence
  → SyncChangeLog RecordDeleted + Pull tombstone
```

`ListBlockingOpsAsync` filtra por `LocalNepRecordId` / `TargetServerRecordId` (**no** por `UserId`).

## SQL Server

| Intento | Resultado |
|---------|-----------|
| `(localdb)\mssqllocaldb` vía `sqlcmd` | **NO DISPONIBLE** (timeout / instancia inaccesible) |
| Harness CI con SQL Server | **NO presente** |

**Estado SQL Server E2E: PENDIENTE / gap ambiental** (igual que 2D.4.1).

### Sustituto ejecutado (mismo código servidor)

Backend de pruebas usa **SQLite `:memory:`** con `SyncAppService` + `SyncPersistence` reales:

- `SyncUpdateDeleteTests` (2C) — Create/Update/stale/Delete/idempotencia/Pull/Update-after-Delete/Delete-after-Delete
- `OfflineBackendIntegrationGateTests` (2D.4.1) — Offline SQLite ↔ backend in-process, incl. `Gate_Delete_Tombstone_No_Resurrection`

Esto **no** equivale a SQL Server físico; se declara explícitamente.

## Matriz A–I

| Escenario | Evidencia | Resultado |
|-----------|-----------|-----------|
| A Create + ChangeLog | SyncUpdateDelete / Gate_Create | OK (SQLite backend) |
| B Update stamp + Pull | SyncUpdateDelete / Gate_Update | OK |
| C Stale Update → Conflict | `Update_Wrong_Stamp_Is_Conflict_Without_Mutation` | OK |
| D Delete + tombstone atómico | `Delete_Valid_Creates_Tombstone_Atomically` | OK |
| E Delete stale → Conflict, sin tombstone | `Delete_Wrong_Stamp_Is_Conflict` | OK |
| F Delete idempotente mismo ClientOperationId | `Delete_Retry_Same_ClientOperationId_Is_Duplicate_No_Extra_Tombstone` | OK |
| G Pull RecordDeleted | `Pull_Receives_Tombstone_After_Delete` / Gate_Delete | OK |
| H Update after Delete | `Update_After_Delete_Is_EntityDeleted_Not_Duplicate` | OK |
| I Delete after Delete (nuevo OpId) | `Delete_Already_Deleted_Different_OpId_Is_EntityDeleted` | OK |
| Cross-session SeesAll vs EntityId | `SeesAll_Delete_Blocked_By_Other_Users_Update_Pending_On_Same_Entity` | OK |
| Conflict bloquea Delete/Edit | OfflineOfflineDeleteTests | OK |

## Auditoría de garantías

| Garantía | Veredicto |
|----------|-----------|
| Atomicidad local Delete+Outbox | OK (TX + rollback test) |
| Idempotencia ClientOperationId | OK (Duplicate, sin segundo Sequence) |
| ConcurrencyStamp | OK (stale Conflict) |
| Tombstones RecordDeleted | OK |
| Pull sin resurrección | OK |
| SeesAll / EntityId (no solo UserId) | OK |
| Conflict persistente, sin LWW, sin auto-aceptar | OK |
| Protocolo push/pull sin cambios | OK |

## Resultados de ejecución

| Chequeo | Resultado |
|---------|-----------|
| OfflineOfflineDeleteTests | **16/16** |
| SyncUpdateDeleteTests | ver filtro conjunto |
| DatabaseConcurrencyTests | ver filtro conjunto |
| OfflineBackendIntegrationGateTests | ver filtro conjunto |
| Filtro Delete+Sync+Concurrency+Gate241 | **54/54** |
| Suite total | **410/410** Passed |
| Web Release | OK, 0 warnings |
| Android Debug | OK, 0 warnings |
| Android E2E runtime | **pendiente** |
| SQL Server físico | **no disponible** (LocalDB inaccesible) |

## Incidencia operacional

Si `RegNeps.Web` (`dotnet run`) está activo, bloquea copia de DLLs al compilar tests (MSB3026). Detener el proceso, ejecutar tests, relanzar. No es cambio arquitectónico.

## Commit

Gate 2D.6.1: test SeesAll Delete vs Update Pending ajeno + `docs/FASE2D6_1_GATE.md` (sin cambios de producción).
