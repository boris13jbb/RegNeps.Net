# FASE 2D.11 — Diseño y auditoría de ClearAll sync-safe

**Tipo:** auditoría + diseño (sin implementación).  
**HEAD de partida:** `934656e` (FASE 2D.10 ApplyCorrective offline).  
**Alcance:** no modificar `src/`, protocolo productivo, SQLite, SyncEngine, ClearAll ni DeleteMany.

---

## 1. Estado actual

### 1.1 Implementación

| Capa | Evidencia | Comportamiento |
|------|-----------|----------------|
| UI Blazor | `Registros.razor` → `ClearAllAsync` | Modal «Vaciar todos los registros»; permiso UX `ClearAllRecords`; revalida con `CurrentUser.HasAsync` |
| Servicio | `NepRecordService.ClearAllAsync` | Exige `AppPermission.ClearAllRecords`; si `ISyncPersistence` inyectado y `CountChangeLogsAsync() > 0` → `InvalidOperationException`; si no, llama repositorio |
| Repositorio | `NepRecordRepository.ClearAllAsync` | `CorrectiveActions.ExecuteDeleteAsync` + `NepRecords.ExecuteDeleteAsync` — **borrado físico de toda la tabla** |
| Tombstones / ChangeLog | — | **No** genera `RecordDeleted` ni ninguna fila `SyncChangeLog` |
| Tests | `SyncApplyCorrectiveChangeLogTests` | Bloqueo con logs; permitido sin logs y sin producir filas sync |

### 1.2 Alcance semántico real

ClearAll **no** significa «registros visibles del usuario» ni «sesión de captura».

Significa:

> Eliminar **todos** los `NepRecord` (y sus `CorrectiveActionEntry`) de la base de datos del servidor.

No filtra por:

* `CreatedByUserId` / ownership;
* `SeesAll`;
* `CaptureSessionId`;
* rango de fechas;
* selección UI.

Es un vaciado **global de tabla** administrativo.

### 1.3 Permisos

* Catálogo: `AppPermission.ClearAllRecords` (sensible).
* Matriz sembrada: SuperAdmin y Admin; **no** Operario / Supervisor / Gerencia.
* Servidor: `NepRecordService` rechaza sin permiso (`UnauthorizedRecordAccessException`).
* UI: oculta/deshabilita si no hay permiso; además revalida antes de ejecutar.
* **No** hay ClearAll offline ni `OfflineOperationType` para ClearAll.

### 1.4 Relación con DeleteMany

`DeleteManyAsync` **no** es ClearAll:

* Permiso: `DeleteRecords`.
* Loop de `DeleteAsync` por ID.
* Con store atómico: cada delete → `DeleteWithTombstoneAsync` → `RecordDeleted` + borrado físico de la fila + `ConcurrencyStamp`/ownership.
* Compatible con Pull offline (N eventos).
* Fallos parciales: resumen `BatchDeleteResult` (continúa).

### 1.5 Guard frente a SyncChangeLog

```text
if CountChangeLogsAsync() > 0 → bloquear ClearAll
```

`CountChangeLogsAsync` cuenta **todas** las filas de `SyncChangeLogs` (`EntityType` NepRecord **y** CatalogItem).

**Implicación operativa (2D.9):** `DatabaseInitializer` / `EnsureCatalogBaselineAsync` escribe ChangeLogs de catálogo. En cualquier BD con baseline de Fabric/Lote, ClearAll queda **efectivamente bloqueado de forma permanente**, aunque no haya habido sync de NepRecord.

Esto no impide el análisis; es un hallazgo de diseño del guard actual (granularidad demasiado gruesa). Una futura 2D.12 debe redefinir el criterio (p. ej. solo logs NepRecord, o sustituir el bloqueo por la estrategia sync-safe).

### 1.6 Efectos colaterales conocidos

