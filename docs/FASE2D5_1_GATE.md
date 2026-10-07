# FASE 2D.5.1 — Gate de integración Offline Update

## Alcance

Validar que la FASE 2D.5 (`UpdateRecord` offline) quedó integrada sin romper Create, Outbox, Push/Pull, idempotencia, Conflict/ConcurrencyStamp, tombstones (sin Delete UI), multiusuario, logout/re-login, reinicio, permisos, NEPS, atomicidad y UX de operaciones.

**No incluye:** capacidades nuevas, Delete UI, background sync, SignalR recovery, resolución de conflictos, LWW/merge, cambios de protocolo.

Base: commit `3c1faae` (`feature/fase-2d5-offline-update`).

## Arquitectura confirmada

```text
UI (OfflineEditRecordPage / Captura / Detalle)
  → OfflineCaptureService (LocalNepRecord + PendingOperation)
  → Outbox SQLite
  → SyncEngine (único motor Push/Pull)
  → HttpSyncApiClient → /api/sync/push|pull
  → SyncAppService / SyncPersistence
```

- Una sola ruta de sincronización (`ISyncEngine` / `SyncEngine`); no hay motor paralelo para Update.
- Auth real: cookie WebView; `LocalSession` solo snapshot UX.
- Permisos locales (`EditRecords`, SeesAll por `RoleCode`) son UX; servidor revalida.

## Matriz validada

| Área | Evidencia principal | Resultado |
|------|---------------------|-----------|
| Create offline / Pending / Accepted / Duplicate / Pull | `OfflineStoreCaptureTests`, `SyncEngineTests`, `SyncEngineValidation221Tests`, `OfflineBackendIntegrationGateTests` | OK |
| Create NEPS 18/19/46/55 | `OfflineStoreCaptureTests`, `OfflineBridgeAndSessionTests`, criterios oficiales | OK |
| Create sin sesión / post-logout | `OfflineStoreCaptureTests`, bridge/session | OK |
| Update solo sincronizado; bloqueo Create Pending; un Update/EntityId | `OfflineOfflineUpdateTests` + gate 2D.5.1 (cross-user) | OK |
| ClientOperationId nuevo + ExpectedConcurrencyStamp | `OfflineOfflineUpdateTests` | OK |
| Accepted / Duplicate / Conflict (local + snapshot, sin LWW/merge/retry) | `OfflineOfflineUpdateTests`, `SyncEngineTests` | OK |
| Pull Create/Update/tombstone; Update local + Pull EntityId | `SyncEngineTests`, `SyncEngineValidation221Tests`, `SyncUpdateDeleteTests` | OK |
| EditRecords / ownership / SeesAll / aislamiento Push | `OfflineOfflineUpdateTests` + `OfflineOfflineUpdateGate251Tests` | OK |
| Logout conserva Outbox; sin cookie no Push; re-login continúa | `OfflineOfflineUpdateTests`, `OfflineOfflineUpdateGate251Tests` | OK |
| Sesión expirada ≠ auth válida | `OfflineOfflineUpdateGate251Tests` | OK |
| Sin password/cookie/token en LocalSession | `OfflineStoreCaptureTests` | OK |
| Reinicio Pending/Conflict/Synced/SyncState | `OfflineOfflineUpdateTests`, `OfflineOfflineUpdateGate251Tests` | OK |
| Atomicidad LocalNepRecord + Outbox | `OfflineOfflineUpdateTests` | OK |
| NEPS vía `NepsQualityCriteria`; AlertasActivas no cambia calidad | `OfflineOfflineUpdateTests`, `OfflineOfflineUpdateGate251Tests`, `NepsQualityCriteriaTests` | OK |
| Idempotencia / CLIENT_OPERATION_REUSED / Update tras tombstone | `SyncUpdateDeleteTests`, `OfflineOfflineUpdateGate251Tests`, `SyncEngineTests` | OK |
| UX operaciones (lista/detalle/conflict) | `OfflineOperationsUxTests`, `OfflineSyncUxValidation231Tests` | OK |
| Tombstones protocolo sin Delete UI | Contrato existente + SyncEngine; UI sin Delete | OK |

## Hallazgo corregido en este gate

**Defecto 2D.5:** `EvaluateEditEligibilityAsync` filtraba operaciones bloqueantes por `UserId` de la sesión actual. Un Supervisor/SeesAll podía encolar un segundo `UpdateRecord` del mismo `EntityId` mientras existía un Update Pending de otro usuario en el mismo dispositivo.

**Corrección mínima:** el bloqueo Pending/Sending/Conflict se evalúa por `LocalNepRecordId` / `TargetServerRecordId` sin filtrar por `UserId`.

No se modificó protocolo Push/Pull, autenticación, NEPS ni tombstones.

## Resultados de ejecución

| Chequeo | Resultado |
|---------|-----------|
| Tests nuevos (gate) | **10** en `OfflineOfflineUpdateGate251Tests` |
| Tests específicos Offline/Update | 14 (`OfflineOfflineUpdateTests`) + 10 gate = **24/24** |
| Suite completa | **394/394** Passed, 0 failed |
| Web Release | OK, 0 warnings / 0 errors |
| Android Debug | OK, 0 warnings / 0 errors |
| Android E2E runtime | no ejecutado (sin dispositivo/APK en este gate) |

## Confirmación de no cambios de protocolo

No se alteraron:

- `/api/sync/push`, `/api/sync/pull`
- `SyncProtocolVersions` / protocolo v1
- autenticación / autorización servidor
- semántica `ConcurrencyStamp` / cursor / tombstones / ClearAll
- SignalR / background sync
- criterios `NepsQualityCriteria`

## Riesgos residuales

1. **Android E2E no ejecutado** en este entorno (solo build Debug).
2. **SeesAll UX** usa códigos de rol locales (`Admin`/`Supervisor`/`SuperAdmin`/`SuperAdministrador`), no el flag servidor `SeesAllRecords` como claim tipado; el servidor sigue siendo autoridad en Push.
3. **Resolución de Conflict** sigue fuera de alcance (solo “Requiere revisión”).
4. Gate Bridge↔Backend SQL Server real (2D.4.1) no se reejecutó aquí; cobertura Offline Update usa FakeApi + tests de servidor ya existentes (`SyncUpdateDeleteTests`).

## Pruebas no ejecutadas

- APK install / flujo manual Login → Create → Sync → Edit → Update → Sync → Conflict.
- Dispositivo físico/emulador Android runtime.
- Re-gate SQL Server E2E completo de 2D.4.1.

## Commit resultante

Rama: `feature/fase-2d5-offline-update`  
Commit: `d350215` — `test: FASE 2D.5.1 — gate Offline Update + bloqueo Update por EntityId`  
Sin push / sin PR.
