# FASE 2I — Bootstrap, migraciones y despliegue reproducible

**Estado:** PASS CON WARNINGS (SQL Server físico y Android E2E siguen PENDING por entorno).  
**HEAD base:** `457ccf8` (FASE 2H).  
**Alcance:** aclarar mecanismos reales; corregir docs contradictorias. Sin features nuevas.

---

## 1. Resumen ejecutivo (respuestas inequívocas)

| Pregunta | Respuesta |
|----------|-----------|
| ¿Cómo se crea una instalación nueva (Web)? | Arrancar `RegNeps.Web` → `EnsureDatabaseCreatedAsync` → `EnsureCreated` (si no hay BD) → parches aditivos → seed → baseline catálogos |
| ¿Cómo se actualiza una instalación existente (Web)? | Arrancar binario nuevo → `EnsureCreated` es **no-op** si la BD ya existe → **solo** `DatabaseInitializer` / `RoleSchemaPatches` (SQL aditivo idempotente) |
| ¿SQLite Web/dev? | Mismo pipeline que SQL Server; archivo bajo `App_Data/` (ruta absoluta desde ContentRoot) |
| ¿SQL Server? | `Database:UseSqlServer=true` + connection string; **mismo** `DatabaseInitializer` (rama `ApplySqlServerPatchesAsync`) |
| ¿EF `Migrate()` en Web? | **No.** No se llama `Database.Migrate` / `MigrateAsync` en el arranque Web |
| ¿OfflineStore MAUI? | **Sí** usa `MigrateAsync()` (`LocalStoreInitializer`) sobre SQLite local del dispositivo |
| ¿Artefacto Android? | `net10.0-android`, script `scripts/build_android_apk.ps1`, Debug + `EmbedAssembliesIntoApk=true` |
| ¿`RegNeps.Migrate`? | Herramienta **CLI de importación Firestore→SQLite**; no es el migrador de esquema oficial de producción |

---

## 2. Tres SQLite distintos (no mezclar)

| Store | Quién | Ubicación típica | Bootstrap | ¿Seed de negocio? |
|-------|-------|------------------|-----------|-------------------|
| **Web / desarrollo** | `RegNeps.Web` + `RegNepsDbContext` | `ContentRoot/App_Data/regneps_v2.db` (desde `ConnectionStrings:RegNeps`) | `EnsureCreated` + patches | Sí (`DbSeeder`) |
| **OfflineStore MAUI** | `RegNeps.Mobile` + `LocalSyncDbContext` | SQLite en almacenamiento app Android | **`MigrateAsync`** (EF migrations OfflineStore) | No (réplica/Outbox/sesión UX) |
| **Tests** | xUnit | `:memory:` o temp file | Tests: `EnsureCreated`+patches (servidor) **o** `Migrate` (OfflineStore) | Fixtures de test |

Los tests **no** demuestran SQL Server. Ver FASE 2H (niveles A/B/C).

---

## 3. Bootstrap real — RegNeps.Web (SQLite y SQL Server)

### Cadena de arranque (código)

```text
Program.cs
  → AddRegNepsInfrastructure(connectionString, useSqlServer)
  → EnsureDatabaseCreatedAsync()
       → DatabaseInitializer.InitializeAsync
            1. EnsureCreatedAsync()          // solo si la BD no existe
            2. ApplySchemaPatchesAsync()     // SQLite o SQL Server
               + RoleSchemaPatches.ApplyAsync
            3. DbSeeder.SeedAsync            // roles, admin seed, etc.
            4. CatalogSyncChangeWriter.EnsureBaselineAsync
            5. HistoricalDataMigrationService.RepairRecordOwnershipAsync (si aplica)
```

Evidencia:

- `src/RegNeps.Web/Program.cs` — `EnsureDatabaseCreatedAsync()`
- `src/RegNeps.Infrastructure/DependencyInjection.cs` — `UseSqlServer` / `UseSqlite`; `EnsureDatabaseCreatedAsync` → `DatabaseInitializer`
- `src/RegNeps.Infrastructure/Persistence/DatabaseInitializer.cs`

### Configuración proveedor

| Entorno | `Database:UseSqlServer` | Connection string |
|---------|-------------------------|-------------------|
| Dev default (`appsettings.json`) | `false` | `Data Source=regneps_v2.db` → resuelto a `App_Data\…` |
| Production plantilla | `true` | SQL Server (User Secrets / env; no secretos en git) |

Proveedor EF: `Microsoft.EntityFrameworkCore.SqlServer` o `.Sqlite` según flag.

### Instalación nueva

```text
BD inexistente
→ EnsureCreated crea tablas desde el modelo EF actual
→ patches aseguran índices/columnas/tablas aditivas (idempotente; p. ej. SyncChangeLogs)
→ seed + baseline catálogos
```

### Actualización (upgrade)

```text
BD ya existe (creada antes con EnsureCreated u otro arranque)
→ EnsureCreated NO altera el esquema (no-op)
→ ApplySchemaPatchesAsync aplica solo lo faltante (IF NOT EXISTS / try-execute)
→ seed idempotente (no pisa IsEnabled de permisos ya editados)
```

