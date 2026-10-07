# FASE 2E — Recuperación SignalR + Pull

## Resumen

Regla arquitectónica:

> **SignalR notifica; Pull sincroniza.**

Tras una desconexión/reconexión de SignalR (o restauración de red), el cliente **no asume** que recibió eventos. Solicita recuperación y ejecuta `POST /api/sync/pull` desde el **cursor local** (`SyncState.LastPulledSequence` ↔ `SyncChangeLog.Sequence`).

Estado de cierre previsto: **PASS CON WARNINGS** (Android E2E y SQL Server físico de entorno, si no disponibles).

## Arquitectura final

```text
SignalR connected
      ↓
operación normal (alertas críticas; señal SyncRecoverySuggested)
      ↓
SignalR disconnected
      ↓
WithAutomaticReconnect()
      ↓
reconnected / connectivity-restored / hub-connected
      ↓
ISyncRecoveryCoordinator.RequestRecovery(reason)
      ↓
coalescing (máx. 1 Pull activo; trigger durante Pull → 1 follow-up)
      ↓
ISyncEngine.PullAsync()  (NO Push; NO mutar Outbox por SignalR)
      ↓
POST /api/sync/pull (páginas ASC hasta HasMore=false)
      ↓
aplicar RecordUpserted / RecordDeleted / Catalog* (reglas existentes)
      ↓
persistir LastPulledSequence = NextCursor
```

### Responsabilidades

| Componente | Hace | No hace |
|------------|------|---------|
| SignalR (`/hubs/alerts`) | Notifica `CriticalAlertReceived` y `SyncRecoverySuggested` | Transportar change logs, snapshots, cursores, ACK de sync |
| `MauiSyncHubRecoveryService` | Conecta hub con cookie WebView; en reconnect pide recovery | Insertar/borrar `LocalNepRecord`, tocar Outbox/SyncState |
| `SyncRecoveryCoordinator` | Coalesce triggers → un Pull | Interpretar payloads SyncChangeLog |
| `ISyncEngine.PullAsync` | Pull-only desde cursor local | Avanzar cursor por notificación SignalR |
| `/api/sync/pull` | Única fuente de verdad de cambios | Depender de SignalR |

## Cursor

- Fuente de verdad de recuperación: `SyncState.LastPulledSequence`.
- Solo avanza tras aplicar una página completa de Pull (transacción existente).
- **Nunca** avanza por haber recibido un evento SignalR.
- No existen cursores paralelos, `LastSignalRSequence`, ni timestamps como sustituto de `Sequence`.
- Cursor no retrocede: si otra corrida avanzó el cursor en TX, la página concurrente se aborta.

## Deduplicación / coalescing

`SyncRecoveryCoordinator` (singleton) + `ManualSyncGate` compartido con sync manual:

1. Varios `RequestRecovery` próximos → un runner.
2. Pull activo + nuevo trigger → `_pullRequested=true` (no cola infinita).
3. Al terminar el Pull, si quedó pedido, se ejecuta **un** Pull adicional.
4. Como máximo un Pull de recuperación concurrente por contexto (gate).

Un fallo de SignalR **no** marca operaciones Outbox como `SyncError`. Pending sigue Pending hasta Push.

## Estados de conectividad (cliente)

| Fase | Significado |
|------|-------------|
| `Idle` | Sin recovery en curso |
| `Disconnected` | Hub/red caídos (informativo) |
| `Reconnecting` | `WithAutomaticReconnect` |
| `PendingPull` | Trigger recibido; Pull pendiente |
| `Synchronizing` | Pull en ejecución |
| `Synchronized` | Último Pull drenó páginas OK |
| `SyncError` | Fallo temporal de Pull de recovery (cursor/Outbox intactos) |

## Blazor Server

- `CriticalAlertReceived` se mantiene.
- `NotificationCenter`: `WithAutomaticReconnect` + handler `Reconnected` → `RefreshAlertsAsync` (no asume eventos perdidos).
- Blazor online **no** ejecuta `SyncEngine` (réplica offline es MAUI).
- Hub permite conexión con `ViewAlerts` **o** `CaptureRecords` **o** `ViewRecords`.
  - Grupo `alert-user:{id}` solo si `ViewAlerts`.
  - Grupo `sync-user:{id}` para señal de recovery.

## MAUI / OfflineStore

- Paquete `Microsoft.AspNetCore.SignalR.Client`.
- Auth: header `Cookie` desde `MauiWebViewCookieProvider` (WebView). No se persisten contraseñas/tokens en SQLite.
- Triggers → recovery: `hub-connected`, `hub-reconnected`, `SyncRecoverySuggested`, `CriticalAlertReceived`, `connectivity-restored`, `app-resumed`, login bridge.
- Logout (`ClearSession`) detiene el hub para no mezclar usuarios.
- Misma ruta: `SyncEngine.PullAsync` → `HttpSyncApiClient.PullAsync`.

## Manejo de errores

- Timeout/red en Pull: no avanza cursor; Outbox Pending intacto; fase `SyncError`; reintento con nuevo trigger.
- Unauthorized: `AuthRequired`; no corrupte cursor.
- Sin sesión UX: no inicia Pull.

## Escenarios de recuperación

| Id | Caso | Resultado |
|----|------|-----------|
| A | Disconnect simple | Pull avanza cursor X→X+N |
| B | Mensajes SignalR perdidos | Pull recupera igual |
| C | Update×2 + Delete mismo registro | Converge (tombstone) |
| D | ClearAll + Create Y offline | Tombstones + Upsert Y; sin resucitar |
| E | Reconexión/triggers repetidos | Sin Pulls concurrentes innecesarios |
| F | HasMore | Drena páginas |
| G | Error temporal | Cursor/Outbox intactos; retry OK |
| H | Pull concurrente | Sin cursor regresivo |
| I | Logout/login | Sin Pull sin sesión; resume mismo usuario |
| J | Permisos Pull | Sin nueva política; servidor sigue autorizando |

## Qué NO hace SignalR

- Segunda cola durable de cambios
- Almacenar eventos pendientes de sync
- Transportar snapshots / change logs completos
- Sustituir `SyncChangeLog.Sequence`
- Confirmar sincronización vía ACK
- Mutar SQLite local directamente

## Límites conocidos

- `SyncRecoverySuggested` se emite hoy junto a alertas críticas (mismo destinatario de alerta + grupo sync). Operarios sin `ViewAlerts` recuperan sobre todo por **reconnect / connectivity / resume / login**.
- No hay broadcast global de cada `SyncChangeLog` (fuera de alcance; evitar tormentas).
- Android E2E y SQL Server físico: validación de entorno, no sustituida por mocks.
- No background sync permanente ni polling que reemplace SignalR.

## Observabilidad

Logs (sin secretos/payloads):

- reconnect / request recovery (`Reason`, `CorrelationId`)
- inicio/fin Pull (`CursorBefore`, `CursorAfter`, `Pages`, `DurationMs`, `Error`)

## Fuera de alcance (explícito)

Paginación Registros, Dashboard, compactación ChangeLog, `RecordsCleared`, historiales completos, Firebase, cifrado SQLite, backfill Firestore, SQL Server prod, E2E Android obligatorio, cambios NEPS/permisos/conflictos, ClearAll offline, Push catálogos, polling permanente.
