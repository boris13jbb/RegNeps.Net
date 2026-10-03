# FASE 2H — Procedimiento de validación SQL Server

**Propósito:** checklist reproducible para cerrar el gate **SQL Server** (hoy PENDING por ausencia de instancia).  
**No ejecutar contra producción.** Usar staging / LocalDB.  
**No** aplica `Migrate()` automático; el runtime usa `EnsureCreated` + `DatabaseInitializer` (parches idempotentes).

Documento hermano: [`FASE2H_VALIDATION_READINESS.md`](FASE2H_VALIDATION_READINESS.md).

---

## 1. Prerrequisitos

| Ítem | Requisito |
|------|-----------|
| Motor | SQL Server 2016+ (recomendado 2019/2022) o LocalDB / SQL Express |
| Herramientas | `sqlcmd` o SSMS; .NET SDK 8 (Web) |
| Red | App y SQL en red alcanzable; firewall puerto SQL |
| BD staging | p. ej. `RegNeps_2H` (vacía o clon controlado) |
| Login SQL | Windows Auth o SQL Auth con DDL + DML |
| App config | `Database:UseSqlServer=true` + `ConnectionStrings:RegNeps` (User Secrets / env; **no** commit de secretos) |

### Connection string de referencia (plantilla)

```text
Server=localhost\SQLEXPRESS;Database=RegNeps_2H;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true
```

O Azure/intranet:

```text
Server=SERVIDOR\INSTANCIA;Database=RegNeps_2H;User Id=...;Password=...;TrustServerCertificate=True;MultipleActiveResultSets=true
```

### Backup obligatorio antes de cualquier prueba

```sql
BACKUP DATABASE [RegNeps_2H]
TO DISK = N'D:\Backups\RegNeps_2H_pre_fase2h.bak'
WITH COPY_ONLY, INIT, COMPRESSION, STATS = 10;
```

Verificar restore en otra DB de prueba. Rollback = restore + binario anterior (ver `CHECKLIST_DESPLIEGUE_PARIDAD.md`).

---

## 2. Arranque fresh (instalación desde cero)

```powershell
cd <repo>

# Ejemplo con User Secrets (ajustar):
dotnet user-secrets set "Database:UseSqlServer" "true" --project src\RegNeps.Web
dotnet user-secrets set "ConnectionStrings:RegNeps" "Server=...;Database=RegNeps_2H;..." --project src\RegNeps.Web

dotnet run --project src\RegNeps.Web
```

En el primer arranque `DatabaseInitializer.InitializeAsync`:

1. `EnsureCreatedAsync`
2. `ApplySqlServerPatchesAsync` + `RoleSchemaPatches`
3. `DbSeeder.SeedAsync`
4. `CatalogSyncChangeWriter.EnsureBaselineAsync`
5. Repair ownership (si aplica datos migrados)

**PASS arranque:** app responde en `:5080`; login seed funciona; sin excepción de schema en log.

Cambiar contraseña del admin seed tras primer uso en cualquier entorno compartido.

---

## 3. Verificación de schema

Ejecutar en SSMS/`sqlcmd` contra `RegNeps_2H`.

### 3.1 Tablas mínimas

```sql
SELECT name FROM sys.tables
WHERE name IN (
  N'NepRecords', N'SyncChangeLogs', N'Fabrics', N'Lotes',
  N'Users', N'Roles', N'RolePermissions', N'RolePermissionAudits'
)
ORDER BY name;
```

**PASS:** existen todas.  
**FAIL:** falta alguna tras arranque.

### 3.2 Columnas sync / concurrencia en NepRecords

```sql
SELECT c.name, t.name AS type_name, c.max_length, c.is_nullable
FROM sys.columns c
JOIN sys.types t ON c.user_type_id = t.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.NepRecords')
  AND c.name IN (
    N'ClientOperationId', N'ConcurrencyStamp', N'CaptureSessionId',
    N'CreatedByUserId', N'IsDeleted' -- ajustar si el modelo usa tombstone distinto
  )
ORDER BY c.name;
```

