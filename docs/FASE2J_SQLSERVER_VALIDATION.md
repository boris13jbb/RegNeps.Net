# FASE 2J — Validación física SQL Server

**Gate final: PASS CON WARNINGS**

Se encontraron 2 defectos reales, visibles solo en SQL Server. Ambos se corrigieron con un cambio mínimo, tienen prueba de regresión y se revalidaron en SQL Server real. No se tocó el protocolo Push/Pull, el modelo de conflictos, los tombstones, los criterios NEPS, los permisos, SignalR, OfflineStore ni la estrategia `EnsureCreated + patches`.

## 1. Entorno

| Elemento | Valor |
|---|---|
| Motor | Microsoft SQL Server 2025 Express (RTM-GDR) KB5122770 — 17.0.1135.8 (X64) |
| Instancia | `.\SA` local, autenticación Windows (sin usuario/contraseña en archivos) |
| Collation servidor | `Modern_Spanish_CI_AS` |
| Runtime | .NET 8.0.31 (SDK 10.0.400-preview); EF Core 8.0.17 (SqlServer + Sqlite) |
| Fecha | 2026-10-05 |
| Bases usadas | `RegNeps_Validation_2J_{escenario}_{hex}` (una por prueba) y `RegNeps_Validation_2J_WebSmoke`; todas eliminadas al terminar. No se usó ninguna base productiva. |

### Cómo repetir

```powershell
$env:REGNEPS_SQLSERVER_VALIDATION = "Server=<instancia>;Integrated Security=True;TrustServerCertificate=True;Encrypt=False"
dotnet test tests/RegNeps.Tests/RegNeps.Tests.csproj --filter "FullyQualifiedName~RegNeps.Tests.SqlServer"
# Opcional: conservar las bases para inspección
$env:REGNEPS_SQLSERVER_VALIDATION_KEEP = "1"
```

- La cadena es la del servidor, sin `Database`. El arnés crea y elimina sus propias bases `RegNeps_Validation_2J_*`.
- Sin la variable, las pruebas `[SqlServerFact]` / `[SqlServerTheory]` quedan **omitidas**, nunca superadas. Así el CI no puede marcar como PASS algo que no se ejecutó en SQL Server.
- Arnés: `tests/RegNeps.Tests/SqlServer/SqlServerValidationDatabase.cs`. Usa el DI real: `AddRegNepsInfrastructure(cs, useSqlServer: true)` y `DatabaseInitializer.EnsureDatabaseCreatedAsync`, sin mocks ni proveedores sustitutos.

## 2. Bootstrap

**Parte A.** En Web no se usa `Database.Migrate()`. El arranque es `EnsureCreated` → parches aditivos (`DatabaseInitializer`, `RoleSchemaPatches`) → `DbSeeder` → `CatalogSyncChangeWriter.EnsureBaselineAsync`. No existe `__EFMigrationsHistory`, y esto se verifica en la prueba de base nueva.

| Escenario | Resultado | Evidencia |
|---|---|---|
| Base nueva (fresh) | **PASS** | `Fresh_Bootstrap_Creates_Schema_Indexes_Seeds_And_Catalog_Baseline`: tablas, tipos de columna, índices filtrados únicos (`IX_SyncChangeLogs_Actor_ClientOperation`, `IX_NepRecords_CreatedBy_ClientOperation`), FK en cascada, seeds (admin, 2 telas, 16 lotes, AlertConfig, 5 roles) y baseline de catálogo = 18 `CatalogUpserted`, sin change logs de NepRecord |
| Bootstrap repetido (×4 con datos) | **PASS** | `Repeated_Bootstrap_Is_Idempotent_And_Preserves_Data`: firmas de columnas e índices idénticas; mismos conteos; huella (CHECKSUM) de change logs y `ConcurrencyStamp` sin cambios; sin columnas, índices ni seeds duplicados |
| Upgrade desde esquema previo a sync | **PASS** | `Upgrade_From_PreSync_Schema_Preserves_Data_And_Converges_With_Fresh`: se eliminan `ClientOperationId`/`CaptureSessionId`/`ConcurrencyStamp`, sus índices, `IX_Fabrics_Name_Unique` y la tabla `SyncChangeLogs`. Tras re-bootstrap: datos conservados, stamps rellenados y distintos, baseline recreado, Create idempotente operativo y un arranque posterior sin cambios. Única diferencia con una base nueva: `IX_SyncChangeLogs_Sequence` (ver W4) |
| Arranque real de `RegNeps.Web` (Release) ×3 | **PASS** | `Database__UseSqlServer=true` y cadena solo en variables de la sesión. HTTP 200 en `/login?ReturnUrl=/`. Tras 3 arranques: 1 usuario, 2 telas, 16 lotes, 5 roles, 18 change logs y 224 índices, idénticos |

