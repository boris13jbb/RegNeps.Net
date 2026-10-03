# FASE 2D.8 — Inventario de cobertura offline y roadmap técnico

> **Estado de esta fase:** SOLO AUDITORÍA + DISEÑO.  
> **No implementar.** Sin cambios a protocolo Sync, endpoints, migraciones, UI funcional ni producción.  
> Base: rama `feature/fase-2d5-offline-update`, HEAD `1c26054` (cierre semántico 2D.7).

---

## 1. Alcance

Determinar con evidencia de código qué capacidades de RegNeps están cubiertas por offline/sync y cuáles quedan fuera, para priorizar fases futuras **sin** avanzar automáticamente a 2D.9.

**Incluye:** inventario NepRecord, ApplyCorrective, importaciones, ClearAll/DeleteMany, catálogos, SignalR, paginación, Pull, LocalSession, seguridad, NEPS, matriz, prioridades, roadmap, dependencias, SQL Server, Android E2E.

**Excluye:** implementación, backfill, Restore, Create-recovery, cifrado SQLite, cambios SignalR/Pull/Push.

---

## 2. Arquitectura actual (evidencia)

```text
[Blazor Server / Web]
  Captura / Registros / Alertas / Migracion
       │
       ▼
  NepRecordService ──► IAtomicNepRecordCreateStore / SyncPersistence
       │                    └── SyncChangeLog (RecordUpserted | RecordDeleted)
       ▼
  RegNepsDb (SQLite dev | SQL Server intranet)

[MAUI Android]
  OfflineCapture / Edit / Conflict UI
       │
       ▼
  OfflineStore (SQLite local)
    LocalNepRecord + PendingOperation (Outbox) + LocalSession (UX 72h)
       │
       ▼
  SyncEngine ──Push──► /api/sync ──► SyncAppService (Create|Update|Delete)
            └──Pull──◄── SyncChangeLog filtrado (solo NepRecord)

[SignalR]  /hubs/alerts → CriticalAlertReceived  (solo Blazor NotificationCenter)
           MAUI: sin HubConnection
```

**Autoridad:** cookie WebView = auth real para Sync. `LocalSession` / `PermissionsCsv` / `RoleCode` = UX local.

---

## 3. Inventario de operaciones sobre NepRecord

| Ruta | Online | Offline | ChangeLog | Tombstone | Concurrency | Sync |
|------|--------|---------|-----------|-----------|-------------|------|
| Create captura Blazor (`NepRecordService` + store atómico) | Sí | No | Sí (`RecordUpserted`) | No | Stamp al crear | Pull a otros |
| Create offline → Push CreateRecord | Origen offline | Sí Outbox | Tras Push Accepted | No | Stamp servidor al Accept | Push+Pull |
| Update Blazor (`UpdateAsync` + ExpectedStamp) | Sí | No | Sí | No | Stamp obligatorio | Pull |
| Update offline → Push UpdateRecord | Origen offline | Sí | Tras Push | No | ExpectedStamp | Push+Pull+Conflict |
| Delete Blazor unitario | Sí | No | Sí (`RecordDeleted`) | Sí | Stamp leído | Pull |
| Delete offline → Push DeleteRecord | Origen offline | Sí | Tras Push | Sí | ExpectedStamp | Push+Pull+Conflict |
| DeleteMany (loop DeleteAsync) | Sí | No | Por ID | Por ID | Por ID | Pull |
| ClearAll | Sí* | No | **No**; **bloqueado** si existen SyncChangeLogs | No (bulk) | N/A | Fuera protocolo fino |
| ApplyCorrective (Blazor) | Sí | **No** Outbox | Sí (`RecordUpserted`, ClientOperationId null) | No | Rota stamp; sin ExpectedStamp entrante | Pull (escalares); Push ApplyCorrective **Invalid** |
| Import Excel/CSV (`RecordImportService`) | Sí | No | Sí por fila (si actor + atómico) | No | Por fila | Pull |
| Migración Firestore JSON (`HistoricalDataMigrationService`) | Sí | No | **No** | No | No en MapRecord (parche BD puede rellenar) | No |
| Conflict Keep Server | No muta servidor | Solo local | No | Puede alinear tombstone local | Snapshot local | Sin Push |
| Conflict Keep Local / Edit&Retry | Tras Push | Sí nueva Outbox | Tras Accepted | Según op | Stamp servidor | Push |

