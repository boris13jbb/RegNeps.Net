# FASE 2B — Backend durable de sincronización (Push / Pull)

## Alcance

- Endpoints `POST /api/sync/push` y `POST /api/sync/pull`
- Tabla `SyncChangeLogs` con secuencia monotónica
- Idempotencia CreateRecord (`ClientOperationId` por usuario)
- Autorización en servidor (claims + matriz de permisos)
- **No** incluye: SyncEngine MAUI, LocalSession bridge, Update/Delete sync, tombstones, SignalR recovery

Base revisada 2A: `aa5f0c5`.

## Arquitectura

| Capa | Pieza |
|------|--------|
| Contrato | `RegNeps.Application/Sync/*` (DTOs versionados, no entidades EF) |
| Orquestación | `SyncAppService` |
| Persistencia atómica | `ISyncPersistence` / `SyncPersistence` (un DbContext + TX) |
| HTTP | `RegNeps.Web/Sync/SyncApiEndpoints.cs` |
| Dominio | `SyncChangeLog`, `SyncConstants` |

`NepRecordRepository` usa `IDbContextFactory` (contexto por operación). Por eso el Push atómico **no** encadena `NepRecordService.AddAsync` con otro SaveChanges: `SyncPersistence` escribe `NepRecord` + `SyncChangeLog` en la misma transacción y reutiliza `NepRecordService.ValidateCreateRequest` + las mismas reglas de ownership/calidad.

## Protocolo

- `SyncProtocolVersion = 1` (`SyncConstants.ProtocolVersion`)
- El servidor **ignora** UserId/Role/permisos/timestamps del cliente como fuente de verdad
- `DeviceId` es auditoría/protocolo, **no** identidad
- Clasificación NEPS: recalculada con `NepsQualityCriteria` / `AlertEvaluator`

### Push

```
PushRequest { ProtocolVersion, DeviceId, Operations[] }
SyncOperation { ClientOperationId, OperationType, Payload, CaptureSessionId, ExpectedConcurrencyStamp?, ClientCreatedAtUtc? }
```

v1 solo soporta `CreateRecord`. Otros tipos → `Invalid` / `OPERATION_TYPE_UNSUPPORTED`.

### Pull

```
PullRequest { ProtocolVersion, DeviceId, Cursor, PageSize? }
PullResponse { Changes, NextCursor, ServerTimeUtc, HasMore }
```

## Resultados Push

| Resultado | Significado |
|-----------|------------|
| Accepted | Operación nueva procesada |
| Duplicate | Ya existía `(CreatedByUserId, ClientOperationId)` |
| Forbidden | Sin permiso / mismatch de sesión |
| Invalid | Payload/protocolo/tipo no soportado |
| TransientError | Error temporal (sin filtrar detalle interno) |

## Idempotencia

- Clave lógica: **`(CreatedByUserId, ClientOperationId)`** — índice único filtrado existente `IX_NepRecords_CreatedBy_ClientOperation`
- Suficiente para CreateRecord v1; **DeviceId no forma parte** de la clave
- **No** se añadió `ProcessedClientOperations` en 2B: el `NepRecord` + fila `SyncChangeLog` bastan para respuesta determinista Duplicate y auditoría del cambio
- Reintentos tras respuesta perdida → `Duplicate`, 0 filas adicionales, 0 cambios adicionales

## Transacción Push (CreateRecord)

```
BEGIN
  comprobar idempotencia
  INSERT NepRecord
  INSERT SyncChangeLog (RecordUpserted)
  COMMIT
```

Fallo en cualquier punto → `ROLLBACK` (probado con trigger que rechaza el ChangeLog).

## SyncChangeLog

Campos: `Sequence` (IDENTITY/AUTOINCREMENT), `EntityType`, `EntityId`, `ChangeType`, `OccurredAtUtc`, `ActorUserId`, `OwnerUserId`, `ClientOperationId`, `DeviceId`, `PayloadJson` (snapshot mínimo).

Índices: Sequence, (EntityType, EntityId), (OwnerUserId, Sequence), ClientOperationId.

## Cursor / NextCursor

`SyncChangeLog` es global. Pull:

1. Escanea `Sequence > Cursor` ASC
2. Filtra por autorización (`SeesAllRecords` o `OwnerUserId`)
3. Devuelve hasta `PageSize` cambios **autorizados**
4. **`NextCursor` = última Sequence examinada** (incluye las no autorizadas saltadas)

Caso:

```
100 (A puede ver) → 101 (A no) → 102 (A puede)
Pull A → Changes 100,102 ; NextCursor = 102
```

Así el cliente no se bloquea intentando re-leer 101.

## Autorización Pull

Requiere `ViewRecords` o `CaptureRecords`.  
No se hace `SELECT *` sin filtro: cada cambio se autoriza; Operario solo ve `OwnerUserId` propio (o ExternalUserId ligado).

## PageSize

- Default: 100  
- Min: 1  
- Max: 500 (`SyncConstants.MaxPageSize`)  
Valores mayores se clampean; nunca se acepta un page arbitrario enorme.

## Migraciones / despliegue

- Modelo EF: `RegNepsDbContext` + entidad `SyncChangeLog`
- Parches aditivos idempotentes en `DatabaseInitializer` (SQLite y SQL Server) — **camino de arranque actual** (`EnsureCreated` + patches)
- Migración formal documentada: `Infrastructure/Migrations/20261002190000_AddSyncChangeLog.cs`  
  No se activa `Migrate()` automático en 2B para no romper el flujo EnsureCreated existente.

## Observabilidad

Logs: UserId, DeviceId, ClientOperationId, Result, DurationMs, CorrelationId (`TraceIdentifier`).  
No se registran passwords, cookies ni tokens.

## Fuera de 2B

UI offline, SyncEngine, retry/backoff, catálogos completos, Update/Delete, conflictos, SignalR recovery.