| Área | Efecto |
|------|--------|
| `SavedReport` | **No** se borran. Tienen `SnapshotJson` independiente. UI informa: al reabrir, la tabla viva puede salir vacía. |
| Dashboard / alertas | Se vacían al consultar la tabla viva (sin ChangeLog de clear). |
| Export / informes | Datos vivos desaparecen; snapshots de informes guardados permanecen. |
| Integridad referencial | FK `CorrectiveActions → NepRecords` (cascade vía delete previo de CorrectiveActions). Sin FK desde SavedReport a NepRecord. |
| Clientes offline | **Sin aviso**: no reciben tombstones → réplicas locales conservan fantasmas (ver §2). |
| Auditoría de eliminaciones | ClearAll **no** deja rastro en `SyncChangeLog`. Delete individual sí (`RecordDeleted` con actor/owner/stamp). |

### 1.7 Modelo actual de tombstones (Delete)

Por cada `DeleteRecord` Accepted (online o Push):

1. Transacción: validar permisos/ownership/stamp.
2. Insertar `SyncChangeLog` (`ChangeType = RecordDeleted`, payload tombstone: `id`, `ownerUserId`, `deletedAtUtc`, `lastConcurrencyStamp`).
3. Borrar físicamente `NepRecord` (+ historial según EF).
4. Pull autorizado filtra por `OwnerUserId` (o SeesAll).
5. `SyncEngine.ApplyDeleteAsync`: marca `LocalNepRecord.IsDeleted`, crea marcador local si no existe, pone `PendingOperation` Pending → `Conflict` + `ENTITY_DELETED`.

Cursor = `Sequence` global monotónica. Página con `HasMore` / `NextCursor`. ChangeTypes desconocidos: el cliente **avanza cursor** sin aplicar (ignorados).

---

## 2. Problema de consistencia

### 2.1 Por qué el ClearAll físico es incompatible

El sistema offline-first asume:

* mutaciones visibles en el futuro vía **log append-only** (`SyncChangeLog`);
* clientes con **cursor incremental** (`LastPulledSequence`);
* réplicas **parciales** (filtro ownership / SeesAll);
* borrados propagados como **tombstones** (`RecordDeleted`), no como ausencia silenciosa.

Un ClearAll físico sin eventos:

* elimina hechos del servidor sin dejar evidencia en el log;
* no mueve el cursor de forma útil para comunicar «estas entidades murieron»;
* deja a clientes atrasados con datos que el servidor ya no tiene;
* permite que Push posteriores sobre EntityIds borrados se comporten como `ENTITY_DELETED` / `Invalid`, pero **solo si el cliente intenta mutar**; la réplica local de lectura **no se corrige sola**.

### 2.2 Escenario formal

```text
1. Cliente A hace Pull hasta cursor X. Local tiene R1…Rk (ServerRecordId).
2. Admin B ejecuta ClearAll físico (hoy bloqueado si hay logs; hipotético sin guard).
3. Servidor borra R1…Rk sin RecordDeleted.
4. Cliente A permanece offline (sigue mostrando R1…Rk).
5. A vuelve online.
6. A solicita Pull(cursor = X).
```

**Qué recibe A hoy (hipotético ClearAll físico con logs previos ya existentes / sin eventos de clear):**

* Nada que indique eliminación de R1…Rk.
* Solo cambios con `Sequence > X` (p. ej. nuevos Create).
* SQLite de A **conserva** R1…Rk como Synced → **fantasmas**.
* Dashboard/listas offline mienten respecto al servidor.

**Qué necesitaría A para no resucitar eliminados:**

* O bien **N** `RecordDeleted` (uno por EntityId que A está autorizado a ver), aplicados con la lógica actual de `ApplyDeleteAsync`;
* O bien un evento de ámbito (`RecordsCleared`) con **marca de agua** (secuencia/tiempo/alcance) que instruya a borrar/retirar la réplica local **sin** tocar Creates locales no enviados;
* O bien un **resync completo** (cursor reset + snapshot baseline) — fuera del protocolo fino actual.

