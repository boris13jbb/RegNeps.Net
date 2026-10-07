# FASE 2G — Auditoría de seguridad offline y Sync

## Resumen

Regla:

> **El cliente offline nunca debe convertirse en autoridad de seguridad.**

Esta fase es principalmente una **auditoría**. Solo se aplicaron endurecimientos justificados (vulnerabilidad, exposición, límites de entrada, secretos).

Estado de cierre previsto: **PASS CON WARNINGS** (Android E2E, SQL Server físico, cifrado SQLite pendientes de entorno/fase futura).

No afirmar «seguro» de forma absoluta. Términos usados: mitigado, riesgo residual, pendiente, no aplicable.

## Superficie auditada

| Área | Componentes |
|------|-------------|
| OfflineStore | `LocalSyncDbContext`, entidades, `LocalSession`, Outbox, `SyncState`, migraciones |
| MAUI | Bridge login/logout, WebView cookie, SecureStorage, páginas offline, bootstrap |
| Servidor | `/api/sync/push`, `/api/sync/pull`, claims, permisos, ownership, DeviceId, ClientOperationId |
| SignalR | `/hubs/alerts`, grupos, `SyncRecoverySuggested` |
| Logs | SyncAppService, recovery, bridge |

## Amenazas consideradas

1. Persistencia de credenciales en SQLite/Preferences/SecureStorage.
2. Elevación de privilegios vía `PermissionsCsv` / RoleCode local.
3. Spoofing de `CreatedByUserId` / actor en Push.
4. Acceso a registros ajenos vía Pull o SQLite local en dispositivo compartido.
5. Bypass de `ConcurrencyStamp` / LWW.
6. Uso de DeviceId como secreto o identidad.
7. Side-channel de cursor Pull / `NextCursor`.
8. Payloads desmesurados (DoS).
9. SignalR como canal privilegiado.

## Hallazgos

### Mitigado en esta fase

| Severidad | Hallazgo | Corrección |
|-----------|----------|------------|
| **MEDIUM** | Cursor Pull (`SyncState.LastPulledSequence`) compartido por dispositivo: tras logout/login de otro usuario, B podía heredar cursor avanzado por A y no recibir sus change logs | Reset de cursor en `ClearUxSnapshotAsync` y al cambiar `UserId` en `UpsertUxSnapshotAsync` |
| **MEDIUM** | `MauiSecureAuthMaterialStore` / memory store no rechazaban cookies/Bearer | `SecureAuthMaterialGuard` + uso en stores y sesión |
| **LOW** | `GetLocalRecordAsync` devolvía filas de otro usuario en el mismo dispositivo | Filtro por `session.UserId` |
| **LOW** | Create/Update sin tope de longitud de strings en validación compartida | Límites alineados a columnas EF en `ValidateCreateRequest` |

### Riesgos residuales / aceptados (documentados)

| Severidad | Hallazgo | Estado |
|-----------|----------|--------|
| **MEDIUM** | SQLite sin cifrado: nombres, observaciones, Outbox, snapshots de conflicto accesibles si se extrae la BD | **Pendiente** — fase futura de cifrado |
| **MEDIUM** | `NextCursor` avanza sobre secuencias no autorizadas (sin filtrar payload) — side-channel de actividad | **Riesgo residual aceptado** (intranet; tests documentan comportamiento) |
| **LOW** | `PermissionsCsv` stale hasta TTL/re-login | **Riesgo residual** — UX-only; servidor revalida Push |
| **LOW** | Outbox/réplica de usuario A permanece tras logout (by design) | **Aceptado** — no destruir Pending; listados filtran por UserId |
| **LOW** | DeviceId cliente arbitrario (máx. 64) — no Guid obligatorio | **Aceptado** — solo auditoría, no authZ |
| **INFO** | Preferencias MAUI guardan URL de servidor | **Aceptable** |

### No aplicable / no encontrado

- Password/hash/cookie/token en columnas SQLite OfflineStore.
- Endpoint Push ClearAll / Outbox ClearAll.
- Cliente elige `CreatedByUserId` en DTO Create (propiedad inexistente; actor desde claims).
- SignalR transportando change logs o secretos.

## Correcciones aplicadas (producción)

1. `SecureAuthMaterialGuard` + integración sesión/stores.
2. Reset de cursor Pull en logout y cambio de usuario.
3. Aislamiento de `GetLocalRecordAsync` por sesión.
4. Validación de longitudes máximas en `ValidateCreateRequest` (Push Create/Update).

## Matriz Push / Pull / SignalR

| Canal | AuthN | AuthZ | Autoridad de datos |
|-------|-------|-------|--------------------|
| Push | Cookie (`[Authorize]`) | Permisos + ownership/SeesAll | Servidor (CreatedBy = actor) |
| Pull | Cookie | View/Capture + filtro ownership | Servidor (payload filtrado) |
| SignalR | Cookie | Capture/View/ViewAlerts | Solo señal → Pull |

## LocalSession

- No autentica al servidor; no eleva permisos reales.
- TTL 72h; logout borra snapshot + material seguro + **cursor Pull**.
- Conserva Outbox y réplica local.
- `RoleCode` / `PermissionsCsv` = **UX hint, nunca autorización**.

## DeviceId / ClientOperationId

- DeviceId: generado localmente (`Guid` N); servidor valida no vacío ≤64; **no es identidad**.
- ClientOperationId: idempotencia `(ActorUserId, ClientOperationId)`; no secreto; no lookup cross-user de resultados ajenos.

## Recomendaciones — futura fase cifrado SQLite

1. SQLCipher u opción MAUI con clave en Keystore/Keychain.
2. Política de wipe de réplica al logout (opcional, producto).
3. Cursor por `(UserId, DeviceId)` si se quiere conservar avance sin reset en logout del mismo usuario.
4. Revisar body size limits de Kestrel para `/api/sync`.

## Límites de la seguridad offline

El dispositivo físico con root/backup puede leer SQLite. La mitigación completa requiere cifrado + políticas MDM. Offline **no** sustituye login ni permisos del servidor.

## Confirmación

**El servidor sigue siendo la autoridad de identidad, permisos, ownership y mutaciones.**
