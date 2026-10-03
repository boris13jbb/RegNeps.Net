# FASE 2H — Validation readiness (auditoría documental)

**Estado de cierre:** PASS CON WARNINGS  
**HEAD de referencia (inicio de fase):** `2c1556f` — `fix: FASE 2G — auditoría seguridad offline/sync + endurecimientos`  
**Alcance:** solo documentación y procedimientos. Sin funcionalidades nuevas, sin push/PR/merge.

## 1. Estado del proyecto

Fases funcionales offline cerradas en esta rama (`feature/fase-2d5-offline-update`):

| Fase | Commit | Tema |
|------|--------|------|
| 2D.12 | `9b161f9` | ClearAll sync-safe (N tombstones) |
| 2E | `ca2b146` | SignalR → Pull recovery |
| 2F | `3af25a8` | Paginación server-side Web |
| 2G | `2c1556f` | Auditoría seguridad offline + endurecimientos |

Arquitectura operativa (no reimplementar en 2H):

- SQLite OfflineStore + Outbox + SyncEngine (Push/Pull)
- Create / Update / Delete / ApplyCorrective offline
- ChangeLog, idempotencia, ConcurrencyStamp, conflictos explícitos
- Catálogos Tela/Lote (Pull-only)
- ClearAll → N `RecordDeleted` (sin `RecordsCleared`)
- Recuperación SignalR → Pull (SignalR notifica; Pull sincroniza)
- Paginación server-side en Registros/Dashboard
- Servidor = autoridad de identidad, permisos y mutaciones

### Evidencia de compilación/suite (FASE 2H)

| Gate | Resultado | Nivel |
|------|-----------|-------|
| Suite .NET (`dotnet test … -c Release`) | **518/518 PASS** (re-ejecutada en 2H) | A |
| Web Release (`dotnet build Web -c Release`) | **0 warnings / 0 errors** (re-ejecutada en 2H) | A |
| Android Debug | PASS en 2G; sin cambios de código Mobile en 2H | A (heredada) |
| Android E2E dispositivo/emulador | PENDING | C |
| SQL Server físico/LocalDB | PENDING | C |
| SQL Server + Android combinado | PENDING | C |
| Cifrado SQLite OfflineStore | FUTURE | — |

**Ajuste de test en 2H (justificado):** `SyncEnginePullRecoveryTests.H_Concurrent_Pull_Does_Not_Regress_Cursor` era flaky al lanzar dos `PullAsync` sobre el **mismo** `DbContext` bajo carga de suite (517/518). Se reescribió con dos contextos SQLite `Cache=Shared` sin cambiar el protocolo Sync. Cobertura de gate ya definida (2E), no feature nueva.

**Nota:** la mayoría de tests de sync/infra usan **SQLite** (`:memory:` o archivo temp). Eso es **Nivel A respecto a SQLite** y **Nivel B como sustituto de SQL Server** (útil, no equivalente).

---

## 2. Matriz completa de evidencia

Leyenda de **Nivel**:

- **A** — Ejecutado realmente (automatizado o build real).
- **B** — Sustituto técnicamente útil (**sustituto, no equivalente** a SQL Server físico / dispositivo real).
- **C** — Pendiente por infraestructura.