Sin una de esas tres, la consistencia eventual **falla**.

---

## 3. Alternativa A — N tombstones

### 3.1 Diseño conceptual

```text
ClearAll (online, admin)
  → para cada NepRecord existente:
       DeleteWithTombstone (o equivalente batch)
         → SyncChangeLog RecordDeleted
         → DELETE físico fila
  → commit(s) controlados
```

Semántica wire: **idéntica** a DeleteMany a escala de tabla. No requiere nuevo `ChangeType` ni bump de `ProtocolVersion` para el caso base.

### 3.2 Análisis

| Dimensión | Evaluación |
|-----------|------------|
| Atomicidad | Ideal: una TX con N deletes+logs. Riesgo timeout/locks en SQL Server a gran N. Práctico: batches (p. ej. 500–2000) con checkpoint de progreso admin. |
| Volumen de eventos | O(N) ChangeLogs. N = conteo actual de NepRecords. |
| Tamaño ChangeLog | Payload tombstone pequeño (~id+owner+stamp+fecha). Dominado por filas, no por bytes/payload. |
| Tiempo | Lineal en N; indexes en `Sequence` ayudan a Pull; insert masivo puede saturar log/TX. |
| Clientes offline días | Recuperan vía Pull paginado (`HasMore`); pueden tardar muchas páginas. Correcto semánticamente. |
| Pull atrasado | Garantizado: cada EntityId autorizado produce `RecordDeleted` visible según ownership. |
| Retención | N tombstones viven hasta política de purge. Millones ⇒ presión de almacenamiento. |
| Compactación | Futura: watermark / RecordsCleared de compactación (fase posterior). |
| SQL Server | Locks de tabla/índice; riesgo timeout; conviene batches + índice Sequence ya existente. |
| SQLite cliente | N ApplyDelete: marcadores `IsDeleted`; Outbox Pending → Conflict ENTITY_DELETED. |
| Red | O(N/pageSize) round-trips Pull. |
| Registros fuera de ventana local | Si el cliente nunca tuvo Ri, recibe tombstone (SeesAll) o no (Operario sin ownership) — inocuo; marcador local opcional evita resurrección. |
| Subconjunto local | **Sí** garantiza recuperación: solo necesita tombstones de EntityIds que conoce o que está autorizado a ver. |

### 3.3 Compatibilidad con réplica parcial

Pull ya filtra por `OwnerUserId` / SeesAll. Un Operario solo recibe tombstones de **sus** registros. Eso coincide con lo que puede tener en SQLite vía sync normal. Correcto.

---

## 4. Alternativa B — `RecordsCleared`

### 4.1 Diseño conceptual

Nuevo `ChangeType` (requeriría extensión de protocolo; probablemente `ProtocolVersion` ≥ 2 o acuerdo de «ignorar desconocidos» ya existente + handler nuevo):

```json
{
  "scope": "AllNepRecords",
  "clearedAtUtc": "...",
  "clearedThroughSequence": 12345,
  "actorUserId": "...",
  "clientOperationId": "...",
  " NepRecordCount": 98765
}
```

Campos mínimos razonables:

| Campo | Rol |
|-------|-----|
| `Sequence` | Cursor / orden total |
| `scope` | `AllNepRecords` (alineado al ClearAll actual) |
| `actorUserId` | Auditoría |
| `clearedAtUtc` | UX / desempate |
| `clearedThroughSequence` | Marca de agua: no borrar Creates con ChangeLog Sequence > watermark |
| `clientOperationId` | Idempotencia del clear (si ClearAll se vuelve reintentable) |
| `ownerScope` | Opcional futuro; hoy ClearAll es global |

`OwnerUserId` del log: no puede ser un usuario concreto. Opciones: sintético tipo `catalog` (`"clearall"`) + regla Pull «todo autenticado», o fan-out N eventos por owner (degenera hacia A).

