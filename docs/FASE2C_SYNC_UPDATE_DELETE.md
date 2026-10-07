# FASE 2C — Sync Update / Conflict / Delete / Tombstones

Base: FASE 2B (`0c7497b`) + 2B.1 (`fea3efa`).

## Alcance

- Push: `UpdateRecord`, `DeleteRecord` (además de `CreateRecord`)
- Conflictos con `ExpectedConcurrencyStamp` vs `ConcurrencyStamp`
- Tombstones `RecordDeleted` en `SyncChangeLog`
- Idempotencia Update/Delete por `(ActorUserId, ClientOperationId)`
- Pull incremental compatible (upsert + delete)
- Update/Delete online y DeleteMany vía store atómico
- Importación masiva: cada fila → `CreateWithChangeLogAsync` (ChangeLog por registro)

**No incluido:** SyncEngine MAUI, LocalSession, bridge, SignalR recovery, backfill histórico ejecutable, paginación 2F, catálogos, merge automático, Firebase/Flutter.

## Protocolo v1 (operaciones)

| OperationType | Permiso | Resultado típico |
|---------------|---------|------------------|
| `CreateRecord` | `CaptureRecords` | Accepted / Duplicate |
| `UpdateRecord` | `EditRecords` | Accepted / Duplicate / Conflict |
| `DeleteRecord` | `DeleteRecords` | Accepted / Duplicate / Conflict / Invalid |

Campos de operación: `ClientOperationId`, `OperationType`, `Payload`, `CaptureSessionId?`, `ExpectedConcurrencyStamp?`, `ClientCreatedAtUtc?`.

### UpdateRecord

Payload: `entityId` + campos de negocio (+ stamp opcional en payload u operación).

Servidor:

1. Autenticar / autorizar `EditRecords`
2. Localizar registro + ownership
3. Comparar `ExpectedConcurrencyStamp`
4. Si coincide: mutar, nuevo stamp, `UpdatedAt`, `RecordUpserted` ChangeLog, commit atómico
5. Si no: **Conflict** sin mutar ni ChangeLog

### Conflict

```
Result = Conflict
EntityId
ServerConcurrencyStamp
ServerSnapshot  // mismo shape canónico que Pull (RecordUpserted)
ChangeSequence? // no se genera uno nuevo en conflicto
```

Sin merge automático. Sin stack/SQL/secretos.

### DeleteRecord

```
BEGIN
  leer + validar ownership + stamp
  INSERT SyncChangeLog RecordDeleted (tombstone autónomo)
  DELETE NepRecord
COMMIT
```

Tombstone payload mínimo: `id`, `ownerUserId`, `deletedAtUtc`, `lastConcurrencyStamp`.

### Semántica Delete

| Caso | Resultado |
|------|-----------|
| Válido | Accepted + 1 tombstone |
| Reintento mismo `ClientOperationId` | Duplicate / `ALREADY_PROCESSED` |
| Stamp incorrecto | Conflict + ServerSnapshot |
| Registro inexistente | Invalid / `ENTITY_NOT_FOUND` |
| Ya eliminado (otro opId) | Invalid / `ENTITY_DELETED` (no segundo tombstone) |
| Sin permiso / ownership | Forbidden |

## ChangeTypes en Pull

- `RecordUpserted` — Create y Update (snapshot completo)
- `RecordDeleted` — Delete (tombstone)

## Payload canónico (upsert)

`id`, `telar`, `neps`, `mtsCalculados`, `tela`, `loteTrama`, `turno`, `operario`, `lineaProduccion`, `observacion`, `createdAtUtc`, `updatedAtUtc`, `concurrencyStamp`, `clientOperationId`, `captureSessionId`, `ownerUserId`, `qualityLabel`

Calidad: siempre recalculada con `NepsQualityCriteria` / `AlertEvaluator` (no 30/60).

## Idempotencia

Índice único filtrado `IX_SyncChangeLogs_Actor_ClientOperation` sobre `(ActorUserId, ClientOperationId)` donde `ClientOperationId` no vacío.

Carreras concurrentes con el mismo opId → una escritura gana; la otra lee Duplicate.

## Cursor (P5)

Sin cambios: `NextCursor = última Sequence examinada`. Política de privacidad del cursor global aceptable para intranet; requisito previo a multi-tenant / exposición externa.

## Históricos (política B)

Backfill administrativo **opcional y no ejecutado** en 2C. Diseño futuro: generar `RecordUpserted` idempotente para `NepRecord` sin ChangeLog, paginado, fuera del flujo Push/Pull.