**PASS:** `ClientOperationId`, `ConcurrencyStamp`, `CaptureSessionId` presentes (tipos nvarchar/uniqueidentifier según modelo EF).

### 3.3 SyncChangeLogs

```sql
SELECT c.name, t.name AS type_name
FROM sys.columns c
JOIN sys.types t ON c.user_type_id = t.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.SyncChangeLogs')
ORDER BY c.column_id;
```

Esperado (lógico): `Sequence` (bigint IDENTITY), `EntityType`, `EntityId` (uniqueidentifier), `ChangeType`, `OccurredAtUtc` (datetime2), `ActorUserId`, `OwnerUserId`, `ClientOperationId`, `DeviceId`, `PayloadJson` (nvarchar(max)).

### 3.4 Índices críticos

```sql
SELECT i.name, i.is_unique, i.has_filter, i.filter_definition
FROM sys.indexes i
WHERE i.object_id IN (OBJECT_ID(N'dbo.NepRecords'), OBJECT_ID(N'dbo.SyncChangeLogs'))
  AND i.name IN (
    N'IX_NepRecords_CreatedBy_ClientOperation',
    N'IX_NepRecords_CreatedBy_CaptureSession',
    N'IX_SyncChangeLogs_EntityType_EntityId',
    N'IX_SyncChangeLogs_Owner_Sequence',
    N'IX_SyncChangeLogs_ClientOperationId',
    N'IX_SyncChangeLogs_Actor_ClientOperation'
  )
ORDER BY i.name;
```

**PASS:** existen; `IX_*_Actor_ClientOperation` / `CreatedBy_ClientOperation` **unique + filter** sobre ClientOperationId no vacío.  
**FAIL:** índice ausente o no único (rompe idempotencia).

### 3.5 Idempotencia de patches

Reiniciar la app dos veces.  
**PASS:** segundo arranque sin error DDL.  
**FAIL:** “already exists” no absorbido o excepción no controlada.

---

## 4. Validación Sync (API / UI)

Usar cookie autenticada (navegador o cliente HTTP). Base URL: `http://localhost:5080`.

### 4.1 Push Create

1. Login usuario con permiso de captura.
2. `POST /api/sync/push` con Create + `ClientOperationId` = `C1`, `DeviceId` fijo.
3. **PASS:** Accepted; fila en `NepRecords`; fila ChangeLog Create/Upsert; `Sequence` asignado.
4. Reenviar mismo body. **PASS:** idempotente (Accepted/same), **sin** segunda fila de negocio.

### 4.2 Push Update

1. Anotar `ConcurrencyStamp`.
2. Update con stamp actual + nuevo `ClientOperationId`.
3. **PASS:** Accepted; stamp rotado; ChangeLog Update.
4. Update con stamp viejo. **PASS:** Conflict + snapshot servidor (no LWW).

### 4.3 Push Delete

1. Delete con stamp actual.
2. **PASS:** tombstone / registro eliminado según modelo; ChangeLog `RecordDeleted`.
3. Pull desde cursor anterior. **PASS:** cliente aplica delete; no resurrección.

### 4.4 ApplyCorrective

1. Operación `ApplyCorrective` (no alias Update).
2. **PASS:** ChangeType/OperationType específico; stamp rotado; Conflict si stamp stale.

### 4.5 Pull + cursor

1. `POST /api/sync/pull` con `AfterSequence` = 0 (o cursor local).
2. **PASS:** páginas ASC; `NextCursor` = max Sequence de página; `HasMore` coherente.
3. Segundo pull con cursor final. **PASS:** vacío / sin reaplicar.

---

## 5. ClearAll (dataset controlado)

```text
Insertar N registros (p. ej. N=50 o N=1000)
→ ClearAll (admin online)
→ N filas RecordDeleted en SyncChangeLogs (no un único RecordsCleared)
→ cliente con cursor antiguo → Pull → réplica sin registros vivos
→ Create posterior en servidor → Pull → solo el create nuevo
```