## 3. Matriz de escenarios

Todas las pruebas están en `tests/RegNeps.Tests/SqlServer/` y se ejecutaron contra SQL Server real.

| Parte | Escenario | Prueba | Resultado |
|---|---|---|---|
| E | Límites NEPS 18/19/45/46/54/55, `NEPS/m = Q/0.09`, `AlertasActivas` separado de la calidad, sin `LimiteNormalMax`/`LimiteAdvertenciaMax` | `Neps_Boundaries_Quality_And_Formula_Persist_On_SqlServer` | **PASS** (Q=0: ver W2) |
| F | Create + ChangeLog atómico; un fallo en el ChangeLog revierte el registro (online y Push) | `Online_Create_Writes_Record_And_ChangeLog_Together`, `Create_With_Failing_ChangeLog_Rolls_Back_Record_Online_And_Push` | **PASS** |
| G | Idempotencia por `ClientOperationId` (conteos antes/después; un INSERT duplicado directo falla con 2601/2627) | `Push_Create_Same_ClientOperationId_Is_Duplicate_Without_New_Rows` | **PASS** |
| H | Update con stamp correcto aceptado; con stamp viejo → `Conflict` con snapshot y `ServerConcurrencyStamp`, sin mutación ni LWW | `Update_Correct_Stamp_Accepted_Then_Old_Stamp_Conflict_Without_Mutation` | **PASS** |
| I | Delete + tombstone atómico; el Pull lo ve; el reintento es idempotente | `Delete_Writes_Tombstone_Atomically_Pull_Sees_It_And_Retry_Is_Idempotent` | **PASS** |
| J | ApplyCorrective solo modifica campos de revisión; stamp viejo → `Conflict`; un fallo del ChangeLog no muta nada | `ApplyCorrective_Updates_Review_Fields_Only_And_Stale_Stamp_Conflicts` | **PASS** |
| K | ClearAll: N tombstones, X→ClearAll→Y por Pull paginado, un Create posterior sobrevive, rollback total ante un fallo; Operario y Supervisor (sin `ClearAllRecords`) rechazados | `ClearAll_Emits_One_Tombstone_Per_Record_Pull_Paginates_And_New_Create_Survives` | **PASS** |
| L | Cursor 0 / N, PageSize pequeño, `HasMore`, orden monótono, sin duplicados ni pérdidas, X→Update→Delete→Pull | `Pull_Cursor_Pages_Are_Ascending_Without_Loss_Or_Duplicates` | **PASS** |
| L | Una Sequence menor sin confirmar no se salta | `Pull_Does_Not_Skip_Uncommitted_Lower_Sequence_With_Read_Committed_Snapshot` | **PASS tras corregir D2** |
| M | Paginación server-side: `ORDER BY CreatedAt DESC, Id DESC` con `OFFSET/FETCH` (SQL capturado), PageSize ≤ 100, COUNT, ownership, SeesAll, sin materializar todo | `Records_Server_Side_Pagination_Filters_Order_Ownership_And_SeesAll` | **PASS** |
| N | Tela/LoteTrama emiten `CatalogUpserted`/`CatalogDeleted` y el Pull los entrega | `Fabric_And_Lote_Mutations_Emit_Catalog_ChangeLogs_And_Pull_Them` | **PASS** |
| O | 8 escritores con el mismo stamp: exactamente 1 `Accepted` y el resto `Conflict` | `Concurrent_Updates_With_Same_Stamp_Exactly_One_Accepted_Rest_Conflict` | **PASS** |
| O | Reintentos concurrentes del mismo `ClientOperationId` (Update, ApplyCorrective y Delete; 5 rondas × 8 escritores): 1 `Accepted` y 7 `Duplicate`, 0 `Conflict`, 1 ChangeLog y un único efecto | `Concurrent_Retries_With_Same_ClientOperationId_One_Accepted_Others_Duplicate` (teoría ×3) | **PASS tras corregir D1 (2J y 2J.1)** |
| P | Incompatibilidades SQLite → SQL Server | Sección 5 | **PASS CON WARNINGS** |
| Q | `dotnet test` y Web Release | Sección 6 | **PASS** |