| Área | Test / artefacto | Tipo | Ejecutado | Entorno | Resultado | Evidencia | Gate pendiente |
|------|------------------|------|-----------|---------|-----------|-----------|----------------|
| Dominio NEPS | `NepsQualityCriteriaTests` | Unit | Sí | In-proc | PASS | Límites Q 18/19/45/46/54/55; fórmula `/0.09` | — |
| Dominio alertas | `AlertEvaluatorTests`, `NepRecordFormulaTests` | Unit | Sí | In-proc | PASS | Calidad ≠ umbrales legacy 30/60 | — |
| Dominio permisos | `RolePermissionsTests`, `RolePermissionServiceTests` | Unit/Int | Sí | SQLite | PASS | Matriz de permisos | — |
| Aplicación captura | `CaptureIsolationTests`, `CaptureValidationRulesTests` | Int | Sí | SQLite | PASS | Aislamiento sesión/usuario | — |
| Aplicación filtros | `RecordFilterTests`, `RecordBatchAndSelectionTests` | Int | Sí | SQLite | PASS | Filtros/selección | — |
| Aplicación export | `ExportByIdsTests`, `ExportAuthorizationHttpTests`, `SavedReportAccessTests`, … | Int/HTTP | Sí | SQLite WebHost | PASS | AuthZ export | — |
| Aplicación share | `ShareDeliveryPolicyTests`, `RecordShareIsolationTests` | Unit/Int | Sí | In-proc/SQLite | PASS | Políticas share | — |
| Infraestructura BD | `DatabaseConcurrencyTests` | Int | Sí | SQLite | PASS (B vs SQL) | Concurrencia EF | SQL Server físico |
| Infraestructura roles | `LegacyRoleSchemaMigrationTests`, `RoleSchemaPatchTransactionTests`, `SqliteGuidTextCasingRepairTests`, `LegacyRoleSchemaEfGuidJoinTests` | Int | Sí | SQLite | PASS (B) | Parches aditivos | SQL Server staging |
| Infraestructura seed | `DbSeederAndCaptureTests` | Int | Sí | SQLite | PASS | Seed + captura | — |
| Sync Push | `SyncPushPullIntegrationTests`, `SyncEngineTests`, `SyncEngineValidation221Tests` | Int | Sí | SQLite server | PASS (B) | Push Accepted/Conflict | SQL Server |
| Sync Pull | mismos + `OfflineBackendIntegrationGateTests` | Int | Sí | SQLite | PASS (B) | Cursor / páginas | SQL Server |
| Idempotencia | índices + tests Push duplicado en Sync* | Int | Sí | SQLite | PASS (B) | `(Actor, ClientOperationId)` | SQL Server unique filter |
| Concurrencia | `SyncUpdateDeleteTests`, `SyncApplyCorrectiveTests`, `DatabaseConcurrencyTests` | Int | Sí | SQLite | PASS (B) | Stale stamp → Conflict | SQL Server |
| Conflictos | `OfflineConflictResolutionTests` | Int | Sí | SQLite local+server | PASS (B) | Keep Server/Local/Edit&Retry | E2E Android |
| ClearAll | `SyncClearAllTests`, `OfflineClearAllSyncTests` | Int | Sí | SQLite | PASS (B) | N tombstones + Pull | SQL Server + dataset |
| Catálogos | `OfflineCatalogSyncTests` | Int | Sí | SQLite | PASS (B) | CatalogUpserted/Deleted | SQL Server |
| ApplyCorrective | `SyncApplyCorrectiveTests`, `SyncApplyCorrectiveChangeLogTests`, `OfflineApplyCorrectiveTests` | Int | Sí | SQLite | PASS (B) | OpType ≠ Update | E2E Android |
| SignalR recovery | `SyncEnginePullRecoveryTests`, `SyncRecoveryCoordinatorTests` | Unit/Int | Sí | In-proc + SQLite | PASS (B) | Coalescing + Pull | Android E2E hub real |
| Paginación | `RecordServerSidePaginationTests` | Int | Sí | SQLite `:memory:` | PASS (B) | OFFSET/COUNT/filtros | SQL Server OFFSET/FETCH |
| Seguridad offline | `OfflineSecurity2GTests` | Int | Sí | SQLite | PASS | Cursor reset, SecureAuth, isolation | Android E2E 2 users |
| MAUI OfflineStore | `OfflineStoreCaptureTests`, `OfflineBridgeAndSessionTests`, `Offline*Tests`, `OfflineSyncUx*` | Int | Sí | SQLite OfflineStore | PASS (A local) | Outbox/UI contratos | Device real |
| MAUI build | `scripts/build_android_apk.ps1` | Build | Sí (2G) | SDK .NET 10 + maui-android | PASS | APK Debug embebido | Install E2E |
| Migraciones servidor | `DatabaseInitializer` + doc migration SyncChangeLog | Bootstrap | Parcial | EnsureCreated+patches | A en SQLite; C SQL | Idempotente | Fresh + upgrade SQL |
| Migraciones OfflineStore | EF migrations `InitialLocalSync`, `AddSyncEngineFields` | Bootstrap | Sí en tests | SQLite archivo | PASS | LocalSyncDb | Device |
| CI | `.github/workflows/dotnet-ci.yml` | CI | En PR a main | ubuntu + net8.0 | N/A en 2H local | No valida Mobile | — |
| Android APK CI | `android-apk.yml` | CI | Manual/workflow | Windows runner | Independiente | No status check obligatorio | — |

