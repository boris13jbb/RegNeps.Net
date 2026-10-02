# Criterios oficiales de calidad NEPS (RegNeps.Net)

## Fuente de verdad

La calificación de calidad vive en `RegNeps.Domain.Constants.NepsQualityCriteria`.

| Nivel | Q (puntaje) | NEPS/m² |
|-------|-------------|---------|
| OK | 0–18 | ≤ 200 |
| Mención | 19–45 | > 200 y ≤ 500 |
| Crítico - Realizar Ajuste | 46–54 | > 500 y ≤ 600 |
| 2da Calidad | ≥ 55 | > 600 |

Fórmula: **NEPS/m² = NEPS / 0.09** (`NepsConstants.TestLengthM = 0.09`).

## Q frente a NEPS/m²

- `NepRecord.Neps` es el **puntaje Q** medido en 0.09 m².
- Con Q entero, Q/0.09 coincide exactamente con las bandas de densidad (18→200, 45→500, 54→600).
- Clasificación **operativa** de un registro: `ClassifyByNeps` → redondeo `AwayFromZero` a Q entero → bandas por score.
- `ClassifyByNepsPerM2` aplica bandas continuas de densidad (leyenda/tests). Con Q entero ambas coinciden.
- Ante Neps fraccionarios, manda el Q redondeado (misma regla en SQL vía cotas exclusivas 18.5 / 45.5 / 54.5).

## AlertasActivas vs calificación

- `AlertasActivas = false` **solo** desactiva notificaciones automáticas (SignalR).
- **No** fuerza la calificación a OK.
- SignalR crítico se dispara para `CriticalAdjustment` y `SecondQuality` cuando las alertas están activas.

## Columnas legacy BD

`AlertConfigs.LimiteNormalMax` / `LimiteAdvertenciaMax` se conservan por compatibilidad.
**No** determinan la calidad. Defaults de seed: 18 / 45 (valores alineados a Q, no usados por el evaluador).

## SnapshotJson

`SavedReports.SnapshotJson` guarda filas de `NepRecord` (datos crudos: Neps, telar, etc.).
La etiqueta de calidad **no** se persiste ahí; al reabrir/exportar se recalcula con `NepsQualityCriteria`.
No hay recalificación masiva de `NepRecords` históricos: la clasificación se aplica al consultar.

## UI Configuración

Editable: `AlertasActivas`, `CantidadReincidenciasCriticas`, `DiasParaReincidencia`.
Umbrales 30/60 eliminados de la UI; se muestra leyenda fija oficial.