## 4. Defectos

### D1 — Reintento concurrente del mismo `ClientOperationId` devolvía `Conflict`

- **Síntoma.** Con 4 Push concurrentes de la misma operación Update (mismo opId y stamp), el resultado fue `Duplicate, Duplicate, Accepted, Conflict CONCURRENCY`. El cliente recibía un conflicto falso de una operación que sí se había aplicado.
- **Causa raíz.** En `AtomicNepRecordCreateStore.UpdateWithChangeLogAsync` y `ApplyCorrectiveWithChangeLogAsync`, la transacción perdedora leía el stamp anterior y quedaba bloqueada en el UPDATE por el bloqueo de fila de la ganadora. Al confirmar la ganadora, el UPDATE afectaba 0 filas y lanzaba `DbUpdateConcurrencyException`. Ese `catch` devolvía `Conflict` sin consultar si la pareja (actor, opId) ya estaba procesada; el `catch (DbUpdateException)` hermano sí lo consulta. En SQLite no aparecía porque las escrituras se serializan. Delete ya estaba cubierto, porque `DbUpdateConcurrencyException` deriva de `DbUpdateException`.
- **Corrección.** En ambos `catch (DbUpdateConcurrencyException)`, después del rollback, si hay opId se llama a `FindProcessedByClientOpAsync`. Si se encuentra, se devuelve el resultado idempotente existente (`ResolveIdempotentUpsertAsync`); si no, se mantiene el `Conflict` original. Archivo: `src/RegNeps.Infrastructure/Sync/AtomicNepRecordCreateStore.cs`.
- **Prueba de regresión.** `Concurrent_Retries_With_Same_ClientOperationId_One_Accepted_Others_Duplicate` (UpdateRecord y ApplyCorrective).
- **Resultado.** PASS en SQL Server: 1 `Accepted` y 5 `Duplicate`. La suite SQLite completa sigue verde.
- **Reapertura en 2J.1.** Al ejecutar por TCP (2J había usado memoria compartida), ApplyCorrective volvió a dar
  `1 Accepted, 4 Duplicate, 1 Conflict CONCURRENCY`. Hubo una segunda carrera, distinta de la primera:
  - La TX ganadora confirmaba **entre** la primera comprobación del opId y la lectura del registro.
  - Con RCSI, la perdedora leía el stamp nuevo sin bloquearse y entraba en la rama «stamp distinto», que devolvía
    `Conflict` sin volver a mirar el opId.
  - En Delete, la rama equivalente es «registro ya borrado», que devolvía `Invalid ENTITY_DELETED`.
- **Corrección 2J.1.** Se añade `FindProcessedAfterRaceAsync`, una segunda comprobación del `ClientOperationId` antes de
  responder `Conflict` por stamp distinto en Update, ApplyCorrective y Delete, y antes de `ENTITY_DELETED` en Delete. La
  reutilizan también los `catch (DbUpdateConcurrencyException)` de 2J.
  - Si el opId ya está procesado, se devuelve el resultado idempotente existente (`ResolveIdempotentUpsertAsync` /
    `ResolveIdempotentDelete`), sin mutación ni change log nuevos.
  - Si no, el comportamiento es el de antes.
  - No cambian el protocolo, los DTO ni el modelo de conflictos.