**No hay** tabla `__EFMigrationsHistory` gestionada por el arranque Web.  
**No hay** “versión de migración EF” del servidor Web: la evolución es **lista de parches idempotentes en código**.

### Archivo `Infrastructure/Migrations/20261002190000_AddSyncChangeLog.cs`

Es **canónico/documental**. Comentario en código: el despliegue sigue con EnsureCreated + patches; **no** se aplica con `Migrate()` automático (y hay conflicto histórico de namespace `Migration` vs `Migrations`).

---

## 4. EnsureCreated vs Migrate — veredicto

| Contexto | Mecanismo | ¿Deliberado? |
|----------|-----------|--------------|
| Web SQLite | `EnsureCreated` + patches | **Sí** |
| Web SQL Server | `EnsureCreated` + patches SQL Server | **Sí** (mismo código) |
| OfflineStore MAUI | `MigrateAsync` | **Sí** |
| Tests servidor | `EnsureCreated` + patches (espejo Web) | Sí |
| Tests OfflineStore | `MigrateAsync` | Sí |
| `RegNeps.Migrate` CLI | `EnsureCreated` (SQLite) + import datos | Herramienta de datos |

### ¿Son equivalentes?

**No.** `EnsureCreated` materializa el modelo actual si la BD no existe y **ignora** el historial EF Migrations. `Migrate` aplica migraciones versionadas y escribe `__EFMigrationsHistory`.

### ¿Pueden entrar en conflicto?

Sí, **si** se mezclaran en la misma BD Web: una BD creada con `EnsureCreated` no tiene historial de migraciones; llamar después `Migrate()` puede intentar recrear objetos o fallar. Por eso Web **no** usa `Migrate`.

### ¿Hay que cambiar Web a Migrate en 2I?

**No.** Sería una migración arquitectónica completa. El diseño actual (EnsureCreated + parches aditivos) es intencional y compatible con SQLite y SQL Server vía ramas en `DatabaseInitializer`.  
Riesgo residual: todo cambio de esquema productivo Web **debe** ir acompañado de un parche idempotente (no solo de una clase Migration documental).

---

## 5. SQL Server — procedimiento reproducible

Detalle operativo de validación: [`FASE2H_SQLSERVER_VALIDATION.md`](FASE2H_SQLSERVER_VALIDATION.md).

| Ítem | Valor |
|------|-------|
| Versión mínima | SQL Server **2016+** (índices filtrados); recomendado 2019/2022 o LocalDB |
| Creación inicial | Arrancar Web con `UseSqlServer=true` (EnsureCreated + patches) |
| Actualización | Mismo arranque; solo patches |
| Migraciones EF formales | No aplicadas en runtime |
| Seed | `DbSeeder` en cada arranque (idempotente) |
| Rollback | Backup/restore BD + binario anterior (`CHECKLIST_DESPLIEGUE_PARIDAD.md`) |

**Gate físico:** PENDING hasta existir instancia. No usar SQLite como “equivalente SQL Server”.

---

## 6. OfflineStore (MAUI) — bootstrap

```text
MauiProgram
  → LocalStoreInitializer.InitializeAsync
       → Database.MigrateAsync()   // InitialLocalSync, AddSyncEngineFields, …
  → EnsureSyncStateAsync(deviceId)
```

- Migraciones: `src/RegNeps.OfflineStore/Migrations/`
- No usa `DatabaseInitializer` del servidor
- No contiene la BD de negocio Web
- Auth real = cookie WebView; `LocalSession` = UX; SecureStorage ≠ cookies (FASE 2G)

---

## 7. RegNeps.Migrate — papel exacto

| Hace | No hace |
|------|---------|
| Importa JSON export Firestore → **SQLite** | No usa SQL Server |
| `EnsureCreated` + intento ALTER `SnapshotJson` | No ejecuta `ApplySchemaPatchesAsync` completo |
| Inserta/actualiza registros, usuarios, telas, informes, alertas vía `HistoricalDataMigrationService` | No es el mecanismo de upgrade de esquema Sync/roles |
| Herramienta histórica / one-shot de datos | No sustituye arrancar Web |

**Orden recomendado:**

1. Export Firestore (`scripts/export_firestore_history.js`) — ver `MIGRACION_FIRESTORE.md`
2. Import: UI `/migracion` (Web ya patcheada) **o** CLI `RegNeps.Migrate` a SQLite
3. Si se usó CLI: **arrancar Web** al menos una vez para aplicar patches (SyncChangeLogs, índices, roles)
4. Cambiar passwords temporales

**¿Obligatorio para SQL Server?** No. Para SQL Server: arrancar Web (schema) + importar por UI `/migracion` (o pipeline de datos aparte). El CLI actual está cableado a `UseSqlite`.

---

## 8. Matriz fresh install vs upgrade