## Idempotencia — semántica (ActorUserId + ClientOperationId)

Índice único filtrado `IX_SyncChangeLogs_Actor_ClientOperation`.

| Caso | Resultado |
|------|-----------|
| Mismo opId + mismo upsert (retry) | `Duplicate` / `ALREADY_PROCESSED` |
| Mismo opId + mismo tombstone (retry) | `Duplicate` / `ALREADY_PROCESSED` |
| Mismo opId, OperationType distinto (p. ej. Update→Delete) | `Invalid` / `CLIENT_OPERATION_REUSED` (no fingir éxito) |
| Mismo opId, mismo Update, payload distinto | `Duplicate` del primero (no reaplica) |
| Create y Update ambos son `RecordUpserted` | Si se reutiliza el opId del Create en un Update del **mismo** EntityId, el servidor no puede distinguir el intent → contrato cliente: **un ClientOperationId = una operación lógica** |

Clave actual (sin columna `OperationType` en ChangeLog): `(ActorUserId, ClientOperationId)` + validación de `ChangeType` + `EntityId` en lectura.

## Pendientes de consistencia sync

| Ruta | Estado |
|------|--------|
| Create online | ChangeLog (2B.1) |
| Update/Delete online | ChangeLog / tombstone (2C) |
| DeleteMany | Delega en DeleteAsync → tombstone |
| Importación productiva (`RecordImportService`) | ChangeLog por fila vía store atómico (DI) |
| ApplyCorrective | ChangeLog atómico (2C.1) |
| ClearAll | Bloqueado si existen SyncChangeLogs (2C.1); sin tombstones |
| Migración histórica Firestore | **Sin ChangeLog** (herramienta admin) |
| Backfill históricos | **No ejecutado** (política B) |

## 2C.1 — Cierre de coherencia de mutaciones

### ApplyCorrective y ChangeLog

- Ruta online (UI Alertas/Registros), **no** forma parte de `POST /api/sync/push`.
- `IAtomicNepRecordCreateStore.ApplyCorrectiveWithChangeLogAsync`: mutación + historial + `RecordUpserted` en **una** TX.
- Sin `ClientOperationId` artificial (mutación online; no inventar idempotencia Push).
- Fallback sin store (tests unitarios): `NepRecordRepository.UpdateAsync` como antes.

### Contrato de payload (réplica v1)

Incluidos en `RecordUpserted` (además del snapshot de captura):

- `accionCorrectiva`, `responsableRevision`, `revisadoPorSupervisor`, `fechaRevisionUtc`
- `updatedAtUtc`, `concurrencyStamp` (ya existían)

**Fuera de alcance** de la réplica v1:

- `HistorialAcciones` completo (auditoría server-side; el cliente usa los escalares de última revisión).

Calidad NEPS: sigue siendo `NepsQualityCriteria` / `AlertEvaluator` (no umbrales 30/60).

### ClearAll — contrato operativo

- Entry points: solo UI `Registros.razor` → `NepRecordService.ClearAllAsync` (permiso `ClearAllRecords`). **No hay API REST** dedicada.
- Roles típicos: Admin / SuperAdmin (matriz).
- Implementación: `ExecuteDelete` de CorrectiveActions + NepRecords; **no** genera tombstones ni ChangeLog.
- **Protección 2C.1:** si `SyncChangeLogs` tiene filas, `ClearAll` lanza `InvalidOperationException` (incompatible con offline-first).
- Confirmación UI advierte la incompatibilidad.
- Futuro (si debe coexistir con offline): Opción A (tombstone/N) o B (`RecordsCleared` + epoch) — **no** en 2C.1.

### Históricos

Política B intacta: registros sin ChangeLog no aparecen en Pull hasta mutación soportada o backfill admin separado. No ejecutado en 2C.1.

### Limitación SQL Server

La suite sync corre en SQLite. La implementación usa TX EF + token `ConcurrencyStamp` + índices filtrados portables; no hay harness SQL Server de sync en CI.

### Deliberadamente posterior (no 2D aquí)

- SyncEngine MAUI / LocalSession / bridge
- SignalR recovery, paginación 2F, catálogos
- `RecordsCleared` / tombstones masivos ClearAll
- Backfill histórico ejecutable
- Columna `OperationType` en ChangeLog (Create vs Update mismo opId)

## Endpoints

Sin cambios de ruta: `POST /api/sync/push`, `POST /api/sync/pull`.