### 4.2 Comportamiento Pull → SQLite

```text
Pull → RecordsCleared(scope=All, through=S)
  → para cada LocalNepRecord con ServerRecordId != null
       cuyo origen servidor sea ≤ watermark:
         IsDeleted = true; SyncStatus = Synced
  → Pending Update/Delete/ApplyCorrective sobre esos EntityId → Conflict ENTITY_DELETED
  → NO tocar LocalNepRecord sin ServerRecordId con Create Pending
  → NO tocar catálogos
```

### 4.3 Riesgos específicos

| Riesgo | Detalle |
|--------|---------|
| Wipe de Create offline no enviado | Si el cliente borra «todo lo local», pierde trabajo. **Prohibido.** Solo entidades con vínculo servidor / dentro del watermark. |
| PendingSync Update/Corrective | Deben pasar a Conflict (como tombstone), no Cancel silencioso. |
| Conflict ya existente | Mantener Conflict; opcionalmente refrescar razón a ENTITY_DELETED / ClearAll. No auto-KeepServer sin UX. |
| Otro ámbito | Scope debe ser explícito; hoy solo hay ámbito global de tabla. |
| Creates posteriores al Clear | Watermark `clearedThroughSequence` / `clearedAtUtc` evita borrar registros creados **después** del clear. |
| Side-channel | Evento global visible a todos los autenticados revela que hubo vaciado (aceptable en intranet mono-tenant). |
| Protocolo | SyncEngine hoy ignora ChangeTypes desconocidos al aplicar pero **avanza cursor** → cliente viejo que no entiende `RecordsCleared` **pierde el clear para siempre** (fantasmas). Por eso hace falta versión mínima de cliente o fan-out tombstones de compatibilidad. |

---

## 5. Clientes offline / Outbox — matriz semántica

Premisa: ClearAll sync-safe **solo online/admin**. El dispositivo no encola ClearAll.

| Estado local | Tras ClearAll sync-safe (A o B bien diseñado) | Notas |
|--------------|-----------------------------------------------|-------|
| `LocalNepRecord` Synced (tiene ServerRecordId) | Retirado (`IsDeleted`) vía tombstone o RecordsCleared | Réplica alineada |
| Create Pending (sin ServerRecordId) | **Sobrevive** localmente; Push Create posterior | Servidor no conocía la entidad; ClearAll no la incluye. **No** rechazar Create por el clear global. |
| Create Accepted luego Clear | EntityId entra en clear → cliente que aún no pulló verá delete/clear | Idempotencia Create intacta hasta clear |
| Update Pending | Push → `Conflict` / `ENTITY_DELETED` / Invalid deleted; o Pull adelanta Conflict | No LWW |
| Delete Pending | Idealmente **idempotente** Accepted/Duplicate si EntityId ya tombstoned; o Conflict DeleteDelete → Keep Server | Alineado a Delete actual |
| ApplyCorrective Pending | `Conflict` + ENTITY_DELETED (como UpdateDelete 2D.10); Keep Local **prohibido** | Sin Restore |
| Conflict ya existente | Conservar Conflict; actualizar snapshot/razón si llega tombstone/clear | No cancelar en silencio |
| Pending de otro usuario | No aplica (Outbox filtrado por sesión) | — |

### Create offline todavía no enviado

* **¿Sobrevive al ClearAll?** Sí, en SQLite.
* **¿Rechazarse al llegar al servidor?** No por causa del ClearAll (el clear no conoce ese ClientOperationId). Se crea normalmente **después** del clear.
* **¿Conflicto?** No automáticamente.

### Update / ApplyCorrective offline

* Conflicto / ENTITY_DELETED; no Accepted silencioso.

### Delete offline

* Preferir idempotencia (ya eliminado en servidor).

### Conflict existente

* No auto-resolver; UX 2D.7 (Keep Server típico tras clear).

---