| Escenario | SQLite Web | SQL Server | Herramienta |
|-----------|------------|------------|-------------|
| DB nueva | EnsureCreated + patches + seed | Igual (rama SQL patches) | Arranque `RegNeps.Web` |
| DB existente antigua | Patches idempotentes | Patches idempotentes | Arranque binario nuevo |
| App actualizada (deploy) | Idem | Idem | Publish + restart; backup previo |
| Seed | DbSeeder cada arranque | Igual | Automático |
| Reparación ownership | Repair en Initialize | Igual | Automático |
| Import Firestore | CLI SQLite o UI | UI tras schema | `RegNeps.Migrate` / `/migracion` |
| Rollback | Restore `.db` + binario | `RESTORE` + binario | Ops (`CHECKLIST_…`) |
| Tests | EnsureCreated o Migrate según store | N/A en CI actual | xUnit |
| OfflineStore device | Migrate EF local | N/A | Arranque MAUI |

---

## 9. Android — artefacto y versiones

Fuente de verdad: `src/RegNeps.Mobile/RegNeps.Mobile.csproj` + `scripts/build_android_apk.ps1` + [`ANDROID_APK.md`](ANDROID_APK.md) (actualizado en 2I).

| Ítem | Valor actual |
|------|----------------|
| TargetFramework | **`net10.0-android`** |
| MauiVersion | 10.0.101 |
| Min API | 24 |
| ApplicationId | `com.burbatech.regneps` |
| Build QA | `powershell -File .\scripts\build_android_apk.ps1` |
| Flags | `-c Debug` `-p:AndroidPackageFormat=apk` **`-p:EmbedAssembliesIntoApk=true`** |
| Salida | `src\RegNeps.Mobile\bin\Debug\net10.0-android\**\*.apk` |
| SDK host | .NET SDK **10+**, workload `maui-android` |
| Cleartext | `usesCleartextTraffic=true` (HTTP LAN) |
| Offline | OfflineStore SQLite local + sync al backend; WebView online = autoridad |

CI: `.github/workflows/android-apk.yml` usa `net10.0-android`. El trigger `push` sigue listando `feature/android-apk-shell`; para otras ramas usar **workflow_dispatch** sobre la rama deseada.

---

## 10. Comandos reproducibles (documentados = ejecutables)

```powershell
cd <repo>

dotnet restore RegNeps.sln
dotnet build RegNeps.sln -c Release
dotnet test tests\RegNeps.Tests\RegNeps.Tests.csproj -c Release
dotnet publish src\RegNeps.Web\RegNeps.Web.csproj -c Release -o publish

# Android QA APK
powershell -ExecutionPolicy Bypass -File .\scripts\build_android_apk.ps1
adb devices
adb install -r <ruta-al.apk>
```

`RegNeps.sln` (CI `.NET`) = net8.0 Web/tests — **no** incluye Mobile net10.

---

## 11. Coherencia con fases 2B–2G / NEPS

La documentación de bootstrap **no** altera:

- SyncChangeLog / Push / Pull / tombstones ClearAll  
- SignalR → Pull recovery  
- Paginación server-side  
- Seguridad offline (servidor autoridad)  
- Fórmula `NEPS/m² = NEPS / 0.09` y bandas Q oficiales  

Solo aclara **cómo** llega el esquema (patches Web vs Migrate OfflineStore).

---

## 12. Límites y riesgos

| Riesgo | Estado |
|--------|--------|
| Upgrade Web depende de parches en código, no de historial EF | Aceptado; disciplina de patches obligatoria |
| Migration SyncChangeLog documental ≠ aplicada por Migrate | Documentado; patches crean la tabla |
| `RegNeps.Migrate` sin patches completos hasta arrancar Web | Documentado |
| SQL Server / Android E2E no validados en máquina sin entorno | PENDING (2H/2I) |
| Workflow Android push solo en rama histórica | Usar workflow_dispatch |
| SQLite OfflineStore sin cifrado | FUTURE (2G) |

---

## 13. Documentación tocada en 2I

| Archivo | Cambio |
|---------|--------|
| `docs/FASE2I_BOOTSTRAP_AND_MIGRATION.md` | Este documento (canónico bootstrap) |
| `docs/ANDROID_APK.md` | Alineado a `net10.0-android` + script real |
| `docs/FASE2H_VALIDATION_READINESS.md` | Puntero a 2I; quita “ANDROID_APK obsoleto” |
| `docs/FASE2H_SQLSERVER_VALIDATION.md` | Aclara EnsureCreated vs Migrate |
| `docs/MIGRACION_FIRESTORE.md` | Papel real de `RegNeps.Migrate` |
| `docs/DESPLIEGUE_INTRANET.md` | Bootstrap Web |
| `README.md` | BD / fuera de alcance Flutter |
| Comentario `DatabaseInitializer` | Flujo productivo inequívoco en código (sin cambio de comportamiento) |

## 14. Validación 2I

| Gate | Resultado |
|------|-----------|
| Suite .NET | 518/518 PASS |
| Web Release | 0 warnings / 0 errors |
| Android Debug | Sin cambios Mobile; PASS heredado (script/`net10` sin cambio) |
| Android E2E | PENDING (dispositivo) |
| SQL Server físico | PENDING (instancia) |

**DETENTE.** No abrir fase siguiente automáticamente.