**PASS:** conteo ChangeLog Delete ≥ N (según implementación exacta por fila); réplica convergente; sin resurrect.  
**FAIL:** wipe sin tombstones suficientes; cliente conserva filas vivas tras Pull completo.

Medir duración wall-clock de ClearAll y del Pull completo (anotar N y hardware).

---

## 6. Concurrencia

| Caso | Pasos | PASS |
|------|-------|------|
| Stale update | Update online desde A; B push update stamp viejo | Conflict |
| Stale delete | Idem delete | Conflict o reject según contrato actual (documentar resultado real) |
| Stale ApplyCorrective | Idem | Conflict |

No auto-aceptar Conflict.

---

## 7. Paginación (dataset grande)

Sembrar ≥ 1 000 registros (ideal 10 000 en segunda pasada).

Desde UI Registros o vía `QueryPagedAsync` / API interna:

| Check | PASS |
|-------|------|
| Filtros | TotalCount refleja filtro en SQL |
| COUNT | `CountFilteredAsync` ≈ COUNT(*) filtrado |
| Página | `PageSize` ≤ 100; items = página pedida |
| Orden | Determinista entre requests iguales |
| OFFSET/FETCH | Plan SQL usa paginación servidor (opcional: capturar SQL EF) |

**Sustituto previo:** `RecordServerSidePaginationTests` en SQLite — **no equivalente**.

---

## 8. Performance básica (no benchmark formal)

Registrar en una hoja:

| Operación | N | Duración ms | Filas | Notas hardware |
|-----------|---|------------:|------:|----------------|
| ClearAll | | | | |
| Pull (todas las páginas) | | | | |
| QueryPaged página 1 | | | | |
| CountFiltered | | | | |

Sin umbrales PASS/FAIL numéricos hasta tener baseline en el hardware real.

---

## 9. Compatibilidad SQLite ↔ SQL Server

| Aspecto | SQLite (tests) | SQL Server (este gate) |
|---------|----------------|------------------------|
| Guid | TEXT | uniqueidentifier |
| Fechas | TEXT | datetime2 |
| Strings | TEXT | nvarchar(n)/max |
| Sequence | AUTOINCREMENT | IDENTITY bigint |
| Índices filtrados | WHERE … | WHERE … (sintaxis T-SQL) |
| Bootstrap | EnsureCreated + patches | Igual camino código |

**Criterio de equivalencia lógica:** mismas tablas/columnas/índices semánticos y mismos resultados de Push/Pull/Conflict/ClearAll — no igualdad bit a bit de tipos de almacenamiento.

### Upgrade desde versión anterior

1. Restaurar backup de BD **pre-2B/2C** (sin SyncChangeLogs) en staging.
2. Arrancar binario actual.
3. **PASS:** tabla SyncChangeLogs + índices creados; app usable; seed/catálogos baseline sin duplicar de forma incorrecta.
4. Comparar lista de columnas/índices con fresh install (§3) — deben coincidir lógicamente.

---

## 10. Seed y datos mínimos

Tras arranque:

- Roles sistema (Operario…SuperAdmin)
- Usuario admin seed (cambiar password)
- Baseline ChangeLog de catálogos Fabric/Lote (si hay catálogo)

Para sync: al menos un usuario con `Capture`/`View`; opcional segundo usuario sin `SeesAll` para authZ.

---

## 11. Criterios PASS / FAIL del gate SQL Server

| Resultado | Condición |
|-----------|-----------|
| **PASS** | Schema §3 OK; Sync §4 OK; ClearAll §5 OK; Concurrencia §6 OK; Paginación ≥1k §7 OK |
| **PASS CON WARNINGS** | Funcional OK pero sin dataset 10k/50k o sin métricas perf |
| **FAIL** | Índice de idempotencia ausente; Conflict LWW; ClearAll sin N tombstones; crash en patches; divergencia fresh vs upgrade |

---

## 12. Relación con Android

Este gate puede cerrarse **sin** Android (API + Web).  
El gate **SQL Server + Android** exige además escenarios A–H de `FASE2H_VALIDATION_READINESS.md` §7–8 contra esta misma BD staging.
