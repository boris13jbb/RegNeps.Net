# FASE 2D — Offline UX, LocalSession bridge y sincronización manual

Documentación acumulada de 2D.1 → 2D.3. La sincronización **permanece manual**.

## Estado de fases

| Fase | Contenido | Estado |
|------|-----------|--------|
| 2D.1 | LocalSession bridge WebView ↔ MAUI + captura offline Create | Validada |
| 2D.2 | SyncEngine MAUI Push/Pull | Validada |
| 2D.2.1 | Validación Id/Duplicate/401/Pull | Validada |
| **2D.3** | **UX de sincronización y conflictos** | **Esta fase** |
| 2D.4+ | Background sync / SignalR recovery | **Fuera de alcance** |

## Autoridad de autenticación

- La **cookie** `RegNeps.Auth` del WebView es la autenticación real para Push/Pull.
- `LocalSession` es un **snapshot UX** (UserId, Username, Role, Permissions, ServerBaseUrl, TTL).
- **No** asumir: «LocalSession válida = autenticación válida».
- No se almacenan contraseñas, cookies ni tokens en SQLite.

## Estados de conectividad (UX)

| Estado | Significado | Sync |
|--------|-------------|------|
| OnlineReady | Red OK + cookie presente | Permitida (manual) |
| OfflineNoNetwork | Sin red en el dispositivo | Captura sí; sync no |
| ServerUnreachable | Red sí, servidor no | Captura sí; sync fallará |
| LocalSessionWithoutOnlineAuth | Sesión local OK, cookie ausente | Captura temporal; **requiere re-login para sync** |
| LocalSessionExpired | TTL local vencido | Re-login |
| NoLocalSession | Sin snapshot | Re-login |
| RequiresLogin | 401 / cookie inválida en última sync | Re-login |

Mensaje típico (sesión local sin cookie):

> Tu sesión local permite capturar temporalmente, pero necesitas volver a conectarte e iniciar sesión para sincronizar.

## Estados Outbox (PendingOperationStatus)

| Estado | Significado para el usuario |
|--------|-----------------------------|
| **Pending** | Pendiente de sincronización (incluye TransientError reintentable) |
| **Sending** | Enviado en el lote actual (transitorio) |
| **Synced** | Accepted o Duplicate en servidor |
| **SyncError** | Error permanente (Forbidden / Invalid / reuso, etc.) |
| **Conflict** | El servidor tiene otra versión; **requiere revisión** (sin LWW) |
| **Cancelled** | Cancelada (no usada en UX 2D.3) |

## Resumen en OfflineCapturePage

- Contadores: Pendientes / Sincronizadas / Con error / En conflicto (calculados desde Outbox; sin tabla histórica nueva).
- Última sincronización conocida (`SyncState.LastSuccessfulSyncUtc`).
- Mensaje de error resumido y sanitizado (sin cookies, tokens, stacks).
- Botón **Sincronizar ahora** → `ISyncEngine.SyncAsync` vía `ManualSyncGate` (una sola ejecución concurrente).
- **No** hay timers, WorkManager, background sync ni SignalR recovery.

## Resultado de Push (mensajes UX)

| Resultado motor | Mensaje usuario |
|----------------|-----------------|
| Accepted / Duplicate | Sincronizado correctamente |
| TransientError | No se pudo sincronizar todavía… permanece pendiente |
| Forbidden | No tienes permisos… |
| Invalid | El servidor no pudo aceptar esta operación… |
| Conflict | Esta información cambió en el servidor y requiere revisión |

## Conflictos

- Sección «Conflictos (requieren revisión)» con comparación amigable (Telar / NEPS / NEPS/m / calidad).
- Conserva snapshot servidor (`ConflictServerSnapshotJson`); **no** sobrescribe local.
- **No** hay resolución automática, merge, LWW, «mantener siempre local/servidor».
- Acción actual: **Requiere revisión** (resolución manual futura deberá respetar `ConcurrencyStamp`).

## Re-login