\* ClearAll solo ejecutable cuando `CountChangeLogsAsync == 0` (p. ej. BD limpia / pre-sync).

**Fallbacks sin `IAtomicNepRecordCreateStore`:** Create/Update/Delete/ApplyCorrective/Import sin ChangeLog — típicos de tests; producción registra store atómico en DI.

---

## 4. ApplyCorrective

| Aspecto | Evidencia |
|---------|-----------|
| Campos | `CorrectiveActionEntry` + `AccionCorrectiva`, `ResponsableRevision`; opcional revisión supervisor / fecha; `UpdatedAt`; **nuevo** `ConcurrencyStamp` |
| Store | `AtomicNepRecordCreateStore.ApplyCorrectiveWithChangeLogAsync` (mismo store atómico que Update, método distinto) |
| ChangeLog | `RecordUpserted`; `ClientOperationId` / `DeviceId` null (online) |
| Payload Pull | Escalares correctivos; **no** historial completo `HistorialAcciones` |
| Offline Outbox | Enum `OfflineOperationType.ApplyCorrective` existe; **sin** método en `OfflineCaptureService` |
| Push v1 | `SyncAppService` solo Create/Update/Delete → `OPERATION_TYPE_UNSUPPORTED` |
| Conflict | Correctivo online no entra Outbox; cliente offline ve cambio vía Pull (stamp refresh sin LWW de negocio) |

### Recomendación

**Mantener online-only en el corto plazo** y, si hace falta offline, **operación específica `ApplyCorrective` en protocolo Sync v2** (no reutilizar Update genérico):

- Campos y permiso distintos (`ApplyCorrectiveAction` vs `EditRecords`).
- No debe mezclarse con Edit&Retry de captura (riesgo de sobrescribir medición).
- Payload de historial completo queda fuera del protocolo fino actual → diseño explícito si se necesita offline.

**No** bloquear Pull de correctivos ya aplicados: el cliente ya recibe escalares vía `RecordUpserted`.

---

## 5. Importaciones

| Origen | ChangeLog | Compatible sync | Clasificación |
|--------|-----------|-----------------|---------------|
| Excel/CSV `RecordImportService` | Sí (fila + actor atómico) | Compatible; reimport **duplica** (`imp-{row}-{Guid}`) | Online-only operativo; no Offline |
| Firestore JSON `HistoricalDataMigrationService` | **No** | **Incompatible** con clientes que dependen de cursor ChangeLog para ese histórico | Online-only / admin; requiere diseño específico (backfill o re-captura) antes de asumir sync |

**Recomendación:** no backfill en esta fase. Documentar migración Firestore como **fuente sin ChangeLog**. Cualquier fase de backfill = diseño aparte + SQL Server físico + política de idempotencia.

---

## 6. ClearAll / DeleteMany

### DeleteMany

- Loop de `DeleteAsync` con tombstone + ChangeLog por ID.
- Compatible con offline vía Pull de `RecordDeleted`.
- Volumen alto = N eventos en cursor (aceptable para lotes UI; riesgoso a escala masiva).

### ClearAll

| Opción | Evaluación |
|--------|------------|
| **A. N tombstones** | Correcto semánticamente; volumen/cursor/retención altos; coste Pull para clientes offline |
| **B. `RecordsCleared` especial** | Requiere protocolo + semántica de cursor/privacidad; riesgo side-channel si no se filtra bien |
| **C. Solo administrativa / online** | **Estado actual:** bloqueado si hay SyncChangeLogs; sin tombstones |
| **D. Otra** | Soft-flag global + purge diferido — complejidad alta |

**Recomendación (diseño):** mantener **opción C** mientras exista sync offline-first activo. Si el negocio exige vaciado con clientes offline vivos → diseñar **A o B** en fase dedicada (P1/P2), nunca reactivar ClearAll silencioso con ChangeLogs presentes.

---

## 7. Catálogos

