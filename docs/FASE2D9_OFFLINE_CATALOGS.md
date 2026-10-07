# FASE 2D.9 — Catálogos mínimos offline

## Problema

La captura nativa offline (MAUI) permite texto libre en Tela y Lote, pero en online el operador selecciona valores de catálogo (`Fabric` / `LoteTramaItem`). `LocalCatalogItem` existía desde FASE 2A sin uso. Sin Pull de catálogos, la UX offline no puede ofrecer opciones válidas ni reflejar altas/bajas del servidor.

## Alcance

- Sincronización **server → client** de un subconjunto de referencia para captura: **Tela (Fabric)** y **Lote (LoteTramaItem)**.
- Reutilización de `LocalCatalogItem` + cursor global de Pull (`SyncChangeLog`).
- UI MAUI: pickers locales + texto libre de respaldo.
- **Fuera de alcance:** ApplyCorrective, ClearAll, SignalR, paginación server-side, cifrado SQLite, réplica completa, Outbox de catálogos, resolución de conflictos nueva.

## Catálogos incluidos y excluidos

| Catálogo | Decisión | Motivo |
|----------|----------|--------|
| Tela (`Fabric`) | **Incluido** | P1 captura; Id Guid estable; `IsActive` |
| Lote (`LoteTramaItem`) | **Incluido** | P1 captura; código estable; `IsActive` |
| Telar | Excluido | Texto libre en captura; no hay catálogo servidor obligatorio |
| Turno | Excluido | Texto libre (A/B/C); sin entidad de catálogo |
| Operario | Excluido | Texto libre; usuarios no se replican |
| Usuarios / roles / permisos | Excluidos | Autoridad servidor; `LocalSession` solo UX |
| NepRecord ajenos / históricos / dashboards | Excluidos | No son catálogo de captura |

## Modelo de datos

### Servidor (`SyncChangeLog`)

- `EntityType = CatalogItem`
- `ChangeType = CatalogUpserted | CatalogDeleted`
- `OwnerUserId = catalog` (sintético; Pull autoriza a cualquier usuario autenticado)
- Payload JSON: `{ id, kind, code, name, isActive, updatedAtUtc }`
  - `kind`: `Fabric` | `Lote`

### Cliente (`LocalCatalogItem` — sin migración de esquema)

| Campo | Uso |
|-------|-----|
| `Id` | Guid del servidor (identidad estable) |
| `Kind` | Fabric / Lote |
| `Code` / `Name` | Visualización y selección |
| `IsActive` | Filtra opciones válidas; delete → `false` |
| `UpdatedAtUtc` | Orden / frescura |

No se crean tablas nuevas. Instalaciones existentes con la migración OfflineStore que ya incluye `LocalCatalogItems` no requieren migración aditiva extra.

## Flujo server → Pull → SQLite → UI

1. Mutación de Fabric/Lote en repositorio → `CatalogSyncChangeWriter` emite ChangeLog atómico.
2. Arranque / primer Pull: `EnsureCatalogBaselineAsync` genera baseline idempotente para filas sin log previo.
3. Cliente `SyncEngine.Pull` aplica `CatalogUpserted` / `CatalogDeleted` → `LocalCatalogItem` (sin tocar Outbox NEPS).
4. Cursor avanza con la página; cambios de catálogo no generan `Conflict` de registros.
5. `OfflineCatalogService` lista solo activos; `OfflineCapturePage` llena pickers y muestra estado UX.

## Identidad

- Identidad = **Guid servidor** (`Fabric.Id` / `LoteTramaItem.Id`).
- No se generan IDs locales de catálogo.
- Unique local `(Kind, Code)`: ante rename/clash, la fila antigua se desactiva y se ajusta `Code` para evitar violación.

## Seguridad

- Autoridad: servidor.
- Sin Push de catálogos; `UpsertCatalog` → `OPERATION_TYPE_UNSUPPORTED`.
- Sin Outbox de catálogos.
- Cliente no puede reactivar un ítem inactivo salvo nuevo `CatalogUpserted` del servidor.
- Permisos de mutación de catálogo siguen en administración online; offline solo lee.

## Estado activo/inactivo

- `IsActive=false` (update o delete físico en servidor) → local `IsActive=false`.
- No borrado físico local automático: el ítem deja de aparecer en pickers activos.
- Texto libre permanece permitido (dominio actual de captura).

## Cursor

- Mismo cursor global `LastPulledSequence`.
- Baseline + mutaciones incrementan secuencia como cualquier ChangeLog.
- Baseline idempotente: no duplica logs si el EntityId ya tiene entrada `CatalogItem`.

## Compatibilidad con versiones

- ProtocolVersion v1 ampliado con EntityType/ChangeType de catálogo (explícitos).
- Clientes antiguos que ignoran ChangeType desconocidos avanzan cursor sin aplicar (comportamiento SyncEngine previo).
- Create/Update/Delete NEPS, tombstones, Conflict, Keep Server/Local, Edit&Retry: sin cambio de contrato.

## Comportamiento sin catálogo

- Si nunca hubo Pull: `HasAnyCatalog=false` → mensaje UX; captura por texto libre.
- Sin consultas de red desde la pantalla offline.
- No se inventan valores.

## Decisiones descartadas

- Réplica completa de dominio / usuarios / roles.
- Outbox bidireccional de catálogos.
- EntityType por tabla (`Fabric` vs `Lote`) separado del unificado `CatalogItem` (se usa `kind` en payload).
- Migración SQLite nueva (el modelo 2A basta).
- Cifrado SQLite (P4).
- Forzar selección estricta sin texto libre (rompería dominio actual).

## Pruebas

Suite `OfflineCatalogSyncTests`:

1. Pull inicial + identidad estable  
2. Pull incremental (alta + rename)  
3. Desactivación / delete → inactivo local  
4. Catálogo ausente → texto libre atómico  
5. Push no acepta mutación de catálogo  
6. Pull no crea Conflict NEPS ni réplica fuera de Fabric/Lote  
7. Persistencia tras reinicio + baseline idempotente  
8. Create NEPS sigue idempotente con ChangeLogs de catálogo  

## Limitaciones

- Android E2E dispositivo/emulador: depende del entorno local.
- SQL Server físico: no disponible en harness de validación (se usa SQLite :memory: como en gates 2D.4+).
- Telar/Turno/Operario siguen texto libre.
- UI online Blazor no se sustituye; solo captura offline MAUI consume catálogos locales.

## Riesgos pendientes

- Dispositivos que nunca hacen Pull online no tendrán pickers (texto libre OK).
- Choques Unique(Kind, Code) en rename agresivo: mitigados desactivando clash; revisar si en producción hay códigos duplicados históricos.
- Android E2E y SQL Server intranet: validación pendiente de entorno.
