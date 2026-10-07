# FASE 2D.4.1 — Gate de integración Offline ↔ Backend

## Flujo auditado (sin rediseño)

```
WebView Login → RegNeps.Auth (cookie runtime)
  → MauiWebViewCookieProvider
  → HttpSyncApiClient → /api/sync/push|pull
  → SyncAppService → SyncPersistence → NepRecord + SyncChangeLog
  → Pull → SyncEngine → SQLite (LocalNepRecord / PendingOperation / SyncState)
  → OfflineSyncUx / OfflineOperationsUx
```

LocalSession = snapshot UX (TTL 72h). Cookie = autoridad de autenticación.

## Harness de esta fase

| Capa | Proveedor en gate |
|------|-------------------|
| OfflineStore | SQLite archivo temporal |
| Backend SyncAppService | SQLite `:memory:` (mismo que FASE 2B/2C) |
| Puente | `BridgeApi` in-process (DTOs OfflineStore ↔ Application) |
| SQL Server | **NO ejecutable** — sin LocalDB/harness CI (gap ambiental) |
| Android APK E2E | **NO ejecutado** — solo build |

## Bug corregido

**Pull sobrescribía campos locales tras Conflict** (LWW implícito vía Pull).

- Causa: `ApplyRecordUpserted` solo preservaba negocio si había `Pending`; `Conflict` caía al overwrite.
- Fix: tratar `Conflict` como divergencia local en revisión (actualizar stamp/snapshot servidor, **no** campos de negocio).
- Regresión: `Gate_Conflict_No_Lww_Ux_Requires_Review_No_ChangeLog_For_Rejected`.

## Observabilidad

- `SyncAppService` ya registra usuario/device/ClientOperationId/resultado/duración/correlation (sin cookie).
- `SyncEngine` no emite ILogger estructurado todavía — warning menor, no blocker.

## Warnings conocidos (sin cambio de arquitectura)

1. SQL Server E2E no disponible en CI/local.
2. Android APK E2E no ejecutado.
3. LocalSession TTL 72h vs cookie 12h.
4. UI offline Create-only.
5. ServerBaseUrl ↔ Entry deben coincidir.