## 6. ClearAll y permisos

| Pregunta | Respuesta basada en repo |
|----------|---------------------------|
| ¿Quién puede? | Roles con `ClearAllRecords` (Admin/SuperAdmin sembrados). |
| ¿SeesAll obligatorio? | De facto sí: ClearAll borra **toda** la tabla; un actor sin SeesAll con este permiso (si se configurara mal) eliminaría registros ajenos. **El servidor debe exigir ClearAllRecords y tratar el alcance como global de tabla.** |
| ¿Ownership? | No aplica al ClearAll actual (no es por propietario). |
| ¿Riesgo acceso parcial? | Alto si se otorgara el permiso a roles sin SeesAll: borraría fuera de su vista UI. Mitigación: mantener permiso solo admin + auditoría. |
| ¿Offline ClearAll? | **No diseñar** en 2D.12. Online/admin-only hasta fase que demuestre necesidad. |

Autoridad: siempre servidor. Permisos locales UX no autorizan.

---

## 7. Idempotencia

| Estrategia | Identidad | Retry |
|------------|-----------|-------|
| N tombstones | Cada `RecordDeleted` puede llevar `ClientOperationId` propio **o** ser server-side sin client op (como Delete online actual con `clientOperationId: null`) | Reintentar ClearAll a medias: IDs ya borrados → tombstone lookup / no-op por entidad (Delete ya contempla entidad ausente + tombstone). |
| RecordsCleared | Un `ClientOperationId` (o hash server-side del clear) en el ChangeLog global | Mismo ClientOperationId → Duplicate del evento; no re-borrar. |

No se introduce un segundo mecanismo de idempotencia más allá de `(ActorUserId, ClientOperationId)` / lookup de tombstone ya usados en Delete.

Para ClearAll admin online, la idempotencia práctica es: **operación de mantenimiento con progreso** (batches) más que un único ClientOperationId de dispositivo.

---

## 8. Cursor y Pull

### 8.1 Escenario `X → ClearAll → Y`

```text
Cursor cliente = X
ClearAll sync-safe produce eventos con Sequence ∈ (X, Y]
Cliente Pull(X) → recibe página(s) hasta Y
Aplica tombstones / RecordsCleared
NextCursor = Y, HasMore según cola
```

### 8.2 Escenario `X → ClearAll → Create → Pull`

```text
ClearAll → eventos delete/clear (Sequence C)
Create nuevo R_new → RecordUpserted (Sequence C+1)
Cliente atrasado Pull(X):
  1) aplica clear/tombstones (réplica vieja muere)
  2) aplica Upsert de R_new (aparece)
```

**Regla anti-borrado accidental de creates posteriores:**

* Con N tombstones: solo EntityIds existentes al clear; R_new no tiene tombstone.
* Con RecordsCleared: **obligatorio** `clearedThroughSequence = C` (o equivalente). El cliente solo retira locales cuyo último origen conocido sea ≤ C / sin ServerRecordId post-clear.

### 8.3 Múltiples ClearAll

```text
Clear1 → (opcional Creates) → Clear2
```

Cada clear deja su huella (N tombstones o evento). Cliente atrasado aplica en orden. Idempotente a nivel de estado local (`IsDeleted` estable).

### 8.4 Eventos no autorizados

Pull ya salta ChangeLogs no autorizados pero **avanza `nextCursor`** sobre lo escaneado. Tombstones ajenos no llegan al Operario. Un `RecordsCleared` global debe ser visible a todo autenticado (como catálogo) o el Operario no limpiaría su réplica.

### 8.5 Clientes viejos vs RecordsCleared

Si se introduce `RecordsCleared` sin tombstones de compatibilidad, clientes que ignoran el ChangeType avanzan cursor y **nunca limpian**. Mitigaciones: (1) N tombstones siempre; (2) bump de versión mínima de app; (3) emitir ambos durante transición.

---

## 9. Retención y compactación

