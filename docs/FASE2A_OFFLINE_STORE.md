# FASE 2A — Offline store local (Outbox SQLite)

## Alcance

- Persistencia SQLite local (`RegNeps.OfflineStore`) para captura offline v1 (**solo CreateRecord**).
- UI nativa MAUI `OfflineCapturePage`.
- Sin Push/Pull, SyncEngine, tombstones ni SignalR recovery (2B+).

## Aislamiento

`RegNeps.OfflineStore` referencia únicamente `RegNeps.Domain` (+ EF SQLite).  
No depende de Blazor Server, HTTP, SignalR ni Firebase para persistir una captura.

## Frontera WebView ↔ OfflineStore

| Capa | Responsabilidad |
|------|-----------------|
| `MainPage` / WebView | UI online Blazor; probe de servidor; navegación a offline |
| `OfflineCapturePage` | UI nativa; llama servicios OfflineStore vía DI |
| `RegNeps.OfflineStore` | SQLite, Outbox, sesión UX, DeviceId |

OfflineStore **no** conoce WebView. MainPage **no** escribe SQLite directamente.

## DeviceId

- Archivo `device-id.json` en AppData (`Guid.NewGuid("N")`).
- **No** usa IMEI, MAC, teléfono ni otra PII.
- Estable mientras exista la instalación.
- Desinstalación/reinstalación del APK → AppData borrado → nuevo DeviceId.

## LocalSession (snapshot UX — no es credencial)

| Campo | Uso |
|-------|-----|
| `UserId`, `Username`, `RoleCode` | Identidad UX offline |
| `PermissionsCsv` | Gate de UI (p. ej. `CaptureRecords`); el servidor revalida en 2B |
| `CapturedAtUtc` / `ExpiresAtUtc` | TTL por defecto 72 h |
| `ServerBaseUrl` | URL usada en el último login online |
| `HasSecureAuthMaterial` | Flag: hay material en SecureStorage (no en SQLite) |

**No** almacena contraseñas ni tokens en claro en SQLite.  
Material de auth futuro → `ISecureAuthMaterialStore` (SecureStorage/Keystore).

### Quién lo proporciona (posterior, 2D)

Bridge desde login Blazor online (p. ej. deep-link / JS → MAUI) tras autenticación cookie exitosa.  
**No implementado en 2A.**

### Cuándo se crea/actualiza

- Tras login online exitoso en el dispositivo (previsto 2D).
- Actualización: mismo `UpsertUxSnapshotAsync` (fila singleton `Id=1`).

### Expiración

- `ExpiresAtUtc`; `GetValidSessionAsync` retorna `null` si venció → no captura offline.

### Cierre de sesión (previsto)

- Borrar/invalidar `LocalSession` y limpiar SecureStorage del usuario.
- Outbox pendiente **no** se borra automáticamente (evitar pérdida de mediciones); política exacta en 2D/2B.

### Cambio de usuario en el mismo dispositivo

- Nuevo login → reemplaza snapshot (`Id=1`) con el nuevo usuario.
- Registros Outbox del usuario anterior permanecen filtrados por `UserId`; no se mezclan en la UI del usuario actual (`ListRecentAsync` filtra por sesión vigente).

El snapshot **nunca** autoriza al servidor.

## Transacción de captura

```
BeginTransaction
  → SaveChanges(LocalNepRecord)
  → SaveChanges(PendingOperation)
  → Commit
```

Si el segundo falla → `Rollback` → ningún registro queda en BD.

## Clasificación NEPS

`NepsQualityCriteria` / `AlertEvaluator` (Domain). Sin duplicar umbrales.

## SQLitePCLRaw (GHSA-2m69-gcr7-jv3q)

- Introducido transitivamente por `Microsoft.EntityFrameworkCore.Sqlite` 8.0.17 → `SQLitePCLRaw.*` 2.1.6.
- Advisory: CVE-2025-6965 / GHSA-2m69-gcr7-jv3q (SQLite &lt; 3.50.2).
- Mitigación aplicada: pin directo `SQLitePCLRaw.bundle_e_sqlite3` **3.0.3** en OfflineStore y Mobile.