| Catálogo | Fuente | Cache offline | Pull | Necesidad captura offline |
|----------|--------|---------------|------|---------------------------|
| Tela (`Fabric`) | SQL servidor `/telas` | No (tabla `LocalCatalogItem` vacía de uso) | No | Útil (UX); hoy texto libre MAUI |
| Lote (`LoteTramaItem`) | SQL `/lotes` | No | No | Útil; hoy texto libre |
| Telar | Texto en registro | N/A | En payload NepRecord | Obligatorio como texto |
| Turno | Online A/B/C fijo; offline libre | N/A | En payload | Mínimo: convención A/B/C en UI |
| Operario / Línea | Texto | N/A | En payload | Texto libre suficiente |
| `LocalCatalogItem` | Esquema SQLite + enum Fabric/Lote | **Sin servicio de lectura/escritura** | `EntityCatalogItem` reservado | Preparado, no operativo |

**Mínimo para captura offline usable hoy:** Telar + Neps + (Tela/Lote/Turno como texto) — **ya posible sin sync de catálogos**.

**Recomendación futura:** Pull **solo** Fabric + Lote activos (catálogo mínimo), no réplica completa. Riesgo: datos obsoletos → TTL + Pull al sync; autoridad siempre servidor.

---

## 8. SignalR

| Tema | Estado |
|------|--------|
| Hub | `/hubs/alerts` → `CriticalAlertReceived` |
| Consumidor | Blazor `NotificationCenter` + `WithAutomaticReconnect` |
| MAUI | **Sin** cliente SignalR |
| Replay | No; al evento → `RefreshAlertsAsync()` (HTTP/consulta) |
| SyncEngine | **No** fuerza Pull por alerta |
| Alerta perdida | Recuperable vía consulta `GetAlertsAsync` (no vía stream) |

**Recomendación (diseño):**  
1) Mantener SignalR como **notificación**, no como fuente de verdad.  
2) Tras reconexión / vuelta online MAUI: **Pull Sync + refresh alertas HTTP** (si MAUI expone alertas).  
3) No acoplar Alert hub a Outbox. Fase SignalR recovery = P2/P3 + E2E.

---

## 9. Paginación

| Uso | Clase | Notas |
|-----|-------|-------|
| Sync Push/Pull page ≤500, batch Push 50 | **A** intencional | Protocolo |
| Captura sesión `take: 500` | **C** riesgo | Tope UI |
| Registros / Dashboard / Export / Analytics hasta 50_000 + page in-memory | **C** | Necesitarán server-side al crecer |
| Alertas `take: 1000` | **C** | |
| Listas offline MAUI Take(20)/elegibles | **D** admin/UX | |
| Tops export 10–15 | **D** | |

**Pantallas candidatas a paginación server-side:** Registros, Dashboard, Exportar, Gráficas/Analytics, alertas densas. No implementar en 2D.8.

---

## 10. Sync Pull

| Entidad / ChangeType | ¿Llega por Pull? |
|----------------------|------------------|
| NepRecord `RecordUpserted` | Sí |
| NepRecord `RecordDeleted` | Sí |
| Catálogos | No |
| Alertas / SignalR | No |
| Otros | Ignorados si aparecen |

**Suficiente para:** captura sync, edición/eliminación offline, resolución de conflictos (snapshot/stamp), tombstones.  
**Insuficiente para:** combos Tela/Lote offline, historial correctivo completo, réplica total.

---

## 11. LocalSession

| Tema | Hecho |
|------|-------|
| Login online previo | Snapshot UX vía bridge; sin contraseña |
| TTL | 72h |
| Multi-usuario dispositivo | Fila `Id=1` — último login sobrescribe |
| Logout | Borra sesión + secure store; **conserva** Outbox/registros |
| SeesAll offline | HashSet `RoleCode` (no claim servidor) |
| Autoridad | **LocalSession no autentica Sync**; cookie WebView sí |

**Riesgos futuros:** rol SeesAll en BD con RoleCode no mapeado → UX incompleta; Outbox de usuario A visible tras login B en mismo dispositivo (protección por UserId en queries UX, bloqueos por EntityId).

---

## 12. Seguridad offline

| Elemento | Estado |
|----------|--------|
| SQLite local | Sin cifrado (SQLCipher no usado) |
| Outbox / snapshots Conflict | Datos de medición + metadatos en claro en app-data |
| DeviceId | Archivo local; nuevo Guid si reinstala |
| Cookie | Secure store MAUI; no en bridge genérico |
| Logout | No limpia Outbox (diseño: no perder pendientes) |

**Recomendación:** cifrado / retención / wipe al logout = **P4 Hardening**, no P0 (salvo política corporativa). No implementar en 2D.8.

