# FASE 2F — Paginación server-side de registros Web

## Resumen

Objetivo:

> El servidor debe devolver únicamente la página solicitada, aplicando filtros, ordenación y autorización en SQL antes de materializar los registros.

Estado de cierre previsto: **PASS CON WARNINGS** (Android E2E y SQL Server físico de entorno, si no disponibles).

## Puntos auditados

| Área | Comportamiento previo | Decisión 2F |
|------|----------------------|-------------|
| **Registros.razor** | `QueryAsync` hasta 1k/50k → Skip/Take en Blazor | **Paginado** vía `QueryPagedAsync` |
| **Dashboard.razor** | `QueryAsync(50k)` solo para últimos 15 + contar pendientes | **Ajustado**: página 15 + `CountFiltered(SoloPendientes)` |
| **Captura.razor** | `Take(500)` PersonalOnly (sesión) | **Conservado** (límite de seguridad de sesión) |
| **Export / ReportExport** | `take` alto para exportar filtrados | **Conservado** (export ≠ página UI) |
| **Analytics / Gráficas / Constructor** | Agregaciones o Take acotado | **Conservado** (no listas paginadas) |
| **GetAlertsAsync** | `QueryAsync(1000)` + filtro en memoria | **Conservado** (fuera de alcance; no es Registros) |
| **GetDashboardSummaryAsync** | Take acotado | **Conservado** (API legacy; Dashboard usa Analytics) |
| **Sync Pull / Outbox / SignalR** | — | **Sin cambios** |
| **ClearAll / QueryAsync Take** | Take 500/10k/50k como techo | **Conservado** como contrato no-UI |

## Consultas modificadas

1. `INepRecordRepository.QueryPagedAsync` / `CountFilteredAsync`
2. `NepRecordService.QueryPagedAsync` / `CountFilteredAsync`
3. `Registros.razor` — carga viva paginada; snapshot de informe sigue en memoria (diseño del informe)
4. `Dashboard.razor` — deja de materializar 50k para «últimos»/pendientes

## Contrato de paginación

```text
PagedResult<T>
  Items
  PageNumber
  PageSize
  TotalCount
  TotalPages / HasPreviousPage / HasNextPage
```

`RecordPaging`:

| Constante | Valor |
|-----------|-------|
| DefaultPageSize | 50 |
| MinPageSize | 1 |
| MaxPageSize | 100 |

`pageSize=0` → default 50. Valores enormes se clampean a 100.  
El `Take(500)` / `50_000` de `QueryAsync` **no** es PageSize UI: es techo de seguridad para export/captura/analytics.

## Autorización

Orden obligatorio:

```text
WHERE ownership / SeesAll (+ ExternalUserId)
+ filtros de negocio
→ COUNT(*)
→ ORDER BY …
→ OFFSET/FETCH (Skip/Take)
```

- Sin `viewerSeesAll` y sin `viewerUserId` → fail-closed (vacío).
- `SeesAllRecords` no altera la matriz de permisos; solo el alcance de filas.
- Servicio exige `ViewRecords` o `CaptureRecords` (igual que `QueryAsync`).
- No se introdujeron reglas nuevas.

## Ordenación determinista (UI paginada)

```text
ORDER BY CreatedAt DESC, Id DESC
```

- Evita duplicados/omisiones entre páginas cuando hay empates de `CreatedAt`.
- `QueryAsync` legacy conserva `CreatedAt DESC, Telar ASC` por compatibilidad.

## Filtros

Reutilizan el mismo builder SQL que `QueryAsync`:

fechas, calidad (`NepsQualityCriteria` / `AlertLevel` / `SoloPendientes`), telar, tela, lote, turno, operario, línea, search, neps/mts, revisión, acción correctiva, `CaptureSessionId`.

No se inventaron filtros nuevos. No se usan `LimiteNormalMax` / `LimiteAdvertenciaMax`.

## COUNT

`QueryPagedAsync` ejecuta `COUNT(*)` con los mismos predicados y luego la página.  
`CountFilteredAsync` expone solo el conteo (Dashboard pendientes).

## Compatibilidad SQLite / SQL Server

LINQ/EF Core (`Skip`/`Take` → OFFSET/FETCH o equivalente SQLite). Sin SQL propietario.

## Índices

Existentes relevantes:

- `IX` sobre `CreatedAt`
- `CreatedByUserId` + `ClientOperationId` / `CaptureSessionId`

**No se añadieron índices nuevos** (no especulativos). El índice de `CreatedAt` cubre el ORDER BY principal.

## N+1

La página materializa entidades `NepRecord` sin `Include` de historial (igual que el listado previo). Detalle/edición sigue cargando por Id cuando aplica.

## Exportaciones

Sin cambio de semántica: exportar filtrados/completos sigue usando `QueryAsync` / servicios de export. No se convierte export a «página actual» salvo que la UI ya seleccionara IDs.

## Riesgos conocidos

- Snapshot de informe guardado sigue cargando todas las filas del snapshot (contrato del informe).
- `GetAlertsAsync` / analytics densos pueden seguir trayendo hasta su techo; refactor aparte.
- COUNT en rangos muy amplios en SQL Server puede ser costoso; mitigación futura: totales aproximados o caché, no implementado aquí.
- Dashboard «pendientes» usa `SoloPendientes` SQL (`Neps >= OkUpper && !Revisado`), alineado con `RequiereSeguimiento` / `RequiresFollowUp`.

## Qué NO hace esta fase

Paginación de Sync Pull, cambios SyncChangeLog/SignalR/Outbox/conflictos/ClearAll, Firebase, cifrado SQLite, rediseño completo Dashboard/Export, cambios NEPS/permisos.
