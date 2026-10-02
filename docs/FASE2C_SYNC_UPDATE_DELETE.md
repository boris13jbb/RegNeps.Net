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

## Pendientes de consistencia sync

| Ruta | Estado 2C |
|------|-----------|
| Create online | ChangeLog (2B.1) |
| Update/Delete online | ChangeLog / tombstone |
| DeleteMany | Delega en DeleteAsync → tombstone |
| Importación | ChangeLog por fila vía store atómico |
| ApplyCorrective | **Pendiente** (muta sin ChangeLog dedicado) |
| ClearAll | **Pendiente** (borrado masivo sin tombstones por fila) |
| Backfill históricos | **No ejecutado** (política B) |

Hasta resolver ApplyCorrective/ClearAll/backfill, el sistema no debe considerarse 100 % sync-consistente para esas rutas.

## Endpoints

Sin cambios de ruta: `POST /api/sync/push`, `POST /api/sync/pull`.