- **Regresión 2J.1.** La teoría tiene ahora 3 casos (UpdateRecord, ApplyCorrective, DeleteRecord), con 5 rondas de 8
  escritores cada uno. Cada ronda comprueba:
  - 1 `Accepted`, 7 `Duplicate` y 0 `Conflict`;
  - que un reintento posterior a la carrera da `Duplicate`;
  - que hay un único change log y un único efecto en el registro;
  - un control con otro opId y stamp obsoleto, que sigue siendo `Conflict` (o `ENTITY_DELETED` en Delete).
- **Resultado 2J.1.** PASS por TCP (`tcp:[::1],1433`).
  - Sin la corrección, la misma prueba falla en Update y ApplyCorrective con `Conflict CONCURRENCY`, así que la regresión
    detecta el defecto.
  - La carrera de Delete no llegó a reproducirse en 5 rondas: su rama queda cubierta por consistencia con las otras dos,
    no por un fallo observado.

### D2 — El Pull podía saltarse para siempre una Sequence aún no confirmada

- **Síntoma.** Con una transacción abierta que ya tenía asignada la `Sequence` 19 y la 20 confirmada, el Pull devolvió `[20]` con `NextCursor=20`. Tras confirmarse la 19, el Pull desde 20 no la devuelve nunca: **pérdida permanente del cambio para el cliente offline**.
- **Causa raíz.** EF Core SQL Server (8.0.17) ejecuta `SET READ_COMMITTED_SNAPSHOT ON` al crear la base desde `EnsureCreated`. La cadena aparece en `Microsoft.EntityFrameworkCore.SqlServer.dll` y se confirmó en `sys.databases`, mientras que `model` está en OFF. Con RCSI, el barrido `WHERE Sequence > cursor ORDER BY Sequence` no ve la fila sin confirmar y avanza el cursor más allá de ella. Como IDENTITY no garantiza confirmación en orden, el cursor monótono queda incompleto.
- **Corrección.** Solo en SQL Server, `SyncPersistence.PullAsync` lee el barrido del cursor con `WITH (READCOMMITTEDLOCK)` (`ChangeLogsForCursorScan`), así que espera a las filas sin confirmar en lugar de saltarlas. Se mantienen el mismo contrato, cursor y paginación. SQLite sigue igual. No se cambió el nivel de aislamiento de la base. Archivo: `src/RegNeps.Infrastructure/Sync/SyncPersistence.cs`.
- **Prueba de regresión.** `Pull_Does_Not_Skip_Uncommitted_Lower_Sequence_With_Read_Committed_Snapshot`.
- **Resultado.** PASS: el Pull espera a la confirmación y devuelve `[19,20]`. El resto de pruebas de Pull y ClearAll siguen en PASS.

## 5. Warnings y observaciones (Parte P)

- **W1.** `AlertConfigs` conserva las columnas legacy `LimiteNormalMax`/`LimiteAdvertenciaMax` en el esquema. La calificación no las usa: se verificó que la calidad sale de `NepsQualityCriteria`/`AlertEvaluator`.
- **W2.** Q=0 se clasifica como OK en el evaluador, pero no puede persistirse: la captura exige `Neps > 0` (`NepRecordService`; Push devuelve `Invalid`). Es una regla de negocio existente que no depende del proveedor y no se modificó.
- **W3.** EF Core 7+ usa `OUTPUT` sin `INTO`, que es incompatible con triggers en `NepRecords`/`SyncChangeLogs`. No deben añadirse triggers a esas tablas. Para forzar fallos, las pruebas usaron CHECK constraints temporales.
- **W4.** `IX_SyncChangeLogs_Sequence` existe solo en bases nuevas. En bases actualizadas no aparece, sin impacto funcional porque la PK clúster ya es `Sequence`.
- **W5.** La unicidad sin distinguir mayúsculas de `IX_Fabrics_Name_Unique` depende del collation del servidor (CI en `Modern_Spanish_CI_AS`).
- **W6.** SQL Server ordena `uniqueidentifier` distinto que .NET y SQLite. El desempate `Id DESC` es determinista pero puede diferir entre proveedores; no hay defecto.
- **W7.** Un rollback deja huecos en IDENTITY. El cursor los tolera, pero no debe suponerse continuidad de `Sequence`.
- **W8.** Tras D2, un Pull concurrente espera a que terminen las transacciones de escritura abiertas (p. ej. un ClearAll con muchos tombstones). Queda una ventana teórica, no reproducida, entre la asignación de IDENTITY y el bloqueo de la fila dentro de la misma sentencia.
- **W9.** Las consultas a `sys.*` mezclan el collation del catálogo (`Latin1_General_CI_AS_KS_WS`) con el de la base y requieren `COLLATE DATABASE_DEFAULT`. Afectó solo al arnés de pruebas, no al producto.
- **W10.** La validación se hizo en SQL Server 2025 Express local. Una base intranet creada previamente por un DBA puede tener RCSI OFF; la corrección D2 funciona en ambos casos.

