# FASE 2D.7 — Implementación: resolución explícita de Conflict

Rama: `feature/fase-2d5-offline-update`  
Base gate: `39b86d0`  
Diseño: `docs/FASE2D7_CONFLICT_DESIGN.md`

---

## 1. Decisiones implementadas

| # | Decisión | Implementación |
|---|----------|----------------|
| 1 | Update vs Delete | `KeepLocal`/`EditAndRetry` rechazados en `UpdateDelete`; Keep Server → tombstone local |
| 2 | Cancel = Keep Server | Una sola API `KeepServerAsync`; textos UX: «Aceptar versión del servidor» / «Aceptar eliminación» / «Descartar mi eliminación» |
| 3 | Persistencia | Sin migración; kind derivado de `OperationType` + `LastServerErrorCode` + forma JSON snapshot |
| 4 | Auditoría | Keep Local Accepted → `SyncChangeLog` (mutación servidor). Keep Server → **solo local** (`Cancelled` + `LastError` `Resolved:KeepServer;by=…;at=…;kind=…` + snapshot/stamp/Payload conservados). **No** es ChangeLog de resolución en servidor. |
| 5 | Autorización | Autor **o** SeesAll (`Admin`/`Supervisor`/`SuperAdmin`/`SuperAdministrador`); Edit/Delete permission UX; Forbidden en Push → `SyncError` |
| 6 | Delete/Delete | Sin pantalla artificial; clasificado `DeleteDelete`; solo Keep Server |

---

## 2. Fuera de alcance (esta fase)

- Create de recuperación con nuevo EntityId  
- `Restore` / resurrección  
- Merge automático campo-a-campo  
- Change type `ConflictResolved` en protocolo Sync  
- ApplyCorrective en UI de resolución  
- LWW / auto-aceptar Conflict  
- Push / PR / 2D.8

---

## 3. Modelo final

```text
Conflict (PendingOperation + LocalNepRecord)
  → GetViewAsync → OfflineConflictKind + AllowedActions
  → KeepServer | KeepLocal | EditAndRetry
       KeepServer: alinear réplica + Cancelled (sin Push)
       KeepLocal / EditAndRetry: Cancelled original + NUEVA PendingOperation
         (nuevo ClientOperationId, ExpectedConcurrencyStamp = stamp servidor)
  → SyncEngine.Push → Accepted | Conflict | Forbidden | Duplicate | …
```

**Snapshot JSON v1:** `ClientNepRecordSnapshot` (activo) o `ClientNepRecordDeletedSnapshot` (tombstone), tal como lo persiste SyncEngine.

**Marcadores `LastError`:** `Resolved:KeepServer` | `Resolved:KeepLocal` | `Resolved:EditAndRetry` (+ by, at, kind, newClientOp).

---

## 4. Estrategia por tipo

| Kind | Keep Server | Keep Local | Edit & Retry |
|------|-------------|------------|--------------|
| UpdateUpdate | Snapshot → local Synced | Nueva Update | Nueva Update editada (base UI = servidor) |
| UpdateDelete | Tombstone local | **No** | **No** (mensaje: hace falta Create futuro) |
| DeleteUpdate | Snapshot vivo | Nueva Delete | No |
| DeleteDelete | Tombstone local | No | No |

---

## 5. Invariantes (verificados en tests)

1–4. Sin sobrescritura remota silenciosa; sin pérdida local sin decisión; sin LWW; stamp respetado.  
5–6. Nueva operación + nuevo ClientOperationId; idempotencia doble click / AlreadyResolved.  
7. Permiso perdido → Unauthorized local; Forbidden servidor → SyncError.  
8–9. Tombstone y no resurrección en UpdateDelete.  
10. Quality vía `AlertEvaluator`/`NepsQualityCriteria` desde Neps.  
11–12. Una Pending efectiva; metadata conservada tras rechazo.

---

## 6. Archivos principales

- `src/RegNeps.OfflineStore/Services/ConflictResolutionService.cs`  
- `src/RegNeps.OfflineStore/Services/ConflictKindClassifier.cs`  
- `src/RegNeps.OfflineStore/Models/ConflictResolutionModels.cs`  
- `src/RegNeps.OfflineStore/Enums/OfflineConflictKind.cs`  
- `src/RegNeps.Mobile/OfflineConflictResolvePage.xaml(.cs)`  
- `src/RegNeps.Mobile/OfflineOperationDetailPage` — botón «Resolver conflicto»  
- `tests/RegNeps.Tests/OfflineConflictResolutionTests.cs`

---

## 7. Tests

Clase: `OfflineConflictResolutionTests` (16 casos): Keep Server/Local/Edit&Retry, segundo Conflict, UpdateDelete, DeleteUpdate, DeleteDelete, permiso perdido, SeesAll, doble click, Duplicate tras timeout, Forbidden, persistencia reinicio, clasificación UI.

---

## 8. Riesgos residuales

- Keep Server no genera `SyncChangeLog` servidor (solo Outbox local).  
- Create-recovery no disponible: mediciones locales en UpdateDelete se pierden si el usuario acepta eliminación.  
- SQL Server físico / Android E2E runtime no verificados en este entorno.  
- SeesAll puede Keep Server Outbox ajena en el mismo dispositivo; Push de Keep Local usa `UserId` de la sesión actual.

---

## 9. Guía de prueba manual (MAUI)

1. Generar Conflict Update/Update (dos dispositivos o FakeApi / sync con stamp stale).  
2. Operaciones → detalle → **Resolver conflicto**.  
3. Verificar local vs servidor y diferencias.  
4. **Aceptar versión del servidor** → op Cancelada, local = servidor, sin Push.  
5. Repetir y **Mantener mis cambios** → nueva Pending, sync → Accepted o nuevo Conflict.  
6. UpdateDelete: solo «Aceptar eliminación»; no botón Restore.  
7. Revocar EditRecords en sesión → Mantener cambios → rechazo; Conflict intacto.
