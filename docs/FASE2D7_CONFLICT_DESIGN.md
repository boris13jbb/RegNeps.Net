# FASE 2D.7 — Diseño de resolución explícita de conflictos offline

> **Estado de esta fase:** DISEÑO APROBADO + IMPLEMENTACIÓN MVP (FASE 2D.7).  
> Ver también `docs/FASE2D7_CONFLICT_IMPLEMENTATION.md`.

Base: rama `feature/fase-2d5-offline-update`, gate 2D.6.1 `39b86d0`.

---

## 1. Alcance

Diseñar cómo un usuario autorizado podrá **resolver de forma explícita** un `PendingOperation` / `LocalNepRecord` en estado `Conflict`, sin pérdida silenciosa de datos y sin alterar el protocolo Sync v1 existente.

**Incluye**

- Clasificación de conflictos según el protocolo actual.
- Modelo de datos necesario (reutilizando lo ya persistido).
- Estrategias posibles y recomendación.
- Update vs Delete, tombstones, idempotencia, autorización, UX conceptual, auditoría, matriz de pruebas e invariantes.

**Excluye**

- Implementación, merge automático, Restore, ClearAll, background sync, SignalR recovery, cambios a `/api/sync/*`.

---

## 2. Estado actual (evidencia en código)

### Flujo

```text
UI → OfflineStore/Outbox → SyncEngine → /api/sync/push
  → Conflict | ENTITY_DELETED → PendingOperation.Conflict
  → LocalNepRecord.SyncStatus = Conflict
  → Edit/Delete bloqueados (ConflictRequiresReview / MutationAlreadyPending)
  → Pull refresca stamp/snapshot servidor SIN LWW de campos de negocio
```

### Persistencia en Conflict (ya existe)