---

## 13. Calidad NEPS

- Calificación: `AlertEvaluator` → `NepsQualityCriteria` (online y offline).
- Legacy `LimiteNormalMax`/`LimiteAdvertenciaMax` (30/60) no son fuente de verdad.
- `QualityLabel` en snapshots = presentación; resolución Offline recalcula desde Neps.
- `AlertasActivas` solo notificaciones.

**Sin hallazgos de reintroducción 30/60 en rutas productivas de calificación.**

---

## 14. Matriz de cobertura

Leyenda: ● = sí / real · ○ = parcial · — = no · ⚠ = warning entorno

| Capacidad | Web | Android Online | Android Offline | Push | Pull | Conflict | Estado |
|-----------|:---:|:--------------:|:---------------:|:----:|:----:|:--------:|--------|
| Create | ● | ● (WebView) | ● | ● | ● | — | Cubierto |
| Update | ● | ● (WebView) | ● | ● | ● | ● | Cubierto 2D.5–2D.7 |
| Delete | ● | ● (WebView) | ● | ● | ● | ● | Cubierto 2D.6–2D.7 |
| Corrective | ● | ● (WebView) | — | — | ○ (escalares) | — | Online-only |
| Import CSV/Excel | ● | — | — | — | ● (vía logs) | — | Online-only |
| Migración Firestore | ● (admin) | — | — | — | — | — | Sin ChangeLog |
| ClearAll | ○ (bloqueado c/ logs) | — | — | — | — | — | Admin pre-sync |
| DeleteMany | ● | — | — | — | ● | — | Online; Pull tombstones |
| Catalogs Tela/Lote | ● | ● (WebView) | ○ texto libre | — | — | — | Sin sync catálogo |
| Alerts SignalR | ● | — | — | — | — | — | Solo Blazor |
| Pagination | ○ in-memory | ○ | ○ Take pequeño | A sync | A sync | — | Riesgo volumen Web |
| Conflict resolution | — | — | ● | ○ (si Keep Local) | ○ | ● | Cubierto 2D.7 |

---

## 15. Prioridades

### P0 — Coherencia offline (pérdida/corrupción)

Ningún P0 abierto tras 2D.7 con evidencia actual: Create/Update/Delete + Conflict + ClearAll bloqueado con ChangeLogs cubren el núcleo.  
**Vigilancia:** no reactivar ClearAll con sync activo; no asumir histórico Firestore en cursor.

### P1 — Uso operativo

1. **Catálogo mínimo offline (Tela/Lote)** — captura usable con combos; depende Pull CatalogItem o seed embebido.  
2. **Android E2E smoke** — validar flujo real dispositivo.  
3. **ApplyCorrective offline** — solo si supervisores operan sin red; requiere protocolo.  
4. **URL servidor configurable / entorno** — si despliegues multi-servidor lo exigen.

### P2 — Escalabilidad

1. Paginación server-side Registros/Dashboard/Export.  
2. Estrategia ClearAll/DeleteMany masivo (A o B) si negocio lo exige.  
3. Retención/purge SyncChangeLogs.

### P3 — UX

1. SignalR (o polling) alertas en MAUI.  
2. Create-recovery post UpdateDelete (explícito, no Restore).  
3. Mejoras UX conflicto / multi-usuario dispositivo.

### P4 — Hardening

1. SQL Server físico E2E en CI/entorno.  
2. Cifrado SQLite / política wipe.  
3. Observabilidad Push/Pull/Conflict.  
4. Backfill ChangeLog histórico (diseño + SQL Server).

---

## 16. Roadmap propuesto (solo diseño)

