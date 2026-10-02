# FASE 2D.5 — Update offline de registros existentes

## Alcance

Permite editar **offline** un registro que ya existe localmente **y** tiene identidad de servidor (`ServerRecordId` + `ConcurrencyStamp`), generando una `PendingOperation` de tipo `UpdateRecord` que el `SyncEngine` existente envía por Push.

**Incluye**

- UI de edición (`OfflineEditRecordPage`) desde Captura (lista editables) y Detalle de operación.
- Servicio `OfflineCaptureService.UpdateRecordAsync` atómico (LocalNepRecord + Outbox).
- Reutilización de protocolo v1 / `SyncUpdateRecordPayload` / `SyncEngine`.
- Conflict: conserva local + snapshot servidor; sin resolución automática.

**No incluye (fuera de 2D.5)**

- Delete offline desde UI.
- Resolución de conflictos (mantener local/servidor/merge).
- Background sync / SignalR recovery / sync automático.
- Last-Write-Wins / merge automático.
- Cambios de `/api/sync/push|pull` salvo bugs indispensables (ninguno en esta fase).

## Campos editables (modelo real)

Alineados con `SyncUpdateRecordPayload` / captura online:

| Campo | Editable offline |
|-------|------------------|
| Telar | Sí (obligatorio) |
| Neps | Sí (obligatorio; recalcula calidad) |
| Tela | Sí |
| LoteTrama | Sí (normaliza a mayúsculas; vacío → prefijo oficial) |
| Turno | Sí |
| Operario | Sí |
| LineaProduccion | Sí |
| Observacion | Sí |

**Inmutables / no editables desde UI ni payload de negocio**

- Id local, `ServerRecordId` / EntityId (solo lectura; se usa como destino).
- `CreatedAtUtc`, `CreatedByUserId` / `OwnerUserId` / `UserId`.
- `ClientOperationId` del Create original (el Update usa **otro**).
- `ConcurrencyStamp` (se envía como `ExpectedConcurrencyStamp`; no se edita a mano).
- `UpdatedAtUtc` (lo actualiza el servicio al guardar).
- Calidad / `QualityLabel` (derivada de `Neps` vía `NepsQualityCriteria` / `AlertEvaluator`).
- Campos calculados (`MtsCalculados` = Neps / 0.09).

## Identidad

| Concepto | Uso en Update |
|----------|----------------|
| Local ID | `LocalNepRecord.Id` — clave de edición en UI |
| `ServerRecordId` / EntityId | Obligatorio; sin él → “Pendiente de sincronización” |
| `ClientOperationId` | **Nuevo** Guid por cada Update (operación lógica distinta del Create) |
| `ExpectedConcurrencyStamp` | Stamp local conocido al editar; va en `PendingOperation` |

Create Pending (sin `ServerRecordId` o Create Pending/Sending) → **Update bloqueado**.

Máximo **un** Update Pending/Sending/Conflict por EntityId → segundo Update bloqueado.

## Flujo

```text
UI (Editar)
  → OfflineCaptureService.UpdateRecordAsync
  → TX: LocalNepRecord actualizado + PendingOperation UpdateRecord
  → estado Pending
  → SyncEngine Push (manual)
  → Accepted | Duplicate | Conflict | Forbidden/SyncError
  → Pull posterior (sin LWW sobre edición en Conflict/Pending)
```

## Atomicidad

En una transacción SQLite:

- éxito: LocalNepRecord modificado **y** Outbox Update creados;
- fallo: ninguno (rollback + `ChangeTracker.Clear`).

## Conflictos

- Sin LWW, sin merge, sin retry automático de Conflict.
- Local preservado; `ConflictServerSnapshotJson` + `ConflictServerConcurrencyStamp` preservados.
- UI: “Requiere revisión” (botón deshabilitado; sin acciones Mantener local/servidor).

## Permisos

- UI/UX: snapshot `EditRecords` (+ ownership o roles SeesAll: Admin/Supervisor/SuperAdmin).
- Autoridad: servidor (`EditRecords` real). Forbidden → `SyncError` sin alterar permisos locales.

## Logout / reinicio

- Logout: limpia `LocalSession`; conserva `LocalNepRecord` y Pending Update.
- Reinicio: persisten modificación, Outbox, `ClientOperationId`, `ExpectedConcurrencyStamp`, `ServerRecordId`, estado Pending/Conflict.

## NEPS

Fórmula: `NEPS/m² = NEPS / 0.09` (`NepsConstants.TestLengthM`).

Umbrales oficiales (no 30/60):

- 18 → OK  
- 19 / 45 → Mención  
- 46 / 54 → Crítico - Realizar Ajuste  
- 55 → 2da Calidad  

## Archivos clave

- `OfflineCaptureService` — Create + Update
- `OfflineUpdateRecordRequest` / `UpdateRecordPayload`
- `OfflineEditRecordPage`
- `OfflineCapturePage` (lista editables)
- `OfflineOperationDetailPage` (botón Editar si elegible)
- Tests: `OfflineOfflineUpdateTests`
