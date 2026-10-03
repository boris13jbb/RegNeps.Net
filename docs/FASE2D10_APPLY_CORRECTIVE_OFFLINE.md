# FASE 2D.10 — ApplyCorrective offline

## Resumen

`ApplyCorrective` es una operación de sincronización **explícita e independiente** de `UpdateRecord`. Permite aplicar una acción correctiva offline sobre un `NepRecord` ya vinculado al servidor, persistirla atómicamente en SQLite/Outbox y enviarla vía `/api/sync/push`.

Estado de cierre previsto: **PASS CON WARNINGS** (SQL Server físico y Android E2E de entorno, si no disponibles).

## Por qué ApplyCorrective NO es UpdateRecord

| Aspecto | UpdateRecord | ApplyCorrective |
|---------|--------------|-----------------|
| Dominio | Mutación de captura (Telar, Neps, Tela, Lote, Turno, etc.) | Solo parámetros de corrección |
| Permiso | `EditRecords` | `ApplyCorrectiveAction` |
| Payload | Campos de captura | `Accion`, `Responsable`, `MarcarRevisado` |
| Efecto en calidad | Puede cambiar Neps → recalcula calidad | **No modifica Neps**; calidad se conserva |
| Historial | No escribe `CorrectiveActionEntry` | Añade entrada de historial + escalas de revisión |
| Auditoría / Outbox / ChangeLog | Identidad `UpdateRecord` | Identidad `ApplyCorrective` |
| Conflictos Keep Local | Reencola Update | Reencola ApplyCorrective (nunca Update) |

Reutilizar `UpdateRecord` ocultaría la semántica, rompería permisos, auditoría e idempotencia diferenciada.

## Semántica exacta

### Modifica

- `AccionCorrectiva`
- `ResponsableRevision`
- `RevisadoPorSupervisor` / `FechaRevision` (si `MarcarRevisado`)
- Entrada en `CorrectiveActions` / `HistorialAcciones`
- `ConcurrencyStamp` (nuevo)
- `UpdatedAt`
- `SyncChangeLog` `RecordUpserted` (snapshot completo)

### No modifica

- Telar, Neps, Tela, LoteTrama, Turno, Operario, LineaProduccion, Observacion
- Propietario / permisos
- Calidad vía umbrales legacy 30/60

### Operación de sincronización

- Outbox: `OfflineOperationType.ApplyCorrective`
- Wire Push: `SyncConstants.OperationApplyCorrective` (`"ApplyCorrective"`)
- ProtocolVersion: **1** (aditivo; la constante ya existía; no requiere bump)

## Payload

```json
{
  "entityId": "<guid servidor>",
  "accion": "...",
  "responsable": "...",
  "marcarRevisado": true
}
```

`ExpectedConcurrencyStamp`, `ClientOperationId`, `CaptureSessionId`, `DeviceId` viajan en el envelope `SyncOperationDto` / `PendingOperation` (no se duplican campos de Update).

El cliente **no** puede enviar Telar/Neps en este payload con efecto; el servidor ignora cualquier campo ajeno a la corrección.

## Permisos

- UX local: `ApplyCorrectiveAction` en snapshot de sesión (solo UX).
- Servidor: revalida `AppPermission.ApplyCorrectiveAction` + ownership / `SeesAll`.
- Sin permiso → `Forbidden` (`CORRECTIVE_FORBIDDEN`).
- No propietario sin SeesAll → `Forbidden` (`OWNERSHIP`).

## Concurrencia

- Push exige `ExpectedConcurrencyStamp`.
- Stamp correcto → mutación + nuevo stamp + ChangeLog atómicos.
- Stamp obsoleto → `Conflict` con `ServerConcurrencyStamp` + `ServerSnapshot` (sin merge / sin LWW).

## Idempotencia

Clave existente: `(CreatedByUserId / ActorUserId, ClientOperationId)`.

Retry tras éxito → `Duplicate`; una sola mutación de dominio y un solo ChangeLog efectivo.

## ChangeLog / Pull

- Tipo: `RecordUpserted` con snapshot canónico (incluye escalas de revisión).
- Otros clientes actualizan `LocalNepRecord` por Pull; **no** reejecutan ApplyCorrective ni generan Outbox.
- `HistorialAcciones` completo queda fuera de réplica v1 (igual que Update); escalas sí se replican.

## Conflictos (reutiliza 2D.7)

| Escenario | Keep Server | Keep Local | Edit & Retry |
|-----------|-------------|------------|--------------|
| Correctiva vs update servidor (UpdateUpdate) | Sí | Sí (reencola ApplyCorrective) | Sí (acción/responsable) |
| Correctiva vs eliminación (UpdateDelete) | Sí | **Prohibido** | **Prohibido** |

Keep Local / Edit & Retry **nunca** convierten la corrección en `UpdateRecord`.

## Calidad NEPS

Tras correctiva, calidad sigue siendo `NepsQualityCriteria` / `AlertEvaluator` sobre Neps (sin cambio). Alertas (`AlertasActivas`) separadas de calificación.

## Atomicidad offline

Transacción SQLite única: mutación de `LocalNepRecord` (escalares correctiva) + `PendingOperation`. Rollback si falla cualquiera.

## UX MAUI

- Sección correctiva en `OfflineEditRecordPage` (permiso UX, stamp, Outbox, estado pendiente).
- Conflictos en `OfflineConflictResolvePage` con campos de correctiva para Edit & Retry.
- Sin segunda pantalla de conflictos.

## Online

`NepRecordService.ApplyCorrectiveAsync` continúa usando `ApplyCorrectiveWithChangeLogAsync` (misma lógica de dominio que Push; online sin stamp/clientOp obligatorios).

## Tests

- `SyncApplyCorrectiveTests` — dominio Push, Duplicate, Conflict, Forbidden, Pull, atomicidad, calidad.
- `OfflineApplyCorrectiveTests` — Outbox, rollback, elegibilidad, conflictos 2D.7, E2E Push.

## Limitaciones

- SQL Server físico: validación de entorno pendiente si no hay instancia.
- Android E2E: solo si hay emulador/dispositivo.
- Historial completo no se replica en Pull v1.
- Sin ClearAll / SignalR / paginación / cifrado SQLite en esta fase.

## Decisiones descartadas

1. Alias UpdateRecord para correctiva — rechazado (semántica distinta).
2. Merge automático / LWW — rechazado.
3. ProtocolVersion bump — innecesario (operación aditiva ya nombrada en constantes).
4. Nueva pantalla de conflictos — rechazado (reutilizar 2D.7).
5. Keep Local sobre EntityId eliminado — prohibido (sin Restore).