| Candidato | Problema | Impacto | Dependencias | Riesgo | Esfuerzo | Prerrequisitos |
|-----------|----------|---------|--------------|--------|----------|----------------|
| Catálogos mínimos offline | Texto libre sin lista | Operativo captura | Pull CatalogItem o sync seed; OfflineStore | Datos obsoletos | M | Diseño payload CatalogItem |
| Android E2E | Runtime no verificado | Confianza release | Emulador/dispositivo, APK | Flaky | M | Builds Android OK |
| ApplyCorrective offline | Correctivo solo online | Operación supervisores offline | Protocolo Push + Outbox + permisos | Conflicto con Update | M–A | Decisión producto |
| SignalR recovery / alertas MAUI | Alertas no llegan a MAUI | UX | Hub auth cookie; no como fuente verdad | Duplicados | M | Definir superficie alertas MAUI |
| Paginación server-side | Take 50k | Escalabilidad | API query cursor/offset | Regresiones UI | M–A | Contratos query |
| ClearAll/DeleteMany sync-safe | Vaciado vs offline | Admin | Opción A/B + permisos | Volumen cursor | A | Decisión producto |
| Configurable server URL | Hardcode entornos | Deploy | MAUI config | Mal config | B | — |
| SQL Server backup/restore | Ops intranet | Continuidad | SQL Server físico | Downtime | M | Entorno SQL |
| Hardening SQLite | Datos en claro | Seguridad | SQLCipher/OS | Compatibilidad | M | Política seguridad |
| Backfill histórico | Migración sin logs | Sync incompleto | SQL Server + diseño | Duplicados | A | Inventario filas |
| Observabilidad | Diagnóstico sync | Ops | Logs/métricas | Ruido | B–M | — |

---

## 17. Dependencias (grafo)

```text
OfflineStore (LocalNepRecord, PendingOperation, LocalSession)
    └── Outbox
          └── SyncEngine
                ├── Push → SyncAppService (Create|Update|Delete)
                │         └── AtomicStore → SyncChangeLog
                ├── Pull ← SyncChangeLog (NepRecord only)
                └── ConflictResolution → (KeepServer local | nueva Outbox)

ApplyCorrective offline ──requiere──► extensión Push + Outbox (no existe)
Catálogos offline ──requiere──► Pull CatalogItem o seed + LocalCatalogItem servicios
SignalR recovery ──independiente──► SyncEngine (complementario: Pull al online)
Paginación Web ──independiente──► OfflineStore
ClearAll sync-safe ──requiere──► protocolo tombstone masivo o evento especial
                                  + política cursor; NO reabrir ClearAll actual
```

---

## 18. SQL Server

> **SQL Server físico no verificable en el entorno actual** (`(localdb)\mssqllocaldb` inaccesible).  
> SQLite **no** es equivalente para gates de producción intranet.

**Fases que requieren SQL Server físico:** backup/restore, backfill ChangeLog, validación índices/concurrencia bajo carga, CI intranet, ClearAll/DeleteMany masivos en datos reales.

---

## 19. Android E2E

> **Android E2E runtime pendiente.**

Smoke/E2E recomendado (diseño, no automatizar aún):

1. Login online → snapshot LocalSession  
2. Entrar modo offline  
3. Create → Outbox  
4. Update  
5. Delete  
6. Push Accepted  
7. Pull (otro cambio remoto)  
8. Conflict Update/Update  
9. Resolución Keep Server / Keep Local  
10. (Futuro) recuperación alertas / Pull tras reconexión  

---

## 20. Decisiones pendientes (antes de implementar 2D.9+)

1. ¿ApplyCorrective offline es requisito de negocio o permanece online-only?  
2. ¿Catálogos Tela/Lote offline: Pull sync vs listas embebidas/TTL?  
3. ¿ClearAll alguna vez con clientes offline (A vs B vs nunca)?  
4. ¿Alertas en MAUI (SignalR vs solo Pull/HTTP)?  
5. ¿Priorizar E2E Android y SQL Server gates antes de nuevas features offline?  
6. ¿Create-recovery post UpdateDelete (fase UX separada)?  
7. ¿Política de cifrado/wipe SQLite?

---

## Referencias de código (auditoría)

- `NepRecordService`, `AtomicNepRecordCreateStore`, `SyncAppService`, `SyncPersistence`  
- `OfflineCaptureService`, `SyncEngine`, `ConflictResolutionService`  
- `RecordImportService`, `HistoricalDataMigrationService`  
- `AlertNotificationHub`, `NotificationCenter.razor`  
- `LocalCatalogItem` / `LocalCatalogKind` (esquema sin uso)  
- `NepsQualityCriteria` / `AlertEvaluator`  
- Docs: `FASE2D7_CONFLICT_DESIGN.md`, `FASE2D7_CONFLICT_IMPLEMENTATION.md`

---

*Fin FASE 2D.8. No avanzar automáticamente a implementación 2D.9.*