| Tema | Implicación |
|------|-------------|
| Millones de tombstones | Crecimiento de `SyncChangeLogs`; Pull lento; backups grandes |
| Retención | Hoy no hay purge. Fase futura: retención por tiempo / min-cursor soportado |
| Clientes extremadamente atrasados | Si se purgan tombstones antes de que pullen → fantasmas otra vez |
| Snapshots/baselines | Resync completo (`LastPulledSequence=0` + snapshot) como escape |
| Cursor mínimo soportado | Servidor publica `MinSupportedCursor`; debajo → forzar full resync |
| Fase futura sugerida | Tras 2D.12 estable: «ChangeLog retention & compaction» (P2), posiblemente usando RecordsCleared como **evento de compactación**, no como único mecanismo inicial |

---

## 10. Matriz de comparación

| Criterio | N tombstones | RecordsCleared |
|----------|--------------|----------------|
| Semántica | Delete lógico por entidad (igual Delete/DeleteMany) | Vaciado de ámbito explícito |
| Atomicidad | Difícil en una sola TX a gran N; batches naturales | Un evento; clear físico puede ser batch aparte |
| Volumen | O(N) | O(1) |
| Pull | Páginas muchas; HasMore largo | Una (o pocas) filas |
| Clientes offline | Cubiertos por ApplyDelete existente | Requiere handler nuevo + watermark |
| Outbox | Conflict ENTITY_DELETED ya implementado | Debe reutilizar mismas reglas |
| Seguridad | Ownership por tombstone (filtro actual) | Evento global: regla Pull especial |
| Retención | Pesada | Ligera; habilita compactar tombstones luego |
| Escalabilidad | Mala a millones | Buena |
| Complejidad | Baja (reusa stack) | Alta (protocolo + compat clientes) |
| Compatibilidad actual | Alta (ChangeType existente) | Baja sin versión/transición |
| Migración | ClearAll → loop/batch DeleteWithTombstone | Nuevo ChangeType + SyncEngine + docs protocolo |

---

## 11. Casos especiales

| Caso | ¿Coexiste con ClearAll sync-safe? |
|------|-----------------------------------|
| DeleteMany | Sí; es el mismo mecanismo a menor escala |
| Importaciones Excel/CSV | Generan ChangeLog; ClearAll posterior las tombstonea |
| Migración Firestore histórica | Filas **sin** ChangeLog de create: ClearAll N-tombstones las cubre al borrar; si nunca tuvieron upsert en log, clientes que no las tenían no las necesitan; clientes que las recibieron offline solo por… (hoy no llegan por Pull) — OK |
| Registros pre-2B.1 sin ChangeLog | Igual: el clear emite tombstones al borrar; no requiere backfill previo |
| Sin propietario | Tombstone exige `OwnerUserId`; hay que definir fallback (actor / `"unknown"`) en implementación — **decisión 2D.12** |
| SeesAll | Admin recibe todos los tombstones / el evento global |
| Pull incompleto | HasMore continúa hasta drenar |
| Réplica local fuera del subconjunto visible | Operario no recibe tombstones ajenos; no debería tener esos ServerRecordId |

---

## 12. Impacto UX (solo diseño)

| Situación | Mensaje / comportamiento conceptual |
|-----------|-------------------------------------|
| Oleada de tombstones | Sync UX: «Servidor eliminó N registros»; contadores Conflict↑ |
| `RecordsCleared` | Banner: «El administrador vació el historial de mediciones» |
| Operaciones pendientes | Lista Operaciones: Conflict ENTITY_DELETED; CTA resolver (Keep Server) |
| Conflictos previos | Permanecen; hint «registro eliminado en vaciado global» |
| Réplica vacía | Lista offline vacía; captura Create sigue permitida |
| Alcance parcial (futuro) | Si algún día Clear no es global, texto debe decir el ámbito; **hoy es global** |

Sin UI en esta fase.

