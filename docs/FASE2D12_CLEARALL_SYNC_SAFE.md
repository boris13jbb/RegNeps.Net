# FASE 2D.12 — ClearAll sync-safe (N tombstones)

## Semántica

`ClearAll` administrativo online elimina **todos** los `NepRecord` de la tabla y, por cada uno, escribe un `SyncChangeLog` con:

* `EntityType = NepRecord`
* `ChangeType = RecordDeleted`
* payload tombstone (`id`, `ownerUserId`, `deletedAtUtc`, `lastConcurrencyStamp`)
* `ActorUserId` = administrador que ejecutó ClearAll
* `OwnerUserId` = `CreatedByUserId` del registro (o actor si faltaba)
* `ClientOperationId` = null (admin, no Push)
* `DeviceId` = null

No existe `RecordsCleared`. No hay Outbox ClearAll. No hay ClearAll offline/MAUI.

## Alcance

Global de tabla (igual que antes). No hay ClearAll por usuario/telar/turno/fecha/sesión.

## Permiso

Obligatorio: `AppPermission.ClearAllRecords`.  
`SeesAll` **no** sustituye a `ClearAllRecords`.

## Flujo

```text
UI Registros → NepRecordService.ClearAllAsync
  → IAtomicNepRecordCreateStore.ClearAllWithTombstonesAsync
      TX única:
        load all NepRecords
        for each: SyncChangeLog RecordDeleted + Remove(entity)
        SaveChanges  (CorrectiveActions por cascade FK)
      commit
```

Sin store atómico (solo pruebas legacy): fallback a `NepRecordRepository.ClearAllAsync` sin ChangeLog.

## Atomicidad

Una sola transacción para N tombstones + N deletes.  
Si falla (p. ej. `DbUpdateConcurrencyException` por stamp rotado concurrentemente): **rollback completo** — ni tombstones parciales ni filas a medias.

No se usa batching en 2D.12: la consistencia del cursor prima sobre throughput.

## CorrectiveActions

Relación `NepRecord.HistorialAcciones` con `OnDelete(Cascade)`.  
Al `Remove` del padre, EF elimina las correctivas en la misma TX.  
No se conserva historial de correctivas tras ClearAll (igual que el vaciado físico previo).

## Concurrencia

ClearAll **no** exige `ExpectedConcurrencyStamp` por fila.  
Lee el stamp actual al cargar y lo guarda en el tombstone (auditoría).  
Si durante la TX un registro cambia su concurrency token, `SaveChanges` puede lanzar → rollback + mensaje de reintento.

## Cursor / Pull — ejemplo `X → ClearAll → Y`

```text
Cliente LastPulledSequence = X
ClearAll genera RecordDeleted con Sequence X+1 … X+N  (Y = X+N)
Pull(X), PageSize=P:
  página 1: cambios (X, X+P], HasMore=true, NextCursor=X+P
  …
  última: hasta Y, HasMore=false
SyncEngine.ApplyDeleteAsync marca LocalNepRecord.IsDeleted
  y Pending → Conflict ENTITY_DELETED (no toca Conflict ya existente)
```

`X → ClearAll → Create(Z) → Pull(X)`:

1. aplica tombstones (réplica vieja muere);
2. aplica `RecordUpserted` de Z;
3. Z permanece.

## Offline / Outbox

| Estado | Tras Pull de tombstones / Push |
|--------|--------------------------------|
| Create Pending sin ServerRecordId | Sobrevive; puede Accepted post-clear |
| Update Pending | Conflict / ENTITY_DELETED |
| ApplyCorrective Pending | Conflict / ENTITY_DELETED |
| Delete Pending | Idempotente o Conflict DeleteDelete |
| Conflict previo | No se cancela automáticamente |

## Idempotencia

No es operación Push. Segundo ClearAll sobre tabla vacía = no-op (0 tombstones nuevos).  
No re-tumbstonea EntityIds ya inexistentes.

## Por qué RecordsCleared queda fuera

* Clientes que ignoran ChangeTypes desconocidos avanzarían el cursor sin limpiar.
* N tombstones reutiliza Delete/DeleteMany/SyncEngine sin bump de protocolo.
* Compactación/RecordsCleared queda para una fase de retención futura.

## Escalabilidad

Volúmenes actuales típicos (intranet) caben en una TX.  
Riesgo futuro: N muy grande → timeout SQL Server; entonces batches **solo** con diseño de cursor documentado (no en 2D.12) o compactación.

## SQL Server

Diseño compatible (IDENTITY Sequence, cascade, TX).  
Validación física pendiente si el entorno no tiene instancia.

## Limitaciones

* Sin compactación de ChangeLog.
* Sin progreso UI por lotes.
* Sin ClearAll offline.
* Android E2E / SQL Server físico pueden quedar como warnings de entorno.

## Criterios 2D.12

Cumplidos cuando: N tombstones atómicos, Pull pagina sin saltos, Create offline sobrevive, permisos OK, sin `RecordsCleared`, regresiones verdes.