| Dato | Dónde | Notas |
|------|--------|--------|
| Intención local (campos negocio) | `LocalNepRecord` | Update: valores editados; Delete: `IsDeleted=true` |
| Payload original | `PendingOperation.PayloadJson` | UpdateRecord / DeleteRecord |
| `ClientOperationId` conflictivo | `PendingOperation.ClientOperationId` | **No reutilizar** en resolución |
| `ExpectedConcurrencyStamp` (stale) | `PendingOperation.ExpectedConcurrencyStamp` | Stamp con el que falló el Push |
| Stamp servidor | `ConflictServerConcurrencyStamp` | También puede actualizarse vía Pull |
| Snapshot servidor | `ConflictServerSnapshotJson` | Shape `ClientNepRecordSnapshot` o tombstone |
| Código / mensaje | `LastServerErrorCode`, `LastError` | p. ej. `CONFLICT`, `ENTITY_DELETED` |
| EntityId | `TargetServerRecordId` / `LocalNepRecord.ServerRecordId` | Identidad servidor |
| Usuario / dispositivo | `UserId`, `DeviceId` | Informativos; bloqueos por EntityId |
| Bloqueo mutación | `ListBlockingOpsAsync` | Pending/Sending/**Conflict** por EntityId (no solo UserId) |

### Semántica servidor (protocolo actual)

| Situación | Resultado típico | Tombstone / mutación |
|-----------|------------------|----------------------|
| Update stamp stale | `Conflict` + snapshot | Sin mutación, sin ChangeLog nuevo |
| Delete stamp stale | `Conflict` | Registro intacto, sin `RecordDeleted` |
| Delete ya eliminado (otro OpId) | `Invalid` / `ENTITY_DELETED` | SyncEngine → `Conflict` local + `IsDeleted` |
| Mismo ClientOperationId retry | `Duplicate` | Sin segundo Sequence |
| Update tras Delete | `ENTITY_DELETED` | No recrea |

UX hoy: «Requiere revisión» (botón deshabilitado). `TryPrepareRetry` **no** reabre Conflict.

---

## 3. Tipos de conflicto

### A. Update vs Update

Ambos parten del mismo stamp `X`. A acepta Update → stamp `Y`. B Push Update con `Expected=X` → **`Conflict`** + snapshot de A. Local B conserva su edición; snapshot servidor en Outbox.

### B. Update vs Delete

B elimina (Accepted → tombstone). A Push Update con stamp viejo → **`ENTITY_DELETED`** (Invalid) o Conflict según camino; SyncEngine marca operación `Conflict`, local `IsDeleted=true`. Snapshot puede ser vacío o tombstone vía Pull `RecordDeleted`.

### C. Delete vs Update

A tiene Delete Pending (`IsDeleted` local). B Update Accepted en servidor. A Push Delete con stamp `X` stale → **`Conflict`** (registro sigue vivo con stamp nuevo). Local A sigue con intención Delete + snapshot servidor (activo).

### D. Delete vs Delete

- Mismo `ClientOperationId` → **`Duplicate`** (idempotencia; un solo tombstone).
- Otro `ClientOperationId` tras eliminación → **`ENTITY_DELETED`** (Invalid), SyncEngine → Conflict local; **no** segundo tombstone.

### E. Conflict local persistente

Tras Conflict:

- `LocalNepRecord`: campos de negocio locales intactos; `SyncStatus=Conflict`; `ConcurrencyStamp` puede refrescarse con Pull (stamp servidor) **sin** sobrescribir Telar/Neps/etc.; `IsDeleted` según intención/ENTITY_DELETED.
- `PendingOperation`: permanece `Conflict`; `PayloadJson` = intención; snapshot/stamp servidor; **no** reenvío automático.

---

## 4. Modelo de datos propuesto (sin migración obligatoria en v1)

### Reutilizar (suficiente para MVP de resolución)

No duplicar una segunda tabla si se puede:

1. **Local (intención):** `LocalNepRecord` + `PayloadJson`.
2. **Servidor (verdad en conflicto):** `ConflictServerSnapshotJson` + `ConflictServerConcurrencyStamp`.
3. **Operación conflictiva:** `PendingOperation` (tipo, ClientOperationId, ExpectedStamp, UserId, DeviceId, errores).

### Campos servidor en snapshot (`ClientNepRecordSnapshot`)

Necesarios para UI/diff: `Id`, `Telar`, `Neps`, `Tela`, `LoteTrama`, `Turno`, `Operario`, `LineaProduccion`, `Observacion`, `ConcurrencyStamp`, `UpdatedAtUtc`, `OwnerUserId`, `CreatedAtUtc` (contexto).

`QualityLabel` / `MtsCalculados` en snapshot: **solo presentación**; autoridad = `Neps` + `NepsQualityCriteria`.

Tombstone (`ClientNepRecordDeletedSnapshot`): `Id`, `OwnerUserId`, `DeletedAtUtc`, `LastConcurrencyStamp`.

### Opcional en fase de implementación (si falta UX)

| Campo | ¿Necesario? |
|-------|-------------|
| `ConflictKind` enum (UpdateUpdate / UpdateDelete / …) | Útil; derivable de `OperationType` + `LastServerErrorCode` + presencia de tombstone |
| `ResolutionClientOperationId` | Sí, en la **nueva** PendingOperation de resolución |
| CorrelationId local | Nice-to-have; servidor ya loguea en Push |
| Copia inmutable “local baseline al conflictar” | Redundante si no se edita local tras Conflict (hoy bloqueado) |

**No** inventar `RestoreRecord` en protocolo v1.

---

## 5. Estrategias posibles

| # | Nombre | Efecto | Riesgo |
|---|--------|--------|--------|
| 1 | **Keep Server** | Descartar intención local; alinear réplica al snapshot/tombstone servidor; marcar Outbox conflictivo como resuelto (Synced/Cancelled) **sin** Push destructivo | Bajo si es explícito |
| 2 | **Keep Local (reaplicar)** | Nueva operación (nuevo ClientOperationId) con `ExpectedConcurrencyStamp = ConflictServerConcurrencyStamp` (o stamp Pull más reciente) y payload = intención local | Medio: puede Conflict otra vez |
| 3 | **Edit & Retry** | UI edita sobre base servidor; nueva Update con stamp servidor actual | Medio-bajo; más control |
| 4 | **Cancel** | Igual que Keep Server en efecto de datos; semántica de “abandonar mi operación” | Solapar con 1 |

**Cancel vs Keep Server:** en datos son equivalentes (servidor manda). Separar en UX: “Aceptar datos del servidor” vs “Descartar mi cambio” solo si el copy ayuda; **una sola implementación** de alineación a servidor.

---

## 6. Estrategia recomendada (justificación)

### Recomendación: **menú tipado + Keep Server / Edit&Retry / Keep Local tipado**

1. **Clasificar** el conflicto (derivado, no LWW):
   - `UpdateUpdate` — servidor activo + Update local.
   - `UpdateDelete` — servidor eliminado (`ENTITY_DELETED` / tombstone) + Update local.
   - `DeleteUpdate` — servidor activo + Delete local.
   - `DeleteDelete` — ya eliminado; idempotencia / aceptar tombstone.

2. **Acciones permitidas por tipo** (ver §7 y §13).

3. **Keep Local** = siempre **nueva** `PendingOperation` con:
   - nuevo `ClientOperationId`;
   - `ExpectedConcurrencyStamp` = stamp servidor actual (no el stale);
   - payload de intención (Update o Delete);
   - autorización revalidada en servidor.

4. **Keep Server** = transacción local:
   - aplicar snapshot o tombstone a `LocalNepRecord`;
   - cerrar operación Conflict (p. ej. Synced/Cancelled + flag de resolución);
   - **sin** Push que sobrescriba servidor.

5. **Edit & Retry** = obligatorio cuando el usuario quiere cambiar valores respecto a servidor o a su intento; base = snapshot servidor; calidad desde Neps.

**Por qué no “servidor siempre” / “cliente siempre”:** violan invariantes 1–3 y el dominio textil (pérdida de medición o eliminación incorrecta).

**Por qué no merge automático campo-a-campo:** Telar/Lote/Turno/Neps son una medición coherente; mezclar “Telar de A + Neps de B” inventa un registro falso y rompe auditoría/calidad.

---

## 7. Update vs Delete (diseño explícito)

### Caso A — Update local / Delete remoto

| UI muestra | Acciones válidas |
|------------|------------------|
| Local: valores del Update | **Keep Server** → tombstone local, Outbox cerrado |
| Servidor: eliminado | **Keep Local** → **prohibido como Update** sobre EntityId eliminado |
| | Opción futura explícita: **nuevo Create** con nuevos IDs (no Restore) — solo si producto lo aprueba; default **no ofrecido** en 2D.7 MVP |

### Caso B — Delete local / Update remoto

| UI muestra | Acciones válidas |
|------------|------------------|
| Local: intención eliminar | **Keep Server** → aceptar registro vivo del snapshot |
| Servidor: campos actuales | **Keep Local** → nueva `DeleteRecord` con stamp servidor actual |
| | **Edit & Retry** no aplica a Delete (salvo cancelar delete y editar = Keep Server + luego Update) |

### Delete/Delete

Keep Server / “confirmar eliminado” → alinear tombstone; Outbox cerrado. No segunda Delete efectiva.

---

## 8. Tombstones

**Prohibido** que una resolución:

- borre o ignore un `RecordDeleted` en servidor;
- haga Update Accepted sobre EntityId eliminado;
- genere doble tombstone;
- “resucite” silenciosamente el mismo Id.

**Keep Local sobre eliminado:** no existe `Restore` en protocolo v1 → **prohibido** como Update/Delete mágico. Si negocio exige recuperar datos, fase aparte: **Create** nuevo EntityId (copia de campos), nunca reutilizar Id tombstoned.

Pull tras resolución Keep Server (delete): cursor entrega tombstone; SyncEngine ya evita resurrección.

---

## 9. Idempotencia de la resolución

| Regla | Diseño |
|-------|--------|
| ClientOperationId de resolución | **Nuevo** Guid; ≠ del Conflict |
| Doble click Keep Server | Idempotente local (op ya cerrada) |
| Doble click Keep Local | Segunda vez bloqueada (MutationAlreadyPending / op Synced) o Duplicate en servidor si mismo OpId |
| Red corta tras Push Accepted | Retry mismo OpId → Duplicate |
| Misma resolución dos veces | No crear segunda Pending efectiva |
| Dos dispositivos resuelven | Segundo puede Conflict de nuevo (stamp cambió) — correcto |

Cerrar Conflict: marcar operación conflictiva como resuelta **antes o atómicamente con** encolar resolución, para no tener dos Outbox activos del mismo EntityId (alineado con 2D.5/2D.6.1).

---

## 10. Autorización

| Capa | Rol |
|------|-----|
| UX local | Ocultar acciones según snapshot `EditRecords` / `DeleteRecords` / ownership / SeesAll RoleCode |
| Servidor | Autoridad: Keep Local Delete → `DeleteRecords`; Keep Local Update / Edit&Retry → `EditRecords`; ownership/SeesAll reales |

**Ejemplo:** permiso revocado tras Conflict → Keep Local Push → `Forbidden` → `SyncError`; **conservar** Conflict metadata / nueva op en SyncError para reintento o Keep Server.

Keep Server es solo local + no eleva privilegios en servidor.

---

## 11. Usuario / sesión / pertenencia del Conflict

El Conflict pertenece a la **combinación**:

- **EntityId** (protección mutua, no solo UserId);
- **PendingOperation** (intención + ClientOperationId);
- **UserId** de la operación (quién puede Push esa Outbox);
- **DeviceId** (Outbox del dispositivo).

Resolución UX: usuario de `LocalSession` actual con permiso; SyncEngine solo Push de ops de `session.UserId`. Otro usuario SeesAll **no** debe poder Push la Outbox ajena; sí podría Keep Server local si producto lo permite (decisión pendiente) o solo el dueño de la op.

---

## 12. Campos NepRecord / LocalNepRecord en resolución

| Campo | Rol | ¿Editable en Edit&Retry? |
|-------|-----|---------------------------|
| Telar, Neps, Tela, LoteTrama, Turno, Operario, LineaProduccion, Observacion | Captura | Sí (negocio) |
| MtsCalculados, QualityLabel / nivel | Derivados de Neps | **No** — recalcular `NepsQualityCriteria` |
| Id / ServerRecordId / EntityId | Identidad | No |
| CreatedAtUtc / CreatedBy* | Auditoría creación | No |
| OwnerUserId / UserId local | Ownership | No via payload |
| ClientOperationId (Create original) | Idempotencia Create | No; resolución usa **otro** |
| CaptureSessionId | Contexto | No cambiar en resolución salvo política UX |
| ConcurrencyStamp | Concurrencia | No editar; usar stamp servidor en nueva op |
| UpdatedAtUtc | Auditoría | Servidor |
| IsDeleted | Tombstone local | Solo vía Keep Server tombstone o Delete Accepted |
| AccionCorrectiva / revisión supervisor | Correctiva | Fuera de MVP resolución captura |

---

## 13. UX propuesta (conceptual)

### Pantalla «Resolver conflicto»

1. **Registro:** Telar, EntityId corto, sesión/turno si hay.  
2. **Estado local:** valores + tipo (Actualizar / Eliminar).  
3. **Estado servidor:** snapshot o “Eliminado el …”.  
4. **Diferencias:** campo a campo (solo negocio).  
5. **Motivo:** mapeo de `LastServerErrorCode` (modificado / eliminado / stamp).  
6. **Acciones** (solo las válidas para el `ConflictKind`):

| Kind | Mantener servidor | Reintentar mis cambios (Keep Local) | Editar y resolver | Cancelar (= Keep Server) |
|------|-------------------|--------------------------------------|-------------------|--------------------------|
| UpdateUpdate | Sí | Sí (nueva Update) | Sí | Sí (alias) |
| UpdateDelete | Sí | No (o Create nuevo — pendiente producto) | No sobre Id muerto | Sí |
| DeleteUpdate | Sí | Sí (nueva Delete) | No | Sí |
| DeleteDelete | Sí | No | No | Sí |

Sin merge automático. Sin “forzar”.

---

## 14. Por qué no merge automático

En NEPS, Telar + Lote + Turno + Neps + Observacion forman una medición; un merge parcial puede:

- falsear calidad (`NepsQualityCriteria`);
- atribuir datos a telar/lote incorrectos;
- romper trazabilidad frente a `SyncChangeLog` / ownership.

Cualquier merge futuro = fase explícita con reglas de dominio, no default.

---

## 15. Matriz de pruebas futuras (implementación 2D.7)

| Caso | Estado servidor | Estado local | Acción | Resultado esperado |
|------|-----------------|--------------|--------|--------------------|
| Update/Update | modificado stamp Y | Update | Keep Server | Local = snapshot Y; op Conflict cerrada; sin Push overwrite |
| Update/Update | modificado Y | Update | Keep Local | Nueva Update, Expected=Y, nuevo ClientOpId; Accepted o nuevo Conflict |
| Update/Update | modificado Y | Update | Edit & Retry | Nueva Update con valores editados + stamp Y |
| Update/Delete | eliminado | Update | Keep Server | Local tombstone; op cerrada |
| Update/Delete | eliminado | Update | Keep Local Update | **Rechazado** (política MVP) |
| Delete/Update | modificado Y | Delete | Keep Server | Local = snapshot activo |
| Delete/Update | modificado Y | Delete | Keep Local | Nueva Delete Expected=Y |
| Delete/Delete | eliminado | Delete Conflict | Keep Server | Tombstone local; sin 2º RecordDeleted |
| Delete retry mismo OpId | eliminado | — | retry | Duplicate |
| Permiso perdido | activo | Update Conflict | Keep Local | Forbidden → SyncError; metadata conservada |
| Resolución duplicada | — | — | mismo ClientOpId resolución | Duplicate / no-op local |
| Resolución concurrente | stamp Z | Keep Local con Y | Push | Conflict de nuevo |
| Doble click Keep Local | — | — | 2× | Una sola Pending efectiva |
| Pull tras Keep Server | — | — | Pull | Sin LWW ni resurrección |
| Quality | Neps cambiado en Edit&Retry | — | — | Label vía `NepsQualityCriteria` |

Ajustes si implementación elige Create-as-recovery para UpdateDelete (requiere aprobación).

---

## 16. Invariantes (obligatorios antes de implementar)

1. Nunca sobrescribir cambio remoto sin operación explícita.  
2. Nunca perder modificación local sin decisión explícita.  
3. Nunca resolver con LWW ni “el más reciente gana”.  
4. Nunca ignorar `ConcurrencyStamp` / `ExpectedConcurrencyStamp`.  
5. Toda reaplicación genera **nueva** operación + nuevo `ClientOperationId`.  
6. Toda resolución es idempotente ante retry/doble click.  
7. Toda reaplicación reautoriza en servidor.  
8. Un tombstone no desaparece silenciosamente.  
9. Un EntityId eliminado no revive accidentalmente.  
10. `QualityLabel` se deriva de `Neps` (`NepsQualityCriteria` / `TestLengthM=0.09`).  
11. Un Conflict no genera múltiples operaciones efectivas por UI.  
12. Resolución Forbidden/Conflict posterior conserva metadata para reintento o Keep Server.

---

## 17. Auditoría en servidor

Hoy: `SyncChangeLog` registra Upserted/Deleted de mutaciones Accepted; Push loguea UserId/DeviceId/ClientOperationId/Result/CorrelationId.

**Suficiente para MVP** si Keep Local es Update/Delete normales (quedan ChangeLogs).

**Gap:** Keep Server es solo local — no hay ChangeLog “ConflictResolved”. Opciones futuras (no implementar ahora):

- payload extendido en siguiente mutación (`resolvedFromClientOperationId`);
- o change type `ConflictResolved` (requiere protocolo — **fuera de v1 salvo decisión explícita**).

Reconstruir historia mínima MVP: logs Push del Conflict + ChangeLog de la resolución Keep Local + estado local Outbox.

---

## 18. Riesgos

1. Usuarios interpretan Keep Local como LWW.  
2. UpdateDelete sin Create-recovery → pérdida de medición local si solo Keep Server.  
3. SQL Server E2E aún no verificable en entorno actual.  
4. Ampliar protocolo prematuramente.  
5. Permitir resolución de Outbox ajena con SeesAll sin reglas claras.  
6. Refrescar `ConcurrencyStamp` en Pull mientras Conflict sin actualizar UI de diff.

---

## 19. Decisiones aprobadas (implementadas)

1. **UpdateDelete:** solo Keep Server (aceptar eliminación). Sin Keep Local Update / Restore / Create automático.  
2. **Cancel ≡ Keep Server** (una semántica; textos UX contextuales).  
3. **Persistencia:** `ConflictServerSnapshotJson` + campos existentes; `OfflineConflictKind` derivado (sin columna nueva).  
4. **Auditoría:** `SyncChangeLog` de la nueva mutación Accepted; Keep Server solo local (`Cancelled` + marcador `Resolved:KeepServer`). Sin tabla de resoluciones.  
5. **Autorización:** autor de la op **o** SeesAll; Keep Local/Edit requieren permiso UX; servidor revalida al Push.  
6. **Delete/Delete:** semántica existente (`ENTITY_DELETED` / Duplicate); UI solo Keep Server para alinear tombstone.

---

## 20. Implementación MVP (hecho)

Ver `docs/FASE2D7_CONFLICT_IMPLEMENTATION.md`.

**Queda fuera (futuro):** Create-recovery tras UpdateDelete, change type `ConflictResolved`, ApplyCorrective en resolución, merge campo-a-campo.

**Prohibido (cumplido):** LWW, merge auto, Restore silencioso, reutilizar ClientOperationId del Conflict.

---

## Referencias de código

- `ConflictResolutionService` / `ConflictKindClassifier`  
- `PendingOperation` — Conflict*, PayloadJson, ExpectedConcurrencyStamp  
- `SyncEngine.ApplyPushResult` (sin LWW)  
- `OfflineConflictResolvePage` (MAUI)  
- `OfflineConflictResolutionTests`

---

*Diseño 2D.7 + implementación MVP. No avanzar a 2D.8 automáticamente.*