---

## 13. SQL Server

| Tema | Diseño |
|------|--------|
| Transacciones | Preferir batches con TX por lote; una mega-TX puede timeout |
| Locks | Delete masivo + insert ChangeLog: planificar ventana de mantenimiento |
| Índices | `Sequence` IDENTITY; índices ownership/ClientOperation ya existen en NepRecords |
| Volumen / tiempo | Estimar N antes; UI admin con progreso (futuro) |
| Timeout | Configurar CommandTimeout elevado solo en job de clear |
| Auditoría | Actor + timestamps en cada tombstone o en evento RecordsCleared |
| Validación física | **Pendiente** si el entorno no tiene SQL Server accesible |

No migraciones en 2D.11.

---

## 14. Matriz de tests futuros (2D.12+)

1. ClearAll sin clientes offline → tabla 0 + N tombstones (o 1 RecordsCleared).
2. ClearAll con cliente atrasado → Pull limpia fantasmas.
3. ClearAll con cliente offline → al reconectar, sin resurrección.
4. Create Pending local sobrevive; Push Create Accepted post-clear.
5. Update Pending → Conflict / ENTITY_DELETED.
6. Delete Pending → idempotente o DeleteDelete resoluble.
7. ApplyCorrective Pending → Conflict; Keep Local prohibido.
8. Conflict pendiente → no auto-cancel; UX Keep Server.
9. ClearAll + Create posterior → Create visible tras Pull; no borrado por watermark.
10. Múltiples ClearAll consecutivos → estado final vacío estable.
11. Cursor anterior al ClearAll → recibe eventos de clear.
12. Cursor posterior al ClearAll → no re-aplica destructivamente.
13. Usuario sin `ClearAllRecords` → Forbidden / Unauthorized.
14. Usuario SeesAll admin → clear global OK.
15. Alcance: verifica que se borran **todos** los NepRecord (no solo los del actor).
16. Históricos sin ChangeLog previo → igual reciben tombstone al clear.
17. Guard/catálogos: ClearAll sync-safe no depende de «cero ChangeLogs»; catálogos no se borran.
18. SavedReport snapshots intactos.
19. SQL Server (cuando haya entorno): batch grande sin corrupción de Sequence.
20. Regresión: Create/Update/Delete/ApplyCorrective/Conflict/Pull/catálogos.

---

## 15. Recomendación arquitectónica

### Evidencia que pesa

1. Delete / DeleteMany / SyncEngine **ya** implementan el camino tombstone + Outbox Conflict.
2. ClearAll actual es **global de tabla**, raro y admin-only.
3. Introducir `RecordsCleared` sin tombstones de compatibilidad **rompe clientes** que ignoran ChangeTypes desconocidos (avanzan cursor).
4. Tras 2D.9, el guard `CountChangeLogs > 0` deja ClearAll inoperante en BD reales (catálogos).
5. El volumen O(N) es el único argumento fuerte contra N tombstones.

### Recomendación

**Para la futura 2D.12: implementar ClearAll sync-safe como N tombstones (`RecordDeleted`) en batches online/admin-only**, reutilizando `DeleteWithTombstoneAsync` (o variante batch interna con el mismo ChangeLog).

**No** introducir aún `RecordsCleared` como único mecanismo.

**Reservar `RecordsCleared` + retención/compactación** para una fase posterior cuando:

* N supere umbrales operativos medidos en SQL Server; y/o
* exista política de `MinSupportedCursor` / full resync.

Mantener ClearAll **bloqueado o sustituido** de forma que **nunca** vuelva el borrado físico silencioso con clientes offline vivos.

Justificación breve: máxima compatibilidad con el protocolo v1 real del repo, menor riesgo de wipe de Outbox, y DeleteMany ya prueba la semántica.

---

## 16. Dependencias para implementación (2D.12)

Componentes a tocar **en la fase de implementación** (no ahora):