### Inventario de archivos de test (no inventar)

Ubicación: `tests/RegNeps.Tests/`. Clases `*Tests` relevantes (≈52 archivos; ~466 métodos Fact/Theory → **518 casos** con InlineData):

`NepsQualityCriteriaTests`, `BusinessRulesTests` (AlertEvaluator/NepRecordFormula/RolePermissions), `RolePermissionServiceTests`, `CaptureIsolationTests`, `CaptureValidationRulesTests`, `RecordFilterTests`, `RecordBatchAndSelectionTests`, `RecordShareIsolationTests`, `RecordServerSidePaginationTests`, `DatabaseConcurrencyTests`, `DbSeederAndCaptureTests`, `LegacyRoleSchemaMigrationTests`, `LegacyRoleSchemaEfGuidJoinTests`, `RoleSchemaPatchTransactionTests`, `SqliteGuidTextCasingRepairTests`, `SyncEngineTests`, `SyncEngineValidation221Tests`, `SyncPushPullIntegrationTests`, `SyncUpdateDeleteTests`, `SyncOnlineChangeLogTests`, `SyncClearAllTests`, `SyncApplyCorrectiveTests`, `SyncApplyCorrectiveChangeLogTests`, `SyncEnginePullRecoveryTests`, `SyncRecoveryCoordinatorTests`, `OfflineStoreCaptureTests`, `OfflineBridgeAndSessionTests`, `OfflineOfflineUpdateTests`, `OfflineOfflineUpdateGate251Tests`, `OfflineOfflineDeleteTests`, `OfflineApplyCorrectiveTests`, `OfflineConflictResolutionTests`, `OfflineCatalogSyncTests`, `OfflineClearAllSyncTests`, `OfflineBackendIntegrationGateTests`, `OfflineSecurity2GTests`, `OfflineSyncUxTests`, `OfflineSyncUxValidation231Tests`, `OfflineOperationsUxTests`, `ExportAuthorizationHttpTests`, `ExportByIdsTests`, `ExportSavedStyleAndColumnsTests`, `SavedReportAccessTests`, `ShareDeliveryPolicyTests`, `SpreadsheetFormulaGuardTests`, `AlertCriticalPublisherTests`, `AlertNotificationRecipientRulesTests`, `ReportBuilderServiceTests`, `AnalyticsReportBuilderParityTests`, `AnalyticsPreferencesCoverageNoteTests`, `TelarSortTests`.

---

## 3. Tests reales vs sustitutos

### Nivel A — realmente ejecutado

- Suite automatizada sobre **SQLite** (server + OfflineStore).
- Builds Web Release y Android Debug.
- Contratos de dominio NEPS, permisos, export authZ, bridge whitelist (en tests).

### Nivel B — sustituto (no equivalente)

- **SQLite en memoria/archivo** en lugar de SQL Server para Sync, ClearAll, paginación, índices filtrados, concurrencia EF.
  - Diferencias posibles: tipos (`TEXT` vs `uniqueidentifier`/`datetime2`/`nvarchar`), índices filtrados, planes OFFSET/FETCH, locking, IDENTITY vs AUTOINCREMENT, collation.
