# FASE 2D.6 — Delete offline UI

## Alcance

Permite eliminar offline un registro **ya sincronizado** (`ServerRecordId` + `ConcurrencyStamp`), generando `PendingOperation` `DeleteRecord` que el `SyncEngine` existente envía por Push. Reutiliza el protocolo 2C (tombstones `RecordDeleted`, `ExpectedConcurrencyStamp`, idempotencia por `ClientOperationId`).

**Incluye**

- `OfflineCaptureService.DeleteRecordAsync` atómico (`IsDeleted` + Outbox).
- Elegibilidad UX: `DeleteRecords`, ownership / SeesAll por `RoleCode`, bloqueos por EntityId.
- UI: botón Eliminar en `OfflineEditRecordPage` y detalle de operaciones; lista mutable en Captura.
- Tests `OfflineOfflineDeleteTests`.

**No incluye**

- Resolución de Conflict / merge / LWW.
- Delete masivo / ClearAll offline.
- Background sync / SignalR.
- Cambios de `/api/sync/push|pull` (ninguno en esta fase).

## Flujo

```text
UI Confirmar Eliminar
  → OfflineCaptureService.DeleteRecordAsync
  → TX: LocalNepRecord.IsDeleted=true + PendingOperation DeleteRecord
  → Pending (nuevo ClientOperationId, ExpectedConcurrencyStamp)
  → SyncEngine Push
  → Accepted | Duplicate | Conflict | Forbidden/SyncError
  → Pull RecordDeleted (no resurrección)
```

## Estados locales

| Situación | LocalNepRecord | Outbox |
|-----------|----------------|--------|
| Delete pendiente | `IsDeleted=true`, `PendingSync` | Delete `Pending` |
| Accepted | `IsDeleted=true`, `Synced` | `Synced` |
| Duplicate | idempotente → `Synced` | `Synced` |
| Conflict (stamp stale) | `IsDeleted=true`, `Conflict` + snapshot servidor | `Conflict` (sin retry auto) |
| Forbidden | `IsDeleted=true` (intento local) | `SyncError` |
| ENTITY_DELETED | tombstone local / Conflict según SyncEngine | según código |

## Bloqueos (por EntityId, no solo UserId)

- Create Pending / sin `ServerRecordId`.
- Update o Delete Pending/Sending/Conflict para el mismo EntityId (cualquier UserId).
- `SyncStatus == Conflict` → “requiere revisión” (sin mutación destructiva adicional).
- Ya `IsDeleted` → no segundo Delete.
- Sin permiso UX `DeleteRecords` / no owner sin SeesAll.

## Idempotencia / concurrencia

- Mismo `ClientOperationId` → Duplicate (servidor 2C); SyncEngine marca Synced.
- Reuso de ClientOperationId con operación distinta → SyncError (`CLIENT_OPERATION_REUSED`) según contrato existente.
- Dos Delete distintos del mismo registro: el segundo se bloquea localmente; en servidor el segundo efectivo no recrea tombstone (tests 2C).
- Delete con stamp stale → Conflict; no eliminación silenciosa en servidor.
- Update tras Delete local → bloqueado (`Deleted`).
- Pull `RecordDeleted` no resucita ni genera otro Delete.

## Limitaciones

- Sin UI de resolución de Conflict.
- SeesAll UX por códigos de rol locales; servidor autoriza en Push.
- Android E2E runtime / SQL Server gate real: pendientes si el entorno no los ejecuta.

## Pruebas ejecutadas

| Chequeo | Resultado |
|---------|-----------|
| `OfflineOfflineDeleteTests` | 15/15 |
| Suite completa | **409/409** Passed |
| Web Release | OK, 0 warnings |
| Android Debug | OK, 0 warnings |
| SQL Server gate 2D.4.1 | no reejecutado en esta fase |
| Android E2E runtime | no ejecutado |

Commit: `a277eb5` en `feature/fase-2d5-offline-update` (sin push/PR).

## Pruebas no ejecutadas

- APK/dispositivo: Login → Create → Sync → Delete → Sync → Conflict.
- Gate SQL Server real de concurrencia.