1. `NepRecordService.ClearAllAsync` — reemplazar guard+ExecuteDelete por orquestación sync-safe.
2. `IAtomicNepRecordCreateStore` / `AtomicNepRecordCreateStore` — posiblemente API batch o loop interno atómico por lote.
3. `ISyncPersistence` — conteos, progreso; redefinir guard (no bloquear por logs de catálogo).
4. `NepRecordRepository.ClearAllAsync` — dejar de usarse como camino sync, o restringirlo a bootstrap sin réplicas.
5. UI `Registros.razor` — textos de confirmación / progreso / error.
6. Tests nuevos (matriz §14) + regresión suite.
7. Docs protocolo / runbook admin.
8. **No** Outbox ClearAll; **no** SignalR; **no** cambio de autenticación.

Opcional más adelante: `SyncConstants.ChangeRecordsCleared`, handler SyncEngine, ProtocolVersion.

---

## 17. Riesgos

| Riesgo | Severidad | Mitigación futura |
|--------|-----------|-------------------|
| Timeout SQL Server en N grande | Alta | Batches + mantenimiento |
| Pull largo para clientes atrasados | Media | HasMore; UX paciencia; luego compactación |
| Guard actual vs catálogos | Media (operativa hoy) | Rediseñar criterio en 2D.12 |
| Permiso mal asignado sin SeesAll | Alta | Mantener ClearAllRecords solo admin |
| RecordsCleared sin compat | Alta | No como primer mecanismo |
| Wipe Create offline | Alta | Reglas Outbox §5 |
| Históricos sin owner | Media | Política OwnerUserId en 2D.12 |
| SQL Server físico no validado | Media | Entorno CI/intranet |

---

## 18. Criterios de aceptación de una futura 2D.12

PASS solo si:

1. ClearAll online con permiso genera evidencia sync (N `RecordDeleted` o diseño aprobado).
2. Ningún cliente atrasado conserva fantasmas tras Pull completo.
3. Create offline Pending sobrevive y puede Accepted post-clear.
4. Update/ApplyCorrective Pending → Conflict; sin LWW.
5. Delete Pending idempotente o resoluble vía 2D.7.
6. Catálogos no se borran.
7. SavedReports no se borran.
8. Sin ClearAll offline/Outbox.
9. Suite de regresión 2A–2D.10 en verde.
10. Web Release / Android Debug sin regresiones de contrato.
11. Documentación de operación admin (ventana, batches, permisos).

PASS CON WARNINGS aceptable si solo falta SQL Server físico o Android E2E de entorno.

---

## 19. Decisiones descartadas en 2D.11

* Implementar ClearAll sync-safe ahora.
* ClearAll offline / Outbox.
* Reactivar borrado físico silencioso «porque está bloqueado».
* Elegir RecordsCleared como único camino sin plan de compatibilidad de clientes.
* Soft-delete permanente de NepRecord sin ChangeLog (duplicaría semántica tombstone de forma opaca).
* Last-Write-Wins tras clear.

---

## 20. Referencias de código (evidencia)

* `NepRecordService.ClearAllAsync` / `DeleteManyAsync` / `DeleteAsync`
* `NepRecordRepository.ClearAllAsync`
* `AtomicNepRecordCreateStore.DeleteWithTombstoneAsync`
* `SyncPersistence.PullAuthorizedChangesAsync` / `CountChangeLogsAsync` / `IsAuthorized`
* `SyncNepRecordPayloadMapper.CreateRecordDeletedEntry`
* `SyncEngine.ApplyDeleteAsync`
* `Registros.razor` (UI ClearAll)
* `AppPermission.ClearAllRecords` / `RolePermissions`
* `SavedReport.SnapshotJson`
* Tests: `SyncApplyCorrectiveChangeLogTests.ClearAll_*`
* Contexto previo: `docs/FASE2D8_OFFLINE_COVERAGE_ROADMAP.md` §6