- **SignalR recovery** probado vía coordinador + Pull sin hub Android real ni WebSockets de red.
- **HTTP sync** vía host de prueba / HttpClient, no APK WebView + cookie de dispositivo.

### Nivel C — infraestructura real

| Capacidad | Por qué no está cerrada |
|-----------|-------------------------|
| SQL Server físico / LocalDB | Sin instancia en el entorno de desarrollo actual |
| Android E2E | Sin emulador/dispositivo conectado para el flujo completo |
| SQL Server + Android A/B | Requiere ambos anteriores |
| Performance 10k–50k formal | Requiere hardware medible + dataset staging |
| Backup/restore SQL prod | Ops; no ejecutar desde agente |

---

## 4. Prerrequisitos SQL Server

Ver procedimiento detallado: [`FASE2H_SQLSERVER_VALIDATION.md`](FASE2H_SQLSERVER_VALIDATION.md).

Resumen:

| Ítem | Valor esperado |
|------|----------------|
| Versión mínima | SQL Server **2016+** (índices filtrados, `datetime2`, `nvarchar(max)`). Recomendado 2019/2022 o LocalDB equivalente |
| Config | `Database:UseSqlServer = true` |
| Connection string | `ConnectionStrings:RegNeps` — ej. `Server=…;Database=RegNeps;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true` |
| Permisos | CREATE TABLE/INDEX/ALTER; IDENTITY; lectura/escritura app |
| Bootstrap | `EnsureCreated` + `DatabaseInitializer.ApplySchemaPatchesAsync` (idempotente) + `DbSeeder` + baseline catálogos — ver FASE 2I |
| Migración formal EF | `20261002190000_AddSyncChangeLog.cs` es **documental**; **no** se aplica con `Migrate()` automático |
| OfflineStore | Sigue en SQLite del dispositivo (independiente del motor servidor) |

Tablas/índices críticos a verificar tras primer arranque:

- `NepRecords` + `ClientOperationId`, `ConcurrencyStamp`, `CaptureSessionId`
- `IX_NepRecords_CreatedBy_ClientOperation` (unique filtered)
- `SyncChangeLogs` + `IX_SyncChangeLogs_Actor_ClientOperation` (unique filtered)
- `Fabrics` / `Lotes` (+ índices únicos nombre donde aplique)
- Roles / RolePermissions / RolePermissionAudits (tras `RoleSchemaPatches`)

---

## 5. Prerrequisitos Android

| Ítem | Valor |
|------|-------|
| TFM | `net10.0-android` (`RegNeps.Mobile.csproj`) |
| SDK | .NET SDK **10+**, workload `maui-android` |
| Min API | `SupportedOSPlatformVersion` 24 |
| PackageId | `com.burbatech.regneps` |
| Build QA | `powershell -ExecutionPolicy Bypass -File .\scripts\build_android_apk.ps1` |
| Flag crítico | `-p:EmbedAssembliesIntoApk=true` (sin esto, Fast Deployment → crash al instalar) |
| APK salida | `src\RegNeps.Mobile\bin\Debug\net10.0-android\**\*.apk` |
| Permisos | `INTERNET`, `ACCESS_NETWORK_STATE` |
| Cleartext | `android:usesCleartextTraffic="true"` (HTTP LAN QA) |
| WebView | Shell hacia `RegNeps.Web` (URL configurable; QA típica `http://<host>:5080`) |
| OfflineStore | SQLite local en dispositivo |
| Auth material | `SecureStorage` (Keystore); **no** cookies/Bearer (guard 2G) |
| SignalR | `Microsoft.AspNetCore.SignalR.Client` 8.x → `/hubs/alerts` |
| adb | `adb devices` → device/emulator `device` |
| Red | Teléfono y PC en misma LAN; firewall puerto **5080**; WebSockets para hub |

