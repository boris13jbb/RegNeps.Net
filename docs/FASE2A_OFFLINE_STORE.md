# FASE 2A — Offline store local (Outbox SQLite)

## Alcance

- Persistencia SQLite local (`RegNeps.OfflineStore`) para captura offline v1 (**solo CreateRecord**).
- UI nativa MAUI `OfflineCapturePage`.
- Sin Push/Pull, SyncEngine, tombstones ni SignalR recovery (2B+).

## DeviceId

- Archivo `device-id.json` en AppData (Guid generado por la app).
- **No** usa IMEI, MAC ni teléfono.
- Desinstalación/reinstalación del APK → nuevo DeviceId.

## Sesión offline

- `LocalSession` en SQLite: UserId, Username, RoleCode, PermissionsCsv, expiración.
- **No** almacena contraseñas ni tokens en claro.
- Material de auth (futuro sync) → `SecureStorage` / Keystore vía `ISecureAuthMaterialStore`.
- Requiere al menos un login online previo que persista el snapshot UX (bridge completo en 2D).

## Esquema SQLite

Tablas: `LocalNepRecords`, `PendingOperations`, `LocalCatalogItems`, `SyncStates`, `LocalSessions`.  
Migración: `20261002180000_InitialLocalSync`.

## Transacción de captura

`BeginTransaction` → insert `LocalNepRecord` + `PendingOperation` → `SaveChanges` → `Commit`.  
Fallo → Rollback; la captura se considera fallida.

## Clasificación

UI y servicio usan `NepsQualityCriteria` / `AlertEvaluator` del ensamblado Domain (sin duplicar umbrales).