1. UX muestra «Volver a iniciar sesión».
2. Vuelve a `MainPage` / WebView (`ReturnToOnlineLoginAsync`).
3. Tras login exitoso, el bridge existente actualiza `LocalSession`.
4. No se crea un sistema de autenticación nuevo.

## Logout

Contrato existente (`OfflineSessionService.ClearUxSnapshotAsync`):

- Limpia `LocalSession` y material seguro.
- **Conserva** Outbox y registros locales pendientes.
- Tras logout no se puede sincronizar hasta nueva sesión WebView válida.
- La UI informa explícitamente que el trabajo offline no se elimina.

## Captura offline (limitación 2D.3)

- Solo **Create** expuesto en UI.
- Update / Delete / ApplyCorrective offline: motor puede existir; **UI diferida**.
- Usa `OfflineCaptureService`, `LocalSession`, `ClientOperationId`, `CaptureSessionId`, `NepsQualityCriteria`.

## Warnings conocidos (no cambian arquitectura en 2D.3)

1. TTL LocalSession 72h puede superar cookie 12h → UX «sesión local sin auth online».
2. No hay E2E APK en dispositivo en esta fase.
3. UI offline solo Create.
4. `ServerBaseUrl` y URL del Entry deben permanecer coherentes.

## Fuera de esta fase

- Background / periodic sync
- SignalR recovery / sync disparado por hub
- Push/Pull automático
- WorkManager / timers / servicios residentes
- Resolución automática de conflictos
- Firebase
- Cambio de protocolo `/api/sync/push|pull`, `ClientOperationId`, `ConcurrencyStamp`, tombstones, NEPS

## Componentes clave

- `OfflineSyncUxService` / `ManualSyncGate` / `ManualSyncRunner` — OfflineStore
- `OfflineCapturePage` — MAUI UX
- `ISyncEngine` — única fuente de verdad para ejecutar sync

---

## FASE 2D.3.1 — Validación técnica (fuentes de verdad)

### De dónde lee la UX cada dato

| Dato UX | Fuente persistida | Derivado / runtime |
|---------|-------------------|--------------------|
| Pending / Synced / SyncError / Conflict | `PendingOperations.Status` (enum; Sending se suma a Pending) | `OfflineSyncUxService.GetCountersAsync` |
| Última sincronización | `SyncState.LastSuccessfulSyncUtc` (singleton Id=1) | Fallback `UpdatedAtUtc` |
| Error última sync | `SyncState.LastError` | Sanitizado (sin secretos) |
| Cursor Pull | `SyncState.LastPulledSequence` | Solo informativo en resumen |
| Sesión local | `LocalSessions` vía `OfflineSessionService` | TTL 72h; **no** es auth |
| Cookie / sync posible | `ISyncAuthCookieProvider` + `LocalSession.ServerBaseUrl` | Runtime WebView; **no** SQLite |
| Conectividad red | `Connectivity` + `ServerAvailabilityProbe` | Runtime |
| Conflictos UI | Outbox `Conflict` + `LocalNepRecord` + `ConflictServerSnapshotJson` | Textos amigables |
| Ejecutar sync | Solo `ISyncEngine.SyncAsync` | Gate: `ManualSyncGate` |

**Una fila Outbox = un estado.** No hay doble fuente para Pending/Conflict: el enum es excluyente.
**Synced en contador** = filas actuales con `Status=Synced` en Outbox (estado actual, no historial externo).

### Semántica IDs

- `LocalNepRecord.Id` — identidad local.
- `LocalNepRecord.ServerRecordId` — Id servidor tras Accepted/Duplicate.
- `PendingOperation.TargetServerRecordId` — Id servidor conocido para Update/Delete.
- `ClientOperationId` — idempotencia; **nunca** se regenera en retry.

### Correcciones 2D.3.1

- Guard anti doble-toque en «Guardar» (`_saving`).
- Aviso UX si `ServerBaseUrl` de sesión ≠ URL Entry (sin hardcode).
- `SanitizeError` clasifica 401/403 también en mensajes cortos limpios.

### No incluido

Background sync, SignalR recovery, push automático, resolución de conflictos, cambio de TTL 72h, E2E APK obligatorio.