**Android docs:** `docs/ANDROID_APK.md` alineado a `net10.0-android` en FASE 2I. Bootstrap Web vs OfflineStore: [`FASE2I_BOOTSTRAP_AND_MIGRATION.md`](FASE2I_BOOTSTRAP_AND_MIGRATION.md).

---

## 6. Procedimientos E2E (comandos base)

### 6.1 Suite .NET (Nivel A)

```powershell
cd <repo>
dotnet test tests\RegNeps.Tests\RegNeps.Tests.csproj -c Release --verbosity minimal
```

**PASS:** 518 passed, 0 failed.  
**FAIL:** cualquier fallo.

### 6.2 Web Release

```powershell
dotnet build src\RegNeps.Web\RegNeps.Web.csproj -c Release --verbosity minimal
```

**PASS:** 0 errors (ideal 0 warnings).

### 6.3 Android Debug APK

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build_android_apk.ps1
```

**PASS:** genera `.apk` bajo `net10.0-android`.  
**FAIL:** SDK &lt; 10, workload faltante, o APK no encontrado.

### 6.4 Servidor Web para E2E

```powershell
dotnet run --project src\RegNeps.Web
# Escucha http://0.0.0.0:5080
```

Para SQL Server: configurar User Secrets / `appsettings.Production.json` (sin secretos en repo) y reiniciar.

### 6.5 Instalar APK

```powershell
adb devices
adb install -r <ruta-al.apk>
```

---

## 7. Escenarios Android obligatorios (A–H)

Precondiciones comunes: servidor alcanzable; usuario con captura; DeviceId estable por instalación; observar estados `PendingSync` / `Synced` / `Conflict` / `SyncError`.

### A — Create offline

```text
login online → abrir captura → desconectar red
→ crear registro → UI PendingSync
→ restaurar red → Sync/Push → Accepted → Synced
→ verificar fila en servidor (mismo ClientOperationId)
```

**PASS:** un registro servidor; Outbox vacía o marcada synced; sin duplicados.  
**FAIL:** duplicado, stuck Pending, o Accepted sin fila.

### B — Update offline

```text
capturar online → anotar ConcurrencyStamp
→ desconectar → Update → PendingSync
→ reconectar → Push → Accepted
→ Pull en otro cliente → datos actualizados + stamp nuevo
```

### C — Delete offline

```text
registro synced → offline Delete → PendingSync
→ Push → tombstone RecordDeleted en ChangeLog
→ Pull otros clientes → réplica sin fila viva
```

### D — ApplyCorrective offline

```text
registro elegible → offline ApplyCorrective (no Update)
→ Push OperationType ApplyCorrective
→ stamp rotado; ChangeLog distinto de UpdateRecord
```

### E — Conflict

```text
Cliente A y B con mismo ServerRecordId
→ A actualiza online (stamp avanza)
→ B offline update con stamp viejo → Push → Conflict
→ snapshot servidor conservado; resolución Keep Server / Keep Local / Edit&Retry
→ si Keep Local: nueva op con stamp actual
```

### F — ClearAll + cursor

```text
Android con cursor X y réplica con filas
→ desconectar Android
→ ClearAll admin en servidor (N RecordDeleted + Create posterior opcional)
→ reconnect → Pull
→ réplica vacía de registros borrados; sin resurrección; Creates posteriores visibles
```

### G — SignalR recovery

```text
hub conectado → forzar disconnect (airplane / matar Wi‑Fi / restart app)
→ AutomaticReconnect / connectivity-restored
→ SyncRecoverySuggested / RequestRecovery
→ un Pull (coalescing); cursor avanza; sin Pull concurrente innecesario
→ NO Push automático por SignalR
```

### H — logout/login (2 usuarios)

```text
Usuario A sync → logout
→ login Usuario B (otro DeviceId opcional)
→ cursor Pull no hereda avance indebido de A (reset 2G)
→ listados locales filtrados por UserId de sesión
→ permisos locales = UX; servidor rechaza Push sin permiso
```

---

## 8. Escenario combinado SQL Server + Android

```text
Android A ──Push/Pull──► SQL Server ◄── Web / Android B
```

Orden sugerido:

1. Staging BD `RegNeps_2H` (backup policy).
2. Arrancar Web con `UseSqlServer=true`.
3. Instalar APK A y B (o A + navegador).
4. Ejecutar A→B: Create, Update, Delete, ApplyCorrective.
5. Conflicto A/B.
6. ClearAll + Pull en A desconectado durante el wipe.
7. SignalR recovery en A tras corte de red.
8. Criterio: convergencia de réplicas con ChangeLog; sin resurrect; sin LWW silencioso.

**PASS:** convergencia observable + logs sin errores fatales.  
**FAIL:** divergencia persistente, resurrect post-ClearAll, o Conflict auto-aceptado.

---

## 9. Datos de prueba NEPS (no cambiar lógica)

| Q | Calidad esperada |
| -: | ---------------- |
| 18 | OK |
| 19 | Mención |
| 45 | Mención |
| 46 | Crítico - Realizar Ajuste |
| 54 | Crítico - Realizar Ajuste |
| 55 | 2da Calidad |

Confirmaciones:

- `NEPS/m² = NEPS / 0.09` (`NepsConstants.TestLengthM`).
- Calidad vía `NepsQualityCriteria` / `AlertEvaluator` — **no** `LimiteNormalMax` / `LimiteAdvertenciaMax`.
- `AlertasActivas` off no cambia banda de calidad.
- Notificación crítica: `CriticalAdjustment` y `SecondQuality` (`IsCriticalNotificationLevel`).

Automatizado: `NepsQualityCriteriaTests` (Nivel A). Manual en UI Captura: crear Q=18/19/46/55 y contrastar badge.

---

## 10. Migraciones y compatibilidad

| Camino | Mecanismo | ¿Mismo esquema lógico? |
|--------|-----------|------------------------|
| Fresh SQLite | EnsureCreated + patches | Sí (tests + dev) |
| Upgrade SQLite antiguo | Parches aditivos idempotentes | Sí si patches cubren columnas |
| Fresh SQL Server | EnsureCreated + `ApplySqlServerPatchesAsync` | Esperado; **C** sin instancia |
| Upgrade SQL Server | Parches IF NOT EXISTS | Esperado; verificar en staging |
| Migración EF formal SyncChangeLog | Archivo documental | No corre `Migrate()` |
| OfflineStore | EF Migrations locales | Sí en MAUI/tests |

**No** ejecutar migraciones destructivas ni `DROP` sobre datos reales en 2H.

Orden de arranque app: `EnsureCreated` → schema patches (SQLite o SQL Server) → RoleSchemaPatches → Seed → Catalog baseline → repair ownership.

---

## 11. Backups y rollback (antes de prueba SQL)

Documentación existente: `docs/CHECKLIST_DESPLIEGUE_PARIDAD.md` §1–3.

Checklist mínimo staging:

1. `BACKUP DATABASE … WITH COPY_ONLY` (o snapshot LocalDB).
2. Probar `RESTORE` en otra DB de prueba.
3. Plan de rollback: **binario anterior + restore BD** (no solo código).
4. No usar producción; preferir `RegNeps_2H_staging`.

2H **no** implementa backup automático.

---

## 12. Performance readiness (sin umbrales arbitrarios)

Datasets mínimos recomendados en staging:

| Dataset | Uso |
|---------|-----|
| 1k `NepRecords` | Smoke paginación + COUNT |
| 10k | Registros UI + filtros |
| 50k | Techos legacy QueryAsync / estrés COUNT |
| ChangeLog alto (N× ops) | Pull paginado |
| ClearAll N=1k–10k | Duración TX tombstones |

Métricas a recoger (sin fijar SLA aún):

- duración wall-clock (ClearAll, Pull page, QueryPaged, CountFiltered)
- filas procesadas / páginas Pull
- tiempo SQL (DMVs / Extended Events si disponible)
- número de queries (EF logging)
- memoria proceso / CPU proceso (orientativo)

Hardware debe anotarse junto a cada medición.

---

## 13. Security readiness (prueba real futura)

Incluir siempre:

- Dos usuarios (con/sin `SeesAllRecords`)
- Acceso a registros ajenos → Forbid / vacío según API
- Logout/login + reset cursor (2G)
- DeviceId distinto por dispositivo
- ClientOperationId idempotente
- Conflictos sin LWW
- Riesgo explícito: **SQLite OfflineStore sin cifrado** (fase futura)

No cifrar en 2H.

---

## 14. Matriz final de gates

| Gate | Estado | Requiere código | Requiere entorno | Procedimiento |
|------|--------|----------------:|-----------------:|---------------|
| Suite .NET | PASS | No | No | `dotnet test … -c Release` |
| Web Release | PASS | No | No | `dotnet build Web -c Release` |
| Android Debug | PASS | No | No | `scripts/build_android_apk.ps1` |
| Android E2E | PENDING | No | Sí (device/emulator + LAN) | Escenarios A–H §7 |
| SQL Server | PENDING | No | Sí (instancia) | `FASE2H_SQLSERVER_VALIDATION.md` |
| SQL Server + Android | PENDING | No | Sí (ambos) | §8 |
| SQLite encryption | FUTURE | Sí | No | Fase futura (2G residual) |
| Perf formal 10k/50k | PENDING | No | Sí (staging + métricas) | §12 |

---

## 15. Criterios PASS / FAIL globales 2H

| Resultado | Condición |
|-----------|-----------|
| **PASS** | Matriz de evidencia completa y procedimientos reproducibles; sin contradicción bloqueante código↔docs de gate |
| **PASS CON WARNINGS** | Documentación lista **y** Android E2E / SQL Server siguen PENDING por entorno (esperado) |
| **FAIL** | Docs contradictorias con implementación de forma que impida ejecutar un gate; regresión real; supuesto arquitectónico falso |

---

## 16. Riesgos

| Riesgo | Severidad | Mitigación |
|--------|-----------|------------|
| SQLite ≠ SQL Server en índices filtrados / tipos | Media | Procedimiento SQL dedicado |
| `ANDROID_APK.md` desactualizado (net8 / rama) | Baja | Usar este doc + script |
| EnsureCreated + patches ≠ migraciones EF formales | Media (aceptado) | Contrato documentado en FASE 2I; validar fresh+upgrade en staging SQL |
| ClearAll N grande → ChangeLog volumen | Media | Medir en §12 antes de prod |
| SQLite sin cifrado en dispositivo | Media | Documentado 2G; fase cifrado |
| SignalR detrás de proxy sin WebSockets | Media | Checklist IIS/nginx existente |
| APK sin EmbedAssemblies | Alta (crash) | Script ya fuerza el flag |

---

## 17. Siguientes pasos (dependientes de infraestructura)

1. Levantar SQL Server/LocalDB staging → ejecutar `FASE2H_SQLSERVER_VALIDATION.md`.
2. Conectar emulador/dispositivo → APK + escenarios A–H.
3. Combinar §8.
4. Opcional: datasets 1k/10k/50k + métricas.
5. Solo entonces valorar fase de cifrado SQLite / umbrales de perf.

**DETENTE aquí.** No abrir fase 2I automáticamente.

---

## 18. Documentación creada en 2H

| Archivo | Propósito |
|---------|-----------|
| `docs/FASE2H_VALIDATION_READINESS.md` | Matriz de evidencia, gates, E2E Android, NEPS, riesgos |
| `docs/FASE2H_SQLSERVER_VALIDATION.md` | Procedimiento reproducible SQL Server |