## 6. Pruebas y build

| Comando | Resultado |
|---|---|
| `dotnet test` con `REGNEPS_SQLSERVER_VALIDATION` | 536 superadas (518 + 18 casos SQL Server), 0 con error |
| `dotnet test` sin la variable | 518 superadas, 17 omitidas (SQL Server), total 535, 0 con error |
| `dotnet build src/RegNeps.Web/RegNeps.Web.csproj -c Release` | 0 advertencias, 0 errores |

La diferencia 536 / 535 no es un test perdido: la suite SQL Server tiene 17 métodos. Uno de ellos es la teoría
`Concurrent_Retries_With_Same_ClientOperationId_One_Accepted_Others_Duplicate`, que en 2J tenía 2 casos (UpdateRecord y
ApplyCorrective). Cuando está omitida, xUnit no expande sus datos y la cuenta como 1 resultado; cuando se ejecuta, la
cuenta como 2. Por eso había 18 casos ejecutados frente a 17 omitidos. En 2J.1 la teoría pasó a tener 3 casos (se añadió
DeleteRecord): ahora son 19 ejecutados (537 en total) frente a 17 omitidos (535).

## 7. Pendientes

| Elemento | Estado |
|---|---|
| Android E2E (FASE 2K) | **PENDING** |
| Prueba de carga formal (volumen/latencia) | **PENDING** |
| Repetir esta suite en la instancia intranet real (versión y collation de destino) | **PENDING** |
| Parte O: concurrencia real (stamps, reintentos por opId) | PASS |
| Resto de partes A–N | PASS |

## FASE 2J.1 — Validación en instancia real de intranet

**Resultado final: PASS** (2026-10-05, HEAD `3f99e54`).

El responsable del proyecto confirmó que `<SERVIDOR>\SA` **es el servidor SQL Server real de RegNeps**: la Web se
ejecuta en el mismo equipo y se conecta en local. No existe otra instancia de intranet. La revalidación final sobre ese
servidor por TCP dio:

| Elemento | Valor |
|---|---|
| Servidor/instancia | `<SERVIDOR>\SA` (conexión de los tests: `tcp:[::1],1433`, TCP + NTLM) |
| Versión / edición | SQL Server 2025 RTM-GDR 17.0.1135.8 / Express Edition (64-bit) |
| Collation | `Modern_Spanish_CI_AS` |
| RCSI | ON en las bases creadas por `EnsureCreated` (OFF en `model`) |
| Suite SQL Server | 19 passed / 0 failed / 0 skipped |
| Suite completa | 537/537 |
| Web Release | 0 advertencias, 0 errores |
| Bases `RegNeps_Validation%` restantes | 0 |

Resultados por bloque:
- **D1:** PASS en UpdateRecord, ApplyCorrective y DeleteRecord.
- **D2:** PASS.
- **Bootstrap, Pull/cursor, ClearAll/tombstones, paginación, catálogos y concurrencia:** PASS.

**Exposición de red.** SQL Server escucha solo en loopback, porque solo se conecta la propia Web. Los clientes, Android
incluido, usan la Web y no SQL Server. Si en el futuro otro equipo necesitara conectarse directamente, habría que
habilitar la IP de LAN y una regla de firewall restringida a esa subred.

Las subsecciones siguientes conservan el bloqueo registrado antes de esa confirmación.

### Evidencia del bloqueo (antes de la confirmación)

- `src/RegNeps.Web/appsettings.Production.json` y `docs/DESPLIEGUE_INTRANET.md` solo contienen el marcador
  `SERVIDOR\INSTANCIA`, sin nombre de servidor real.
- `RegNeps.Web` no tiene User Secrets (`UserSecretsId` no configurado).
- No existen variables de entorno `REGNEPS_*`, `ConnectionStrings__*` ni `Database__*` en los ámbitos de usuario, máquina
  o proceso.
- El único servicio SQL del equipo es `MSSQL$SA`, el SQL Server 2025 Express local ya usado en 2J.
- `sqlcmd -L` no devuelve ningún servidor anunciado en la red.

### Entorno

| Elemento | Valor |
|---|---|
| SQL Server / versión / edición | BLOCKED — no identificado |
| Instancia | BLOCKED — no proporcionada |
| Collation | BLOCKED |
| RCSI | BLOCKED |
| BD de validación | No creada |

### Tests

```text
SQL Server suite (instancia intranet): no ejecutada — BLOCKED
Regresión local sin variable: 518 passed / 0 failed / 17 skipped / 535 total
```

| Bloque | Estado |
|---|---|
| D1 — idempotencia concurrente | BLOCKED |
| D2 — Pull sin saltos con RCSI | BLOCKED |
| Bootstrap | BLOCKED |
| Pull/cursor | BLOCKED |
| Concurrencia | BLOCKED |
| Collation | BLOCKED |
| Web Release | 0 advertencias, 0 errores |

### Diferencias respecto a Express local

No determinables sin acceso a la instancia real.

### Para desbloquear

1. Obtener el nombre `SERVIDOR\INSTANCIA` de la intranet y una cuenta con permiso para crear y borrar bases
   `RegNeps_Validation_2J_*`, con autenticación integrada o credenciales fuera del repositorio.
2. Ejecutar las consultas de identificación de la instancia, en la Parte C del encargo 2J.1.
3. Ejecutar la suite:

   ```powershell
   $env:REGNEPS_SQLSERVER_VALIDATION = "Server=<SERVIDOR\INSTANCIA>;Integrated Security=True;TrustServerCertificate=True;Encrypt=False"
   dotnet test tests/RegNeps.Tests/RegNeps.Tests.csproj --filter "FullyQualifiedName~RegNeps.Tests.SqlServer"
   ```

   Esperado: 19 superadas, 0 con error. La suite crea y elimina sus propias bases y no toca la productiva.

   Si la instancia de intranet tiene RCSI OFF por defecto en `model`, la prueba D2 lo detecta igualmente: las bases las
   crea `EnsureCreated` y EF Core activa RCSI al crearlas.

### Revalidación local por TCP (2026-10-05)

La instancia sigue siendo **local**, no de intranet: esto no cambia el estado BLOCKED de 2J.1 respecto a la intranet.

- **Instancia:** `<SERVIDOR>\SA`, SQL Server 2025 Express 17.0.1135.8, `Modern_Spanish_CI_AS`.
- **TCP:** 1433 estático, solo en loopback (`127.0.0.1` y `::1`). SQL Browser deshabilitado y sin reglas de firewall.
- **Autenticación:** Windows (NTLM).
- **Cadena de los tests:** `Server=tcp:[::1],1433;Integrated Security=True;TrustServerCertificate=True;Encrypt=False`.
  Con autenticación integrada, `127.0.0.1,1433` falla con el error 18452 y `localhost,1433` es rechazado por SqlClient.
- **Primera ejecución por TCP:** 17/18 superadas. Falló D1 en ApplyCorrective; ver la reapertura de D1 en la sección 4.
- **Tras la corrección:**
  - suite SQL Server: 19 superadas, 0 con error, 0 omitidas;
  - suite completa: 537/537;
  - sin la variable: 518 superadas y 17 omitidas;
  - Web Release: 0 advertencias, 0 errores.
