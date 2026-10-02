# Inventario funcional completo — RegNeps.Net

| Campo | Valor |
| ----- | ----- |
| Proyecto | RegNeps.Net |
| Tipo | ASP.NET Core 8 + Blazor Server + cliente MAUI Android (WebView) |
| Fecha de auditoría | 2026-10-01 |
| Rama analizada | `feat/captura-session-save-share` |
| Remoto | `https://github.com/boris13jbb/RegNeps.Net.git` |
| Alcance | Solo lectura; sin modificación de código de aplicación |
| Método | Inspección de código fuente, configuración, tests, workflows y documentación del repositorio |

> **Aviso de alcance.** Este repositorio **no es Flutter**. No existen `lib/`, `pubspec.yaml`, `firebase.json`, `firestore.rules` ni runtime Firebase/Firestore en la aplicación .NET. La app Flutter hermana (`Documents\regneps` / calculadora_neps) es un proyecto aparte. Un archivo `INVENTARIO_FUNCIONAL_COMPLETO.md` en la raíz del repo describe ese sistema Flutter y **no** debe usarse como inventario de RegNeps.Net.

> **Etiquetas de clasificación.** En este código las categorías visibles son **Normal**, **Advertencia** y **Crítico**. No existen en UI ni PDF las etiquetas «OK», «Mención», «Crítico — Realizar Ajuste» ni «2da Calidad». Esas nomenclaturas pertenecen al sistema Flutter hermano.

---

## Índice

1. [Resumen ejecutivo](#1-resumen-ejecutivo)
2. [Inventario del repositorio](#2-inventario-del-repositorio)
3. [Mapa general del sistema](#3-mapa-general-del-sistema)
4. [Arquitectura y flujo de datos](#4-arquitectura-y-flujo-de-datos)
5. [Inventario de pantallas](#5-inventario-de-pantallas)
6. [Navegación](#6-navegación)
7. [Módulos funcionales](#7-módulos-funcionales)
8. [Reglas de negocio NEPS](#8-reglas-de-negocio-neps)
9. [Captura (detalle)](#9-captura-detalle)
10. [Registros (detalle)](#10-registros-detalle)
11. [Informes guardados (detalle)](#11-informes-guardados-detalle)
12. [Generación de PDF](#12-generación-de-pdf)
13. [Generación de CSV](#13-generación-de-csv)
14. [Generación de Excel](#14-generación-de-excel)
15. [Sistema de compartir](#15-sistema-de-compartir)
16. [Dashboard, alertas y analítica](#16-dashboard-alertas-y-analítica)
17. [Configuración](#17-configuración)
18. [Autenticación](#18-autenticación)
19. [Usuarios, roles y permisos](#19-usuarios-roles-y-permisos)
20. [Firebase / Firestore](#20-firebase--firestore)
21. [Modelos de datos](#21-modelos-de-datos)
22. [Servicios](#22-servicios)
23. [Repositorios y persistencia](#23-repositorios-y-persistencia)
24. [Sincronización y offline](#24-sincronización-y-offline)
25. [Manejo de errores](#25-manejo-de-errores)
26. [Permisos de plataforma](#26-permisos-de-plataforma)
27. [Notificaciones](#27-notificaciones)
28. [Tests](#28-tests)
29. [CI/CD](#29-cicd)
30. [Deployment](#30-deployment)
31. [Versiones y dependencias](#31-versiones-y-dependencias)
32. [Funcionalidades legacy](#32-funcionalidades-legacy)
33. [Flujos end-to-end](#33-flujos-end-to-end)
34. [Casos límite](#34-casos-límite)
35. [Funcionalidades ocultas o poco visibles](#35-funcionalidades-ocultas-o-poco-visibles)
36. [Diferencias por plataforma](#36-diferencias-por-plataforma)
37. [Seguridad funcional](#37-seguridad-funcional)
38. [Glosario](#38-glosario)
39. [Matriz maestra de funcionalidades](#39-matriz-maestra-de-funcionalidades)
40. [Matriz de cobertura](#40-matriz-de-cobertura)
41. [Matriz de dependencias](#41-matriz-de-dependencias)
42. [Hallazgos de la auditoría](#42-hallazgos-de-la-auditoría)
43. [Conclusión e inventario final](#43-conclusión-e-inventario-final)

---

## 1. Resumen ejecutivo

### Propósito

RegNeps.Net es un sistema de **control de calidad textil (neps)** orientado a la operación VICUNHA. Permite capturar mediciones por telar, clasificarlas por umbrales configurables, consultar historial, gestionar alertas y acciones correctivas, administrar catálogos (telas/lotes), generar informes PDF/Excel/CSV y compartir archivos desde navegador o APK Android (WebView).

### Usuarios

Usuarios locales en base de datos SQL/SQLite, con roles: Operario, Supervisor, Administrador, Gerencia, Super administrador. Autorización por matriz de permisos (`AppPermission`) persistida y editable por Super Admin.

### Principales módulos

Autenticación · Dashboard · Captura · Registros · Alertas · Análisis/Gráficas · Informes · Exportar · Telas · Lotes · Usuarios · Roles y permisos · Configuración de alertas · Migración histórica Firestore→SQL · Cliente Android MAUI.

### Plataformas

| Plataforma | Estado en este repo |
| ---------- | ------------------- |
| Web (Blazor Server) | COMPLETA — host principal |
| Android (MAUI WebView) | COMPLETA como shell; lógica en servidor |
| iOS | NO DISPONIBLE (sin target) |
| Windows / macOS / Linux escritorio nativo | NO DISPONIBLE como app; el servidor .NET corre en Windows/Linux |
| Flutter | FUERA DE ESTE REPO |

### Almacenamiento

- Desarrollo: SQLite (`regneps_v2.db` bajo `App_Data`)
- Intranet/producción: SQL Server (configurable)
- Exportaciones temporales en memoria (`TempExportStore`, TTL 5 min)
- Sin SharedPreferences de negocio (solo Preferences MAUI para URL del servidor)
- Sin Hive / sin Firestore en runtime

### Autenticación

Cookies (`RegNeps.Auth`), contraseñas BCrypt, sin ASP.NET Identity, sin Firebase Auth en runtime.

### Exportaciones y sharing

QuestPDF (PDF completo/clásico), ClosedXML (Excel/CSV), endpoints `/api/export/*`, puente nativo MAUI `Share.Default` + JS `regnepsDownload.shareOrDownload`.

### Integración Firebase

Solo **herramientas de migración** (script Node `firebase-admin`, JSON en `FTS/`, UI `/migracion`, CLI `RegNeps.Migrate`). **No** hay Firebase en el runtime Blazor/MAUI.

---

## 2. Inventario del repositorio

### 2.1 Estructura de carpetas (nivel superior)

```text
RegNeps.Net/
├── .github/workflows/     # CI .NET + APK Android QA
├── apk/                   # Artefactos APK (no lógica)
├── docs/                  # Documentación (este inventario + despliegue/migración/APK)
├── FTS/                   # Exports JSON Firestore (migración)
├── publish/               # Salida publish
├── scripts/               # build_android_apk.ps1, export_firestore_history.js
├── secrets/               # Credenciales locales (NO documentar valores)
├── src/                   # Código de aplicación
│   ├── RegNeps.Domain/
│   ├── RegNeps.Application/
│   ├── RegNeps.Infrastructure/
│   ├── RegNeps.Web/
│   └── RegNeps.Mobile/
├── tests/RegNeps.Tests/
├── tools/RegNeps.Migrate/
└── RegNeps.sln
```

### 2.2 Lo que el pedido Flutter pedía y NO existe aquí

| Ruta / artefacto pedido | Estado |
| ----------------------- | ------ |
| `lib/`, `pubspec.yaml` | AUSENTE |
| `android/`, `ios/`, `web/`, `windows/`, `macos/`, `linux/` (Flutter) | AUSENTE |
| `functions/`, `firebase.json`, `firestore.rules` | AUSENTE |
| `analysis_options.yaml` | AUSENTE |
| Equivalente real | `src/`, `tests/`, `RegNeps.Mobile/Platforms/Android`, `.github/workflows` |

### 2.3 Conteos aproximados (excl. bin/obj/node_modules/.git)

| Tipo | Cantidad |
| ---- | -------- |
| Archivos `.cs` (src/tests/tools, excl. bin/obj) | 65 |
| Archivos `.razor` | 35 |
| Líneas `.cs` | 9 098 |
| Líneas `.razor` | 7 839 |
| Total líneas `.cs` + `.razor` | 16 937 |
| Proyectos `.csproj` | 7 |
| Workflows YAML | 2 |
| Tests unitarios (archivos) | 10 |

### 2.4 Arquitectura

Capas limpias:

```text
UI Blazor / MAUI WebView
        ↓
RegNeps.Application (casos de uso)
        ↓
RegNeps.Domain (entidades, reglas, permisos)
        ↓
RegNeps.Infrastructure (EF Core, export, import, migración)
        ↓
SQLite / SQL Server
```

---

## 3. Mapa general del sistema

```text
RegNeps.Net
│
├── Autenticación (cookies + BCrypt)
├── Panel principal (Dashboard)
├── Captura (sesión + share + informe sesión)
├── Registros (filtros + import + edición)
├── Alertas + acciones correctivas
├── Análisis / Gráficas
├── Informes guardados
├── Exportar (PDF/Excel/CSV)
├── Catálogo Telas
├── Catálogo Lotes
├── Usuarios
├── Roles y permisos
├── Configuración de alertas
├── Migración histórica (Firestore → SQL)
├── API de exportación / importación
├── Cliente Android MAUI (WebView + share nativo)
└── Herramientas: CLI Migrate, script export Firestore
```

---

## 4. Arquitectura y flujo de datos

### 4.1 Flujo típico

```text
Página Blazor (.razor)
  → CurrentUserService / IPermissionService
  → NepRecordService | AnalyticsService | ReportExportAppService | AuthService | …
  → I*Repository
  → RegNepsDbContext (EF Core)
  → SQLite / SQL Server
```

### 4.2 Exportar / compartir

```text
UI (Captura / Exportar / Gráficas)
  → ReportExportAppService / ExportFileService
  → TempExportStore.Put (bytes + ownerUserId)
  → GET /api/export/temp/{id}
  → JS shareOrDownload  OR  MAUI regneps-share://file
```

### 4.3 Autorización

```text
Cookie claims (identidad + rol)
  → IPermissionService (matriz en BD, revalidable)
  → UI oculta acciones
  → Servicios rechazan sin permiso (no solo UI)
```

---

## 5. Inventario de pantallas

| ID | Pantalla | Ruta / archivo | Acceso | Funcionalidades principales | Estado |
| -- | -------- | -------------- | ------ | --------------------------- | ------ |
| SCR-01 | Home | `/` · `Home.razor` | Cualquiera | Redirección login/dashboard | COMPLETA |
| SCR-02 | Login | `/login` · `Login.razor` | Anónimo | Login cookie | COMPLETA |
| SCR-03 | Dashboard | `/dashboard` · `Dashboard.razor` | `ViewDashboard` | KPIs, charts, últimos registros | COMPLETA |
| SCR-04 | Captura | `/captura` · `Captura.razor` | `CaptureRecords` | Alta, sesión, share, informe | COMPLETA |
| SCR-05 | Registros | `/registros` · `Registros.razor` | `ViewRecords` | Filtros, paginación, multi-select, import CSV/Excel, solo lectura informe | COMPLETA |
| SCR-06 | Alertas | `/alertas` · `Alertas.razor` | `ViewAlerts` | Listado no-normales, correctivas | COMPLETA |
| SCR-07 | Gráficas | `/graficas` · `Graficas.razor` | `ViewDashboard` | Analytics + export + prefs localStorage | COMPLETA |
| SCR-08 | Informes | `/informes` · `Informes.razor` | `ManageReports` | CRUD informes + export completo/clásico | COMPLETA |
| SCR-09 | Exportar | `/exportar` · `Exportar.razor` | `ExportReports` | Preview/export PDF/XLSX/CSV + columnas | COMPLETA |
| SCR-10 | Reportes | `/reportes` · `Reportes.razor` | Auth | Redirect a `/exportar` | COMPLETA (alias) |
| SCR-11 | Telas | `/telas` · `Telas.razor` | `ManageFabrics` | CRUD + import/export | COMPLETA |
| SCR-12 | Lotes | `/lotes` · `Lotes.razor` | `ManageFabrics` | CRUD + export | COMPLETA |
| SCR-13 | Usuarios | `/usuarios` · `Usuarios.razor` | `ManageUsers` | CRUD usuarios, roles BD, reset ≥8 | COMPLETA |
| SCR-14 | Roles | `/roles` · `Roles.razor` | SuperAdmin + `ManageRoles` | Roles parametrizables + matriz | COMPLETA |
| SCR-15 | Config | `/config` · `Config.razor` | `ViewSettings` / `EditAlertConfig` | Umbrales alerta | COMPLETA |
| SCR-16 | Migración | `/migracion` · `Migracion.razor` | SuperAdmin | Import JSON Firestore | COMPLETA |
| SCR-17 | Error | `/Error` · `Error.razor` | Anónimo | Página de error | COMPLETA |
| SCR-18 | Constructor informes | `/constructor-informes` · `ConstructorInformes.razor` | `ExportReports` | Agrupación, stats, PDF/Excel profesional | COMPLETA |

**Componentes compartidos (sin `@page`):** `MainLayout`, `EmptyLayout`, `NavMenu`, `CircleMenu`, `PageHeader`, `KpiCard`, `EmptyState`, `LoadingBlock`, `StatusBadge`, `AppModal`, `HubTabs`, `MorphButton`, `NotificationCenter`, `PermissionRefresh`, `RedirectToLogin`, `Routes`, `App`.

### 5.1 Detalle por pantalla (resumen funcional)

#### SCR-01 Home

- Objetivo: enrutar al destino correcto.
- Layout: mínimo («Redirigiendo…»).
- Autenticado → `/dashboard`; no autenticado → `/login`.

#### SCR-02 Login

- Layout: `EmptyLayout`.
- Formulario: usuario + contraseña → `POST /api/login`.
- Error query `?error=1`: «Usuario o contraseña incorrectos, o cuenta inactiva.»
- En Development muestra hint del seed `admin` / `Admin123!`.

#### SCR-03 Dashboard

- Filtro periodo: 7 / 30 / 90 días (default 30).
- KPIs: Registros, Promedio neps, Total mts, Críticos, Advertencias, Pendientes (con deltas).
- Charts: tendencia neps; donut Normal / Advertencia / Crítico.
- Tabla últimos registros (hasta 15, página de 5).
- Atajos a Captura / Registros / Alertas / Gráficas / Informes según permisos.

#### SCR-04 Captura

Ver [§9](#9-captura-detalle).

#### SCR-05 Registros

Ver [§10](#10-registros-detalle).

#### SCR-06 Alertas

- Lista mediciones con nivel ≠ Normal (hasta 1000 visibles).
- Badge estado + «Pendiente revisión» si requiere seguimiento.
- Modal acción correctiva (acción + responsable) con `ApplyCorrectiveAction`.

#### SCR-07 Gráficas

- Filtros: agrupación, periodo, telar, tela, lote, turno, alerta, operario, rankings.
- KPIs: Registros, Promedio/Total neps, Mts, Advertencias, Críticos, % normales, Índice calidad.
- Export CSV/Excel/PDF + captura PNG de gráficos.

#### SCR-08 Informes / SCR-09 Exportar

Ver [§11–15](#11-informes-guardados-detalle).

#### SCR-11 Telas / SCR-12 Lotes

- Catálogos activos/inactivos; export CSV/XLSX; Telas admite import Excel (máx. 5 MB).

#### SCR-13 Usuarios / SCR-14 Roles / SCR-15 Config / SCR-16 Migración

Ver [§17–20](#17-configuración).

---

## 6. Navegación

### 6.1 Mapa

```text
Login (/login)
  ↓ cookie
Home (/) → Dashboard (/dashboard)
  ├── Captura (/captura)
  ├── Registros (/registros)
  ├── Alertas (/alertas)
  ├── Análisis (/graficas)
  ├── Informes (/informes)
  │     └── Exportar (/exportar)  ← también desde Reportes (/reportes)
  ├── Telas (/telas)
  ├── Lotes (/lotes)
  ├── Usuarios (/usuarios)
  ├── Roles (/roles)              [SuperAdmin]
  ├── Migración (/migracion)      [SuperAdmin]
  └── Configuración (/config)
```

### 6.2 Guards y redirecciones

| Mecanismo | Ubicación | Comportamiento |
| --------- | --------- | -------------- |
| `[Authorize]` global | `_Imports.razor` | Todas las páginas excepto Login/Error |
| `AuthorizeRouteView` | `Routes.razor` | No autenticado → `RedirectToLogin` |
| Chequeo de permiso en página | cada `.razor` | Mensaje denegado o redirect (Informes→Exportar) |
| Chequeo en servicio | `NepRecordService`, etc. | Excepción / outcome Unauthorized |
| Cookie LoginPath | `Program.cs` | `/login` |

### 6.3 Menús

- **NavMenu** (sidebar): secciones por permiso.
- **CircleMenu**: atajos Inicio, Captura, Registros, Alertas, Informes.
- **Topbar**: breadcrumbs, `NotificationCenter`, usuario, logout `POST /api/logout`.

### 6.4 Query params relevantes

| Destino | Parámetros observados |
| ------- | --------------------- |
| `/registros` | filtros + `reportId` (abrir desde informe / snapshot) |
| `/exportar` | filtros de periodo/telar/tela/estilo |
| `/login` | `error=1` |

---

## 7. Módulos funcionales

A continuación, cada funcionalidad con ID único. El detalle exhaustivo de Captura/NEPS/Export está en secciones dedicadas; aquí se resume el catálogo.

### MÓDULO: AUTENTICACIÓN

#### AUTH-001 Login

- **Descripción:** Autentica usuario local y emite cookie.
- **Ubicación:** `Login.razor`, `AuthService`, `POST /api/login`, `AuthClaims`.
- **Actor:** Cualquier visitante anónimo.
- **Precondiciones:** Usuario activo, no eliminado, contraseña BCrypt válida.
- **Entradas:** Username, password.
- **Proceso:** Validar → verificar hash → claims (userId, username, display_name, role, is_super_admin, external_user_id) → cookie 12 h.
- **Persistencia:** Cookie; `LastLoginAt` en BD.
- **Errores:** Credenciales inválidas / cuenta inactiva.
- **Plataformas:** Web; Android vía WebView.
- **Tests:** Cubierto indirectamente por flujos de seed/captura.
- **Estado:** COMPLETA

#### AUTH-002 Logout

- **Ubicación:** NavMenu → `POST /api/logout`.
- **Resultado:** Cierra cookie y vuelve a login.
- **Estado:** COMPLETA

#### AUTH-003 Revalidación de sesión / permisos

- **Ubicación:** `RegNepsRevalidatingAuthStateProvider`, `PermissionRefresh`.
- **Proceso:** Permisos se resuelven en runtime (no van en cookie); revocación se refleja sin exigir logout.
- **Estado:** COMPLETA

#### AUTH-004 Recuperación / reset de contraseña (self-service)

- **Estado:** DOCUMENTADO PERO NO IMPLEMENTADO (no hay flujo «olvidé mi contraseña»). Existe **reset por administrador** en Usuarios (USR-003).

---

### MÓDULO: DASHBOARD

#### DASH-001 Panel de calidad

- **Ubicación:** `Dashboard.razor`, `AnalyticsService`.
- **Filtros:** 7/30/90 días.
- **KPIs / charts:** ver §16.
- **Categorías UI:** Normal, Advertencia, Crítico.
- **Estado:** COMPLETA

---

### MÓDULO: CAPTURA

Funcionalidades: CAP-001…CAP-017 — detalle en §9.

| ID | Nombre | Estado |
| -- | ------ | ------ |
| CAP-001 | Registrar medición | COMPLETA |
| CAP-002 | Preview Mts | COMPLETA |
| CAP-003 | Clasificar alerta post-guardado | COMPLETA |
| CAP-004 | Nuevo registro (misma sesión) | COMPLETA |
| CAP-005 | Nueva sesión | COMPLETA |
| CAP-006 | Editar registro de sesión | COMPLETA |
| CAP-007 | Eliminar registro de sesión | COMPLETA |
| CAP-008 | Selección múltiple | COMPLETA |
| CAP-009 | Compartir un registro (archivo) | COMPLETA |
| CAP-010 | Compartir seleccionados | COMPLETA |
| CAP-011 | Compartir sesión | COMPLETA |
| CAP-012 | Guardar informe pendientes de sesión | COMPLETA |
| CAP-013 | Confirmación Neps &gt; 100 | COMPLETA |
| CAP-014 | Detección duplicado reciente (&lt; 2 min) | COMPLETA |
| CAP-015 | Selector estilo Completo/Clásico al compartir | COMPLETA |
| CAP-016 | Compartir hoy (prioridad last/selección/hoy) | COMPLETA |
| CAP-017 | Seleccionar todos los de hoy (manual) | COMPLETA |

---

### MÓDULO: REGISTROS

| ID | Nombre | Estado |
| -- | ------ | ------ |
| REG-001 | Listar / filtrar | COMPLETA |
| REG-002 | Editar registro | COMPLETA |
| REG-003 | Eliminar registro | COMPLETA |
| REG-004 | Acción correctiva desde lista | COMPLETA |
| REG-005 | Importar Excel | COMPLETA |
| REG-006 | Descargar plantilla import | COMPLETA |
| REG-007 | Vaciar todos | COMPLETA |
| REG-008 | Abrir desde informe (snapshot / solo lectura) | COMPLETA |
| REG-009 | Paginación (50/página) | COMPLETA |
| REG-010 | Selección múltiple + acciones en lote | COMPLETA |
| REG-011 | Borrado en lote con resumen | COMPLETA |
| REG-012 | Guardar informe de la selección | COMPLETA |
| REG-013 | Importar CSV (UTF-8, `,`/`;`) + resultado por fila | COMPLETA |

---

### MÓDULO: ALERTAS

| ID | Nombre | Estado |
| -- | ------ | ------ |
| ALT-001 | Listar alertas no normales | COMPLETA |
| ALT-002 | Aplicar acción correctiva | COMPLETA |
| ALT-003 | Detectar reincidencia crítica | COMPLETA |
| ALT-004 | Push en vivo SignalR al crear crítico | COMPLETA |

---

### MÓDULO: ANALÍTICA

| ID | Nombre | Estado |
| -- | ------ | ------ |
| ANA-001 | KPIs y series filtrables | COMPLETA |
| ANA-002 | Export analytics CSV/Excel/PDF | COMPLETA |
| ANA-003 | Captura PNG de gráficos | COMPLETA |
| ANA-005 | Preferencias periodo/filtros (localStorage) | COMPLETA |

---

### MÓDULO: INFORMES Y EXPORTACIÓN

| ID | Nombre | Estado |
| -- | ------ | ------ |
| INF-001 | Guardar informe | COMPLETA |
| INF-002 | Listar / editar / eliminar informe | COMPLETA |
| INF-003 | Exportar informe guardado (completo/clásico) | COMPLETA |
| INF-004 | Vista solo lectura por reportId | COMPLETA |
| EXP-001 | Exportar por filtros (UI Exportar) | COMPLETA |
| EXP-002 | Exportar por IDs | COMPLETA |
| EXP-003 | PDF completo | COMPLETA |
| EXP-004 | PDF clásico | COMPLETA |
| EXP-005 | CSV | COMPLETA |
| EXP-006 | Excel | COMPLETA |
| EXP-007 | Temp download / share | COMPLETA |
| EXP-008 | Export catálogo telas/lotes | COMPLETA |
| EXP-009 | Selector / API `columns=` | COMPLETA |
| RPB-001 | Constructor de informes profesionales | COMPLETA |

---

### MÓDULO: CATÁLOGOS

| ID | Nombre | Estado |
| -- | ------ | ------ |
| TEL-001 | CRUD telas + import/export | COMPLETA |
| LOT-001 | CRUD lotes + export | COMPLETA |

---

### MÓDULO: ADMINISTRACIÓN

| ID | Nombre | Estado |
| -- | ------ | ------ |
| USR-001 | Crear / listar usuarios | COMPLETA |
| USR-002 | Activar / desactivar | COMPLETA |
| USR-003 | Reset contraseña (admin) | COMPLETA |
| USR-004 | Soft-delete usuario | COMPLETA |
| USR-005 | Cambiar rol | COMPLETA |
| ROL-001 | Editar matriz de permisos | COMPLETA |
| ROL-002 | Roles parametrizables (CRUD + SeesAllRecords) | COMPLETA |
| ROL-003 | Inicializar roles base (SuperAdmin) | COMPLETA |
| ROL-004 | Rol inactivo niega permisos | COMPLETA |
| CFG-001 | Configurar umbrales de alerta | COMPLETA |
| MIG-001 | Importar JSON Firestore | COMPLETA |

---

### MÓDULO: MÓVIL

| ID | Nombre | Estado |
| -- | ------ | ------ |
| MOB-001 | WebView carga servidor Blazor | COMPLETA |
| MOB-002 | Persistencia URL servidor | COMPLETA |
| MOB-003 | Puente share archivo nativo | COMPLETA |
| MOB-004 | Puente share texto (legado) | COMPLETA (legado) |

---

## 8. Reglas de negocio NEPS

### 8.1 Símbolos solicitados vs realidad

| Símbolo pedido | Estado en RegNeps.Net |
| -------------- | --------------------- |
| `classifyNeps()` | **AUSENTE** — equivalente: `AlertEvaluator.GetLevel` |
| `NepsQualityCriteria` | **AUSENTE** |
| `AlertLevel` | Presente — `Domain/Enums/AlertLevel.cs` |
| `displayLabel` / `ToDisplayLabel()` | Presente |
| Categorías OK / Mención / 2da Calidad | **AUSENTES** en este código |

### 8.2 Fórmula metros

```text
MtsCalculados = Neps / TestLengthM
TestLengthM   = 0.09
```

Fuente: `NepsConstants.TestLengthM`, propiedad `NepRecord.MtsCalculados`.

### 8.3 Clasificación (`AlertEvaluator.GetLevel`)

```text
Si AlertasActivas == false → Normal
value = Round(neps, AwayFromZero)   // entero
Si value ≤ LimiteNormalMax      → Normal
Si value ≤ LimiteAdvertenciaMax → Advertencia
En otro caso                    → Crítico
```

**Defaults seed (`AlertConfig`):**

| Parámetro | Default |
| --------- | ------- |
| LimiteNormalMax | 30 |
| LimiteAdvertenciaMax | 60 |
| LimiteCriticoMin (calculado) | 61 |
| CantidadReincidenciasCriticas | 3 |
| DiasParaReincidencia | 1 |
| VentanaReincidenciasHoras | 24 |
| AlertasActivas | true |

### 8.4 Etiquetas visibles

| Enum interno | `ToDisplayLabel()` |
| ------------ | ------------------ |
| `Normal` | Normal |
| `Advertencia` | Advertencia |
| `Critico` | Crítico |

### 8.5 Recomendaciones (`GetRecommendations`)

- **Normal:** monitoreo rutinario.
- **Advertencia:** tensión/alimentación trama; limpieza; notificar si persiste.
- **Crítico:** detener/reducir velocidad; inspeccionar; acción correctiva + supervisor; notificar inmediato.
- **Reincidencia:** añade mensaje de revisión técnica del telar.

### 8.6 Reincidencia crítica (`HasCriticalRecurrence`)

Mismo telar (ignore case), nivel crítico, ventana `now.AddDays(-Math.Max(1, DiasParaReincidencia))`, conteo ≥ `CantidadReincidenciasCriticas`.

**Nota:** `VentanaReincidenciasHoras` (`DiasParaReincidencia * 24`) es **solo informativo en UI** (`Config.razor`). La evaluación real usa **días**, no horas.

### 8.7 Seguimiento (`RequiereSeguimiento`)

Nivel ≠ Normal **y** `RevisadoPorSupervisor == false`.

### 8.8 Constantes adicionales

| Constante | Valor |
| --------- | ----- |
| `WorkspaceId` | `"vicunha"` |
| `LoteTramaPrefix` (default create si lote vacío en backend) | `"63E264"` |

### 8.9 Validaciones de medición

| Capa | Regla |
| ---- | ----- |
| Backend create/update | Telar obligatorio; Neps > 0 |
| UI Captura | Además exige Tela y Lote |
| Neps = 0 o negativo | Rechazado |
| Alertas desactivadas | Todo se clasifica Normal |

### 8.10 Índice de calidad (Analytics)

```text
QualityIndex = 100 - (críticos * 100 / total)
```

---

## 9. Captura (detalle)

**Archivo:** `src/RegNeps.Web/Components/Pages/Captura.razor`  
**Servicio:** `NepRecordService`  
**Permiso base:** `CaptureRecords`

### 9.1 Formulario

| Campo | Obligatorio UI | Obligatorio backend | Notas |
| ----- | -------------- | ------------------- | ----- |
| Telar | Sí | Sí | |
| Neps | Sí (>0) | Sí (>0) | `double?`, decimal |
| Tela | Sí | No | Catálogo o manual |
| Lote | Sí | No (default prefix) | Mayúsculas, max 64 |
| Turno A/B/C | No | No | Panel adicionales |
| Operario / Línea / Observación | No | No | |

### 9.2 KPIs de sesión

Filtrados por `CaptureSessionId` + `RecordQueryScope.PersonalOnly`: cantidad registros, suma neps, suma mts, telares distintos.

### 9.3 Nuevo registro vs Nueva sesión

| Acción | Efecto |
| ------ | ------ |
| **Agregar registro** | Guarda; limpia Telar/Neps/Obs; conserva tela/lote/turno; mismo `CaptureSessionId`; nuevo `ClientOperationId` |
| **+ Nuevo registro** | Confirma si hay pendientes; prepara otra medición en la misma sesión |
| **+ Nueva sesión** | Nuevo `CaptureSessionId`; limpia formulario, tabla e indicadores de sesión; historial global intacto |

### 9.4 Compartir

| Modo | Handler | Qué envía |
| ---- | ------- | --------- |
| Compartir (1) | `ShareRecordAsync` | Exactamente 1 ID → `ExportByIdsAsync` |
| Compartir seleccionados | `ShareSelectedAsync` | Solo IDs en `_selectedRecordIds` (nunca agrupa por sesión/fecha/usuario/telar/lote) |
| Compartir sesión | `ShareSessionExportAsync` | `ExportAsync` con `CaptureSessionId` + rango fechas min/max de `_sessionRecords` |
| Banner post-guardado | `ShareRecordAsync(lastId)` | Mismo flujo de un registro |

Reglas verificadas en `Captura.razor`:

- Estilo de archivo **siempre** `completo` (el PDF/Excel/CSV clásico no se elige desde Captura).
- `viewerSeesAll: false` (aislamiento del actor).
- Entrega: `TempExports.Put` → JS `regnepsDownload.shareOrDownload` → `ShareDeliveryPolicy.Parse`.
- **No** usa `regnepsUi.shareText` ni `RecordShareFormatter` en este flujo.
- Cancelar / clipboard / unsupported / native-requested **no** limpia selección (`clearOnSharedSuccess: false`).
- Tras recargar sesión, la selección se reduce a IDs aún visibles; **no** hay auto-selección al guardar.

### 9.4.1 Guardar informe de sesión

- Permiso: `ManageReports`.
- Modal con nombre (default `Informe {yyyyMMdd_HHmmss}`).
- `ReportExportAppService.SaveReportAsync` con filtros de sesión y `viewerSeesAll: false`.
- Éxito tipificado: «Informe «{name}» guardado. Puede abrirlo en Informes.»

### 9.5 Aislamiento multi-usuario

- Propietario = actor autenticado (`CreatedBy*`).
- Sesión JS por `userId`.
- Listado personal + sesión.
- Export share con `viewerSeesAll: false`.
- Idempotencia por `ClientOperationId`.
- Concurrencia por `ConcurrencyStamp`.

### 9.6 Persistencia

SQLite/SQL Server vía `NepRecordRepository`. Sesión de captura en cliente (JS) + campo `CaptureSessionId` en registro.

### 9.7 Sincronización

Online al servidor Blazor (SignalR circuit). **No hay cola offline** de captura.

---

## 10. Registros (detalle)

**Archivo:** `Registros.razor`

### 10.1 Filtros

Telar, Tela, Lote, Turno (A/B/C), Alerta (Normal/Advertencia/Crítico), Desde, Hasta, Pendientes.

### 10.2 Acciones por fila

| Acción | Permiso |
| ------ | ------- |
| Editar | `EditRecords` |
| Acción correctiva | `ApplyCorrectiveAction` (solo si ≠ Normal) |
| Eliminar | `DeleteRecords` |

### 10.3 Acciones globales

| Acción | Permiso |
| ------ | ------- |
| Plantilla Excel | `EditRecords` |
| Importar `.xlsx/.xls/.csv` | `EditRecords` |
| Vaciar todos | `ClearAllRecords` |
| Actualizar | lectura |
| Selección múltiple / borrado lote | `DeleteRecords` |
| Guardar informe de selección | `ManageReports` |

### 10.4 Selección y solo lectura

- Paginación 50/página; selección en memoria (`RecordSelectionRules`); se limpia al cambiar filtro/página.
- Con `reportId`: banner solo lectura; sin editar/eliminar/importar/vaciar/acción correctiva; «Cerrar informe».
- No exporta PDF/Excel/CSV desde esta pantalla (usar Exportar / Captura / Informes / Constructor).

### 10.5 Visibilidad de datos

`SeesAllRecords` viene del rol en BD (`AppRole.SeesAllRecords`); valor inicial: todos excepto Operario.

---

## 11. Informes guardados (detalle)

**Entidad:** `SavedReport`  
**Servicio:** `ReportExportAppService`  
**UI:** `Informes.razor` (requiere `ManageReports`; si solo `ExportReports` → redirect `/exportar`)

### 11.1 Campos persistidos

Name, CreatedAt, CreatedBy*, FiltersJson, RecordCount, SummaryText, SnapshotJson.

### 11.2 Operaciones

| Operación | Comportamiento |
| --------- | -------------- |
| Crear | Exige ≥1 registro; guarda filtros + snapshot + resumen |
| Actualizar | Puede quedar con 0 vivos |
| Listar / eliminar | Por repositorio |
| Ver datos | Navega a `/registros?...&reportId=` y carga snapshot |
| Exportar | `ExportSavedAsync` — estilo PDF **completo** fijo; usa snapshot o reconsulta filtros |
| Guardar desde Captura/Exportar | Misma API |

### 11.3 Servicios relacionados (nombres reales)

| Pedido Flutter | En RegNeps.Net |
| -------------- | -------------- |
| `ReportShareHelper` | **AUSENTE** |
| `ReportExportService` | Equivalente: `ReportExportAppService` + `ExportFileService` |
| `FileShareHelper` | **AUSENTE** — JS `regnepsDownload` + MAUI Share |

---

## 12. Generación de PDF

**Generador:** QuestPDF en `ExportFileService`.

### 12.1 Estilos

| Estilo | Activación | Contenido |
| ------ | ---------- | --------- |
| Completo (default) | `style=completo` o default | Header VICUNHA, fórmula, filtros, resumen ejecutivo, tabla, alertas críticas, rankings, recomendaciones, firma, pie página |
| Clásico | `clasico` / `classic` | Header VICUNHA + jeansidentity, fórmula, tabla, resumen compacto |

### 12.2 Columnas tabla principal

Nro, Fecha, Lote de trama, Tela, Telar, Neps, Mts calculados, Estado alerta, Observación, Recomendación (`ReportColumnIds`).

### 12.3 Leyenda / clasificación en PDF

Texto de columna «Estado alerta»: **Normal / Advertencia / Crítico**.  
**No** hay bloque de leyenda de colores en PDF. Los umbrales numéricos no se imprimen en el documento.

### 12.4 Metadatos / nombres

Patrones: `reporte_neps[_clasico][_rango]_stamp.pdf`, `regneps_registro_{telar}_stamp.pdf`, `regneps_seleccion_{N}_stamp.pdf`, nombre sanitizado de informe guardado.

### 12.5 Almacenamiento temporal

`TempExportStore` (memoria, 5 min, un solo uso, ligado a `ownerUserId`).

---

## 13. Generación de CSV

- Encoding: UTF-8 **con BOM**.
- Separador: el de `ClosedXML`/builder interno (comas estándar del builder).
- Completo: cabeceras + filas + TOTAL REGISTROS / TOTAL NEPS / PROMEDIO NEPS.
- Clásico: primera línea `Formula utilizada: Mts calculados = Neps / 0.09`.
- Analytics: KPIs + bloques por dimensión.
- Catálogos: columnas Nombre/Codigo/Activo/Creado (telas) o Codigo/Activo/Creado (lotes).

---

## 14. Generación de Excel

- Formato XLSX vía ClosedXML.
- Completo: hojas Registros, Resumen por telar/tela/lote, Alertas críticas, Advertencias, Tendencia diaria; relleno de color por alerta.
- Clásico: una hoja «Informe».
- Analytics: KPIs, Por telar, Por tela, Por día.
- Colores alerta Excel: verde `#C8E6C9`, naranja `#FFE0B2`, rojo `#FFCDD2`.

---

## 15. Sistema de compartir

### 15.1 Componentes reales

| Componente | Rol |
| ---------- | --- |
| `ShareDeliveryPolicy` | Interpreta resultado JS y mensajes; política de limpieza de selección |
| `RecordShareFormatter` | Formatea texto de registros (flujo archivo de Captura **no** lo usa) |
| `regnepsDownload.shareOrDownload` | Web Share API / descarga / puente nativo |
| `regnepsUi.shareText` | Share de texto (legado) |
| MAUI `MainPage` | `regneps-share://file` y `://share` |

### 15.2 Ausentes (Flutter)

`FileShareHelper`, `ReportShareHelper`, `share_plus`, `XFile`, FileProvider Flutter, política `EXTRA_TEXT` tipo Flutter.

### 15.3 Comportamiento con archivos

| Canal | Texto adjunto |
| ----- | ------------- |
| Web `navigator.share({ files, title })` | **Sin** `text` |
| MAUI `ShareFileRequest` | Solo Title + File — **sin** Text |
| Share texto legado | Sí envía texto |

Equivalente funcional a «no EXTRA_TEXT con archivos»: al compartir PDF/Excel/CSV desde Captura/MAUI **no** se adjunta texto complementario.

### 15.4 Resultados (`ShareDeliveryResult`)

| Resultado | Token JS | Mensaje usuario | ¿Error? | ¿Limpia selección en Captura? |
| --------- | -------- | --------------- | ------- | ----------------------------- |
| Shared | `shared` | «Archivo compartido.» | No | No (`clearOnSharedSuccess=false`) |
| NativeRequested | `native-requested` | «Se abrió el menú para compartir.» | No | No |
| Downloaded | `downloaded` | «El archivo se descargó.» | No | No |
| Copied | `copied` | Portapapeles (fallback) | No | No |
| Cancelled | `cancelled` | `null` (sin toast) | No | No |
| Unsupported | `unsupported` | Menú no disponible | Sí | No |
| Failed | `failed` / desconocido | «No se pudo compartir el archivo.» | Sí | No |

### 15.5 MIME / temp

| Pieza | Detalle |
| ----- | ------- |
| `TempExportStore` | Singleton en memoria; TTL 5 min; `Put`/`TryTake` de un solo uso; exige `ownerUserId` |
| Descarga | `GET /api/export/temp/{id}` con cookie; permisos Export/ManageReports/ViewDashboard/Capture/ViewRecords + ownership |
| MIME | `text/csv`, OpenXML xlsx, `application/pdf` según formato |
| Límite export filtros | Hasta **50 000** registros (`ReportExportAppService.ExportAsync`) |
| Export por IDs | Sin expansión por sesión/fecha; orden de la solicitud; nombres `regneps_registro_*` / `regneps_seleccion_*` |

---

## 16. Dashboard, alertas y analítica

### 16.1 Dashboard — categorías

Donut y badges: **Normal · Advertencia · Crítico** (no cuatro categorías Flutter).

### 16.2 Alertas

Origen: `GetAlertsAsync` / evaluación `AlertEvaluator` sobre registros.  
Sin tarjetas KPI propias. Interacción: acción correctiva.

### 16.3 Analítica

Servicio: `AnalyticsService`.  
Métricas: totales, promedio, min/max, mts, conteos por nivel, quality index, series, rankings top 5.  
Export: `/api/export/analytics/{format}`.

---

## 17. Configuración

Pantalla `/config` — entidad `AlertConfig`.

| Opción | Default | Editable por | Efecto |
| ------ | ------- | ------------ | ------ |
| Límite normal máx. | 30 | `EditAlertConfig` | Umbral Normal |
| Límite advertencia máx. | 60 | idem | Umbral Advertencia |
| Reincidencias críticas | 3 | idem | Trigger reincidencia |
| Días para reincidencia | 1 | idem | Ventana |
| Alertas activas | true | idem | Si false → todo Normal |
| Crítico desde / ventana horas | calculados | solo lectura UI | Informativos |

Validación: `advertencia > normal ≥ 0`.  
`ManageSettings` (enum 16) está **reservado** y **no** aparece en catálogo UI.

Otras configuraciones:

| Config | Quién | Persistencia |
| ------ | ----- | ------------ |
| URL servidor (MAUI) | Usuario del dispositivo | Preferences nativas |
| Connection string / UseSqlServer | Administrador de despliegue | appsettings (no UI) |
| Matriz roles | SuperAdmin | BD |

---

## 18. Autenticación

| Tema | Implementación |
| ---- | -------------- |
| Login / logout | Cookie + endpoints API |
| Persistencia sesión | Cookie 12 h |
| Hash | BCrypt |
| Identity / Firebase Auth | No en runtime |
| Recuperación self-service | No implementada |
| Reset admin | Usuarios |
| Revalidación | `RegNepsRevalidatingAuthStateProvider` |

Seed (solo si no hay SuperAdmin activo): usuario `admin`, password `Admin123!` (`DbSeeder`).

---

## 19. Usuarios, roles y permisos

### 19.1 Roles (`AppUserRole`)

Operario · Supervisor · Admin (etiqueta «Administrador») · Gerencia · SuperAdmin («Super administrador»).

### 19.2 Permisos del catálogo (`PermissionCatalog`)

ViewDashboard, CaptureRecords, ViewRecords, EditRecords, DeleteRecords*, ClearAllRecords*, ViewAlerts, ApplyCorrectiveAction, ManageFabrics, ManageReports, ExportReports, EditAlertConfig, ManageUsers*, DeleteUsers*, ChangeRoles*, ManageRoles* (solo SuperAdmin), ViewSettings.  
(\* marcados sensibles en UI de Roles.)

### 19.3 Matriz por defecto (`RolePermissions.Matrix`) — solo seed / respaldo

La autorización en runtime lee la matriz **persistida** en BD (`IPermissionService`). Esta tabla es el seed inicial (`DbSeeder.EnsureDefaultRolePermissionsAsync`) y no pisa cambios ya guardados.

| Permiso | SuperAdmin | Admin | Supervisor | Operario | Gerencia |
| ------- | ---------- | ----- | ---------- | -------- | -------- |
| ViewDashboard | Sí | Sí | Sí | No | Sí |
| CaptureRecords | Sí | Sí | No | Sí | No |
| ViewRecords | Sí | Sí | Sí | Sí | Sí |
| EditRecords | Sí | Sí | Sí | No | No |
| DeleteRecords | Sí | Sí | No | No | No |
| ClearAllRecords | Sí | Sí | No | No | No |
| ViewAlerts | Sí | Sí | Sí | No | Sí |
| ApplyCorrectiveAction | Sí | Sí | Sí | No | No |
| ManageFabrics | Sí | Sí | No | No | No |
| ManageReports | Sí | Sí | Sí | No | Sí |
| ExportReports | Sí | Sí | Sí | No | Sí |
| EditAlertConfig | Sí | Sí | No | No | No |
| ManageUsers | Sí | No | No | No | No |
| DeleteUsers | Sí | No | No | No | No |
| ChangeRoles | Sí | No | No | No | No |
| ManageRoles | Sí | No | No | No | No |
| ViewSettings | Sí | Sí | No | No | No |
| ManageSettings (enum 16) | — | — | — | — | — (no está en la matriz ni en el catálogo UI) |

### 19.4 Reglas sensibles de usuarios

No crear/promover SuperAdmin desde UI de creación; no desactivar/eliminar último SuperAdmin; no auto-desactivar/eliminar; contraseña ≥8 con letras y números en backend (`UserAdminService`). La UI de reset muestra «mín. 6» (`Usuarios.razor`) — **inconsistencia UI vs backend**.

---

## 20. Firebase / Firestore

### 20.1 Runtime aplicación

**AUSENTE.** No hay SDK Firebase en proyectos `src/*`.

### 20.2 Migración / legado

| Pieza | Uso |
| ----- | --- |
| `scripts/export_firestore_history.js` | Export histórico con `firebase-admin` |
| `FTS/firestore_export*.json` | Datos de import |
| `HistoricalDataMigrationService` | Import a SQL |
| `/migracion` + `tools/RegNeps.Migrate` | UI / CLI |
| `AppUser.ExternalUserId` | UID Firebase histórico |
| `docs/MIGRACION_FIRESTORE.md` | Documentación |

### 20.3 Secretos

Si existe `secrets/serviceAccountKey.json` u otras credenciales: **SECRETO DETECTADO — NO MOSTRAR VALOR**. No se documentan contenidos.

### 20.4 Reglas / índices Firestore

No hay `firestore.rules` ni `firestore.indexes.json` en este repo (pertenecen al proyecto Firebase externo usado solo para export).

---

## 21. Modelos de datos

| Modelo | Archivo | Campos principales | Uso | Persistencia |
| ------ | ------- | ------------------ | --- | ------------ |
| `NepRecord` | `Domain/Entities/NepRecord.cs` | Telar, Neps, Tela, LoteTrama, Turno, Operario, Linea, Obs, auditoría, correctiva, ClientOperationId, CaptureSessionId, ConcurrencyStamp | Mediciones | BD |
| `CorrectiveActionEntry` | mismo dominio | Historial acciones | Embebido en registro | BD |
| `AppUser` | `AppUser.cs` | Username, hash, role, flags, ExternalUserId, soft-delete | Auth | BD |
| `Fabric` | `Fabric.cs` | Nombre, Código, Activo | Catálogo telas | BD |
| `LoteTramaItem` | `SavedReport.cs` (mismo archivo) | Código, Activo | Catálogo lotes | BD |
| `AlertConfig` | `AlertConfig.cs` | Umbrales | Config | BD |
| `SavedReport` | `SavedReport.cs` | Filtros, snapshot, resumen | Informes | BD |
| `RolePermission` | `RolePermission.cs` | Role + Permission + Allowed | Matriz | BD |
| `RolePermissionAudit` | `RolePermissionAudit.cs` | Auditoría cambios | Seguridad | BD |

`MtsCalculados`, `LimiteCriticoMin`, `VentanaReincidenciasHoras` son calculados / ignorados en EF.

---

## 22. Servicios

| Servicio | Archivo | Responsabilidad | Dependencias | Uso |
| -------- | ------- | --------------- | ------------ | --- |
| `NepRecordService` | Application/Records | CRUD mediciones, alertas, share IDs, permisos | Repos, AlertEvaluator | Captura, Registros, Alertas |
| `AuthService` | Application/Auth | Login BCrypt | IUserRepository | API login |
| `UserAdminService` | Application/Auth | Admin usuarios | IUserRepository, permisos | Usuarios |
| `AnalyticsService` | Application/Analytics | KPIs y series | Repos, AlertConfig | Dashboard, Gráficas |
| `ReportExportAppService` | Application/Reports | Export filtros/IDs/saved | IExportFileService, repos | Export/Informes/Captura |
| `ExportFileService` | Infrastructure/Export | PDF/Excel/CSV bytes | QuestPDF, ClosedXML | Export |
| `PermissionService` / `PermissionMatrix` | Application/Permissions | Matriz runtime | RolePermission repo | Toda la app |
| `HistoricalDataMigrationService` | Infrastructure/Migration | Import Firestore JSON + snapshots | DbContext | Migración |
| `FabricImportService` / `RecordImportService` | Infrastructure/Import | Import Excel | ClosedXML | Telas/Registros |
| `TempExportStore` | Web/Export | Temp files | Memoria | Share/download |
| `CurrentUserService` | Web/Auth | Actor actual | Auth state | UI |

---

## 23. Repositorios y persistencia

| Repositorio | Entidad |
| ----------- | ------- |
| `NepRecordRepository` | NepRecord |
| `AlertConfigRepository` | AlertConfig |
| `FabricRepository` | Fabric |
| `LoteTramaRepository` | LoteTramaItem |
| `UserRepository` | AppUser |
| `SavedReportRepository` | SavedReport |
| `RolePermissionRepository` | RolePermission + Audit |

**Inicialización:** `DatabaseInitializer.InitializeAsync`:

1. `EnsureCreatedAsync`
2. `ApplySchemaPatchesAsync` (idempotente SQLite y SQL Server)
3. `DbSeeder.SeedAsync`
4. `RepairRecordOwnershipAsync`

**Parches aditivos verificados:**

| Elemento | Acción |
| -------- | ------ |
| `Users.ExternalUserId` | Columna |
| `SavedReports.SnapshotJson` | Columna |
| `NepRecords.ConcurrencyStamp` | Columna + backfill |
| `NepRecords.ClientOperationId` | Columna |
| `IX_NepRecords_CreatedBy_ClientOperation` | Índice único filtrado (idempotencia) |
| `NepRecords.CaptureSessionId` | Columna |
| `IX_NepRecords_CreatedBy_CaptureSession` | Índice |
| `IX_Fabrics_Name_Unique` | Índice único por nombre |
| `RolePermissions` | Crear tabla si falta |
| `RolePermissionAudits` | Crear tabla + índice `ChangedAt` |

**Proveedores:** SQLite (dev) / SQL Server (`Database:UseSqlServer`).

---

## 24. Sincronización y offline

| Tema | Estado real |
| ---- | ----------- |
| Sync Firebase | NO — no hay runtime Firebase |
| Offline captura | NO — requiere circuito Blazor / red al servidor |
| Multi-usuario concurrente | SÍ — aislamiento por usuario + stamps + ClientOperationId |
| Indicadores online/offline | NO DETERMINADO como feature dedicada (Blazor muestra error de circuito) |
| MAUI | Depende de alcanzar la URL del servidor configurada |

---

## 25. Manejo de errores

| Tipo | Situación | Mensaje / manejo |
| ---- | --------- | ---------------- |
| Auth | Credenciales | «Usuario o contraseña incorrectos…» / API: inválidas o inactivas |
| Permiso UI | Sin permiso pantalla | Alert warning específico por módulo |
| Permiso servicio | Operación sensible | Unauthorized / mensaje «No tiene permiso…» |
| Validación captura | Telar/Neps/Tela/Lote | Mensajes de campo / ValidationFailed |
| Concurrencia | Stamp distinto | `RecordConcurrencyConflictException` + pedir recarga |
| Idempotencia | Mismo ClientOperationId | AlreadySaved |
| Share | Cancel/Fail/Unsupported | Mensajes `ShareDeliveryPolicy` |
| Export temp | ID ajeno / expirado | 404 / no entrega |
| Import | Archivo inválido / >5 MB | Mensajes en Telas/Registros |
| Layout | Error Blazor | «Se produjo un error inesperado.» |
| Config | Límites incoherentes | «advertencia > normal ≥ 0» |

---

## 26. Permisos de plataforma

| Plataforma | Permisos observados en repo |
| ---------- | --------------------------- |
| Android (MAUI) | Los del manifiesto MAUI/WebView + uso de Share; no hay cámara/ubicación en código de negocio revisado |
| Web | Descarga/share del navegador; cookies |
| iOS | NO DISPONIBLE |

Solo documentar lo usado: red HTTP al servidor, almacenamiento temporal de archivo para Share, Preferences URL.

---

## 27. Notificaciones

- **Push FCM / notificaciones sistema:** AUSENTE en runtime .NET.
- **Alertas de calidad (pantalla `/alertas`):** listado de mediciones ≠ Normal + acciones correctivas; no push remoto.

### 27.1 NotificationCenter (UI in-app)

**Archivo:** `Components/Shared/NotificationCenter.razor` (topbar del `MainLayout`).

| Aspecto | Comportamiento verificado |
| ------- | ------------------------- |
| Acceso | Visible si `ViewAlerts` |
| Origen de datos | `NepRecordService.GetAlertsAsync` + `AlertConfig` (scope Default, hasta 1000) |
| Criterio | `GetAlertLevel(config) ≠ Normal` |
| Título por ítem | Operario, o «Telar {Telar}» |
| Mensaje | `{nivel}: {neps} neps · {tela}` + «Pendiente revisión» si `RequiereSeguimiento` |
| Leído/no leído | IDs vistos en cliente vía JS `regnepsUi.getNotificationSeenIds` / `setNotificationSeenIds` |
| Acciones | Marcar leída, marcar todas, preview, drawer «Todas las alertas», enlace a `/alertas` |
| Persistencia remota de «leídas» | No — solo almacenamiento local del navegador/WebView |

---

## 28. Tests

| Test | Archivo | Funcionalidad | Tipo | Estado |
| ---- | ------- | ------------- | ---- | ------ |
| Umbrales / mts / RolePermissions smoke | `BusinessRulesTests.cs` | NEPS + permisos estáticos | unit | Presente |
| Filtros registros | `RecordFilterTests.cs` | REG-001 | unit | Presente |
| Aislamiento captura | `CaptureIsolationTests.cs` | CAP / multi-user | unit/integration-like | Presente |
| Share por IDs | `RecordShareIsolationTests.cs` | CAP-009/010 | unit | Presente |
| ShareDeliveryPolicy | `ShareDeliveryPolicyTests.cs` | Compartir | unit | Presente |
| ExportByIds | `ExportByIdsTests.cs` | EXP-002 | unit | Presente |
| RolePermissionService | `RolePermissionServiceTests.cs` | ROL-001 | unit | Presente |
| DbSeeder + create | `DbSeederAndCaptureTests.cs` | Seed / CAP-001 | unit | Presente |
| Concurrencia BD | `DatabaseConcurrencyTests.cs` | Persistencia | unit | Presente |
| Orden telares | `TelarSortTests.cs` | Utilidad | unit | Presente |

**Sin cobertura aparente (UI E2E / widget):** pantallas Blazor completas, MAUI, PDF visual pixel-perfect, Migración UI, Login E2E, Gráficas charts JS.

---

## 29. CI/CD

### 29.1 `.NET CI` — `dotnet-ci.yml`

- Trigger: PR/push a `main`.
- .NET 8 → restore / build / test `RegNeps.sln` (Mobile fuera del job típico de test).
- Job `build-and-test` — check requerido para PRs a main.

### 29.2 `Android APK QA` — `android-apk.yml`

- `workflow_dispatch` o push a rama/path específicos.
- Windows + .NET 10 + workload maui-android.
- APK Debug con `-p:EmbedAssembliesIntoApk=true`.
- Artifact `RegNeps-Android-QA`.
- **No** es status check obligatorio general.

### 29.3 Flujo push → producción

No hay deploy automático a hosting/producción en los workflows revisados. Despliegue intranet es manual (`docs/DESPLIEGUE_INTRANET.md`).

---

## 30. Deployment

### Web / Intranet

- `dotnet publish src/RegNeps.Web -c Release`
- Kestrel `http://0.0.0.0:5080` o IIS
- SQLite o SQL Server según `Database:UseSqlServer`
- Ver `docs/DESPLIEGUE_INTRANET.md`

### Android

- Script `scripts/build_android_apk.ps1` con EmbedAssemblies
- Documentación `docs/ANDROID_APK.md` (nota: doc menciona net8; csproj actual `net10.0-android`)
- WebView apunta a URL del servidor

### Backend Functions

- **AUSENTE** (no Cloud Functions en este repo)

---

## 31. Versiones y dependencias

### 31.1 Proyectos

| Proyecto | TFM |
| -------- | --- |
| Domain / Application / Infrastructure / Web / Tests | net8.0 |
| Mobile | net10.0-android |
| Migrate tool | net8.0 (CLI) |

### 31.2 Dependencias producción relevantes (Infrastructure / Web)

| Dependencia | Versión observada | Uso |
| ----------- | ----------------- | --- |
| EF Core Sqlite / SqlServer | 8.0.17 | Persistencia |
| ClosedXML | 0.104.2 | Excel/CSV |
| QuestPDF | 2024.10.3 | PDF |
| BCrypt.Net-Next | 4.0.3 (Application + Infrastructure) | Passwords |
| Microsoft.Maui.Controls | 10.0.101 (`MauiVersion`) | Shell Android |
| Microsoft.Extensions.Logging.Debug | 10.0.0 | Logging Mobile (Debug) |

### 31.3 Scripts Node (migración)

`scripts/package.json` — `firebase-admin` para export (herramienta, no runtime app).

---

## 32. Funcionalidades legacy

| Elemento | Visible usuario | Interno/legacy | Notas |
| -------- | --------------- | -------------- | ----- |
| `/reportes` | Redirect | Alias de Exportar | |
| `regneps-share://share` texto | Menos usado | Legado | Captura usa archivos |
| `RecordShareFormatter` / shareText | No en flujo archivo Captura | Implementado | Tests de texto por IDs |
| `AppUser.ExternalUserId` | No | Migración Firebase | |
| `ManageSettings` enum 16 | No en UI | Reservado | |
| `CreatedBy*` en request create | No | `[Obsolete]` | Actor server-side |
| Nombres Flutter OK/Mención/2da Calidad | No en este repo | N/A | Solo en inventario Flutter raíz |
| Internos Normal/Advertencia/Critico | Sí (labels) | Enum Critico sin acento | Display «Crítico» |
| README «Flutter original en raíz» | Doc | Parcialmente desactualizado respecto a Mobile ya existente | |

---

## 33. Flujos end-to-end

### Flujo 1 — Login → Dashboard

Login → cookie → `/` → `/dashboard` (si `ViewDashboard`).

### Flujo 2 — Captura → cálculo → clasificación → guardado

Formulario → validación UI → EnsureActive tela/lote → `CreateWithOutcomeAsync` → Mts=Neps/0.09 → `GetLevel` → mensaje con label → tabla sesión.

### Flujo 3 — Registro → filtro → edición

`/registros` → filtros → Editar → Update con concurrency stamp.

### Flujo 4 — Captura → compartir archivo

Seleccionar formato → `ExportByIdsAsync` → TempStore → shareOrDownload / MAUI Share → `ShareDeliveryPolicy` mensaje (sin limpiar selección si no Shared con flag).

### Flujo 5 — Informe → guardar → consultar

Captura/Exportar/Informes → `SaveReportAsync` → listado Informes → Ver datos (snapshot) / Exportar.

### Flujo 6 — PDF completo

Exportar o Captura con style completo → `BuildPdf`.

### Flujo 7 — PDF clásico

Exportar style clásico → `BuildClassicPdf`.

### Flujo 8 — CSV

Export filtros/IDs/analytics/catálogo → `BuildCsv*`.

### Flujo 9 — Excel

Idem → `BuildExcel*`.

### Flujo 10 — Informe guardado → export

`GET /api/export/saved/{id}/{format}` o UI Informes (estilo completo).

### Flujo 11 — «Compartir clásico» desde informe guardado

**NO DETERMINADO / NO EVIDENCIADO:** export saved fuerza estilo completo. El clásico se elige en UI Exportar/Captura, no como par «compartir clásico» de informe guardado tipo Flutter.

### Flujo 12 — Datos → Dashboard KPIs

Registros en BD → Analytics periodo → KPIs/charts.

### Flujo 13 — Datos → Alertas

Evaluación nivel ≠ Normal → lista + correctiva.

### Flujo 14 — Analítica → exportación

Gráficas filtros → `/api/export/analytics/...` o PDF con charts vía temp.

### Flujos adicionales

- **Migración:** JSON → `/migracion` o CLI → SQL.
- **Import registros:** plantilla → Excel → Registros.
- **Roles:** SuperAdmin edita matriz → `PermissionRefresh` en sesiones abiertas.
- **Android:** APK → URL servidor → mismos flujos Web + share nativo.

---

## 34. Casos límite

| Caso | Comportamiento observado |
| ---- | ------------------------ |
| Neps ≤ 0 | Rechazado |
| Alertas desactivadas | Todo Normal |
| Redondeo | AwayFromZero antes de umbral |
| Lote vacío backend | Prefijo `63E264` |
| ClientOperationId repetido | AlreadySaved / idempotente |
| Concurrencia edit | Conflicto por stamp |
| Temp export expirado / ajeno | No entrega |
| Informe sin filas vivas | Snapshot/reconsulta; UI avisos si BD vacía |
| Filtros sin resultados | EmptyState + limpiar filtros |
| Operario vs SeesAll | Solo propios |
| Último SuperAdmin | No desactivar/eliminar |
| Import >5 MB (telas) | Error |
| Doble clic guardar | ClientOperationId / flags UI |
| Pérdida de red Blazor | Error de circuito (sin cola offline) |

---

## 35. Funcionalidades ocultas o poco visibles

- CircleMenu flotante (atajos).
- Panel «Datos adicionales» en Captura (turno/operario/línea/obs).
- Modo tela/lote Manual en Captura.
- `/reportes` alias.
- Hint seed admin solo en Development.
- `ManageSettings` reservado invisible.
- Share texto legado MAUI.
- Endpoints API no listados en menú (`/api/export/*`, `/api/import/template`, `/api/migration/import`).
- Repair ownership post-migración automático en startup.
- Query `reportId` para abrir snapshot en Registros.
- Preferencia URL servidor en MAUI (fuera del menú Blazor).

---

## 36. Diferencias por plataforma

| Funcionalidad | Web | Android MAUI | iOS | Windows app | macOS | Linux app |
| ------------- | --- | ------------ | --- | ----------- | ----- | --------- |
| UI Blazor | PASS | PASS (WebView) | NO DISPONIBLE | NO DISPONIBLE | NO DISPONIBLE | NO DISPONIBLE |
| Captura / Registros / etc. | PASS | PASS (servidor) | NO DISPONIBLE | NO DISPONIBLE | NO DISPONIBLE | NO DISPONIBLE |
| Share archivos | PASS (Web Share / download) | PASS (Share nativo) | NO DISPONIBLE | NO DETERMINADO | NO DETERMINADO | NO DETERMINADO |
| Host servidor Kestrel | PASS | N/A (cliente) | N/A | PASS (host) | PARCIAL* | PASS (host) |
| Offline negocio | NO DISPONIBLE | NO DISPONIBLE | — | — | — | — |
| Firebase runtime | NO DISPONIBLE | NO DISPONIBLE | — | — | — | — |

\*El servidor .NET puede hospedarse en varios SO; no hay cliente de escritorio nativo en el repo.

---

## 37. Seguridad funcional

- Autenticación cookie + BCrypt.
- Autorización por permiso en UI **y** servicios.
- SuperAdmin exclusivo para Roles y Migración.
- TempExport anti-IDOR por owner.
- Soft-delete usuarios; protecciones último SuperAdmin.
- Contraseñas no se loguean; no hay Identity tokens OAuth en app.
- Firestore rules: N/A en runtime.
- Secretos de service account: fuera del código versionable idealmente; si están en `secrets/`: no exponer.
- Aislamiento Captura por usuario/sesión/operation id.

---

## 38. Glosario

| Término | Definición en este sistema |
| ------- | -------------------------- |
| NEPS | Defectos/neps medidos en la muestra |
| NEPS/m² / Mts calculados | `Neps / 0.09` (metros calculados según constante de ensayo) |
| Clasificación / Estado alerta | Normal, Advertencia o Crítico según umbrales |
| Puntaje | No hay puntaje numérico de calidad aparte del índice analytics |
| Registro | Fila `NepRecord` |
| Captura | Pantalla/sesión de alta de mediciones |
| Sesión de captura | `CaptureSessionId` que agrupa mediciones de una corrida de UI |
| Informe | `SavedReport` con filtros + snapshot |
| Completo / Clásico | Estilos de exportación PDF/Excel/CSV |
| Alerta | Medición no Normal y/o pendiente de revisión |
| Operario (rol) | Rol con captura y ver propios registros |
| Vicunha | Workspace / marca en encabezados de reporte |

---

## 39. Matriz maestra de funcionalidades

| ID | Módulo | Funcionalidad | Subfuncionalidad | Pantalla | Servicio | Persistencia | Plataforma | Test | Estado |
| -- | ------ | ------------- | ---------------- | -------- | -------- | ------------ | ---------- | ---- | ------ |
| AUTH-001 | Auth | Login | Cookie BCrypt | Login | AuthService | Cookie+BD | Web/Android | Parcial | COMPLETA |
| AUTH-002 | Auth | Logout | API logout | Nav | Program | Cookie | Web/Android | — | COMPLETA |
| AUTH-003 | Auth | Revalidar permisos | PermissionRefresh | Layout | PermissionService | BD | Web/Android | RolePermission* | COMPLETA |
| AUTH-004 | Auth | Recuperación self-service | — | — | — | — | — | — | DOCUMENTADA PERO NO IMPLEMENTADA |
| DASH-001 | Dashboard | Panel KPIs | Periodo 7/30/90 | Dashboard | AnalyticsService | BD | Web/Android | — | COMPLETA |
| CAP-001 | Captura | Registrar medición | Validar+guardar | Captura | NepRecordService | BD | Web/Android | Capture*/DbSeeder* | COMPLETA |
| CAP-002 | Captura | Preview Mts | Neps/0.09 | Captura | — | Memoria | Web/Android | BusinessRules* | COMPLETA |
| CAP-003 | Captura | Clasificar | GetLevel | Captura | AlertEvaluator | — | Web/Android | BusinessRules* | COMPLETA |
| CAP-004 | Captura | Nuevo registro | Misma sesión | Captura | — | Sesión JS | Web/Android | Capture* | COMPLETA |
| CAP-005 | Captura | Nueva sesión | Nuevo CaptureSessionId | Captura | — | JS+BD | Web/Android | Capture* | COMPLETA |
| CAP-006 | Captura | Editar | Concurrency | Captura | NepRecordService | BD | Web/Android | Capture* | COMPLETA |
| CAP-007 | Captura | Eliminar | Delete | Captura | NepRecordService | BD | Web/Android | — | COMPLETA |
| CAP-008 | Captura | Selección múltiple | Checkboxes | Captura | — | Memoria UI | Web/Android | Share* | COMPLETA |
| CAP-009 | Captura | Compartir 1 | ExportByIds | Captura | ReportExportAppService | Temp | Web/Android | ExportByIds*/Share* | COMPLETA |
| CAP-010 | Captura | Compartir seleccionados | Solo IDs | Captura | ReportExportAppService | Temp | Web/Android | RecordShare* | COMPLETA |
| CAP-011 | Captura | Compartir sesión | Filtro sesión | Captura | ReportExportAppService | Temp | Web/Android | — | COMPLETA |
| CAP-012 | Captura | Guardar informe sesión | SaveReport | Captura | ReportExportAppService | BD | Web/Android | — | COMPLETA |
| REG-001 | Registros | Filtrar listar | Filtros | Registros | NepRecordService | BD | Web/Android | RecordFilter* | COMPLETA |
| REG-002 | Registros | Editar | Modal | Registros | NepRecordService | BD | Web/Android | — | COMPLETA |
| REG-003 | Registros | Eliminar | Delete | Registros | NepRecordService | BD | Web/Android | — | COMPLETA |
| REG-004 | Registros | Correctiva | Modal | Registros | NepRecordService | BD | Web/Android | — | COMPLETA |
| REG-005 | Registros | Import Excel | InputFile | Registros | RecordImportService | BD | Web/Android | — | COMPLETA |
| REG-006 | Registros | Plantilla | API | Registros | ExportFileService | — | Web/Android | — | COMPLETA |
| REG-007 | Registros | Vaciar todos | ClearAll | Registros | NepRecordService | BD | Web/Android | — | COMPLETA |
| REG-008 | Registros | Snapshot informe | reportId | Registros | IReportSnapshotService | BD | Web/Android | — | COMPLETA |
| ALT-001 | Alertas | Listar | ≠ Normal | Alertas | NepRecordService | BD | Web/Android | — | COMPLETA |
| ALT-002 | Alertas | Correctiva | Apply | Alertas | NepRecordService | BD | Web/Android | — | COMPLETA |
| ALT-003 | Alertas | Reincidencia | HasCriticalRecurrence | Eval | AlertEvaluator | — | Web/Android | BusinessRules* | COMPLETA |
| ANA-001 | Analítica | KPIs/series | Filtros | Gráficas | AnalyticsService | BD | Web/Android | — | COMPLETA |
| ANA-002 | Analítica | Export | CSV/XLSX/PDF | Gráficas | Export* | Temp/file | Web/Android | — | COMPLETA |
| ANA-003 | Analítica | PNG charts | JS | Gráficas | JS | Temp | Web/Android | — | COMPLETA |
| INF-001 | Informes | Guardar | Snapshot | Informes | ReportExportAppService | BD | Web/Android | — | COMPLETA |
| INF-002 | Informes | CRUD lista | Edit/Delete | Informes | ReportExportAppService | BD | Web/Android | — | COMPLETA |
| INF-003 | Informes | Export saved | API | Informes | Export* | File | Web/Android | — | COMPLETA |
| EXP-001 | Export | Por filtros | UI | Exportar | ReportExportAppService | Temp | Web/Android | — | COMPLETA |
| EXP-002 | Export | Por IDs | Captura | ReportExportAppService | Temp | Web/Android | ExportByIds* | COMPLETA |
| EXP-003 | Export | PDF completo | QuestPDF | Export* | ExportFileService | Temp | Web/Android | ExportByIds* | COMPLETA |
| EXP-004 | Export | PDF clásico | QuestPDF | Export* | ExportFileService | Temp | Web/Android | — | COMPLETA |
| EXP-005 | Export | CSV | ClosedXML | Export* | ExportFileService | Temp | Web/Android | ExportByIds* | COMPLETA |
| EXP-006 | Export | Excel | ClosedXML | Export* | ExportFileService | Temp | Web/Android | ExportByIds* | COMPLETA |
| EXP-007 | Export | Temp share | Store | API | TempExportStore | Memoria | Web/Android | Share* | COMPLETA |
| EXP-008 | Export | Catálogos | API | Telas/Lotes | ExportFileService | File | Web/Android | — | COMPLETA |
| TEL-001 | Catálogo | Telas | CRUD+import | Telas | Fabric* | BD | Web/Android | Concurrency* | COMPLETA |
| LOT-001 | Catálogo | Lotes | CRUD | Lotes | Lote* | BD | Web/Android | Concurrency* | COMPLETA |
| USR-001 | Admin | Usuarios CRUD | Create/list | Usuarios | UserAdminService | BD | Web/Android | — | COMPLETA |
| USR-002 | Admin | Activar/desactivar | Toggle | Usuarios | UserAdminService | BD | Web/Android | — | COMPLETA |
| USR-003 | Admin | Reset password | Admin | Usuarios | UserAdminService | BD | Web/Android | — | COMPLETA |
| USR-004 | Admin | Soft-delete | Delete | Usuarios | UserAdminService | BD | Web/Android | — | COMPLETA |
| USR-005 | Admin | Cambiar rol | ChangeRoles | Usuarios | UserAdminService | BD | Web/Android | — | COMPLETA |
| ROL-001 | Admin | Matriz permisos | Switches | Roles | PermissionService | BD | Web/Android | RolePermission* | COMPLETA |
| CFG-001 | Config | Umbrales | Form | Config | AlertConfig repo | BD | Web/Android | BusinessRules* | COMPLETA |
| MIG-001 | Migración | Import Firestore JSON | UI/CLI | Migracion | HistoricalDataMigrationService | BD | Web/Server | — | COMPLETA |
| MOB-001 | Mobile | WebView | Carga URL | MAUI | MainPage | — | Android | — | COMPLETA |
| MOB-002 | Mobile | URL persistida | Preferences | MAUI | Preferences | Device | Android | — | COMPLETA |
| MOB-003 | Mobile | Share archivo | Bridge | MAUI | Share.Default | Temp file | Android | — | COMPLETA |
| MOB-004 | Mobile | Share texto | Legado | MAUI | ShareTextRequest | — | Android | — | LEGACY |
| SYS-001 | Sistema | Seed admin/telas | Startup | — | DbSeeder | BD | Server | DbSeeder* | COMPLETA |
| SYS-002 | Sistema | Parches esquema | Startup | — | DatabaseInitializer | BD | Server | — | COMPLETA |

**Subfuncionalidades contadas (aprox.):** ~95 (desglose Captura/Export/Admin en §7–15).

---

## 40. Matriz de cobertura

| Funcionalidad | Implementada | Test | Web | Android | Firebase runtime | Exportación | Compartir |
| ------------- | ------------ | ---- | --- | ------- | ---------------- | ----------- | --------- |
| Login | Sí | Parcial | Sí | Sí | No | — | — |
| Captura | Sí | Sí | Sí | Sí | No | Sí (share) | Sí |
| Registros | Sí | Filtros sí | Sí | Sí | No | Import sí / export no en UI | No |
| Alertas | Sí | Parcial | Sí | Sí | No | — | — |
| Dashboard | Sí | No UI | Sí | Sí | No | — | — |
| Gráficas | Sí | No UI | Sí | Sí | No | Sí | Parcial |
| Informes | Sí | No UI | Sí | Sí | No | Sí | Vía export |
| PDF completo/clásico | Sí | ByIds parcial | Sí | Sí | No | Sí | Sí |
| CSV/Excel | Sí | ByIds | Sí | Sí | No | Sí | Sí |
| Roles/permisos | Sí | Sí | Sí | Sí | No | — | — |
| Migración Firestore | Sí (herramienta) | No UI | Sí | N/A | Solo export Node | — | — |
| Offline | No | — | No | No | — | — | — |
| OK/Mención/2da Calidad | No | — | — | — | — | — | — |

---

## 41. Matriz de dependencias

| Funcionalidad | Servicios | Modelos | Repositorios | Almacenamiento | Paquetes |
| ------------- | --------- | ------- | ------------ | -------------- | -------- |
| Captura | NepRecordService, Permission*, Export* | NepRecord, Fabric, Lote, AlertConfig | Nep*, Fabric*, Lote*, Alert* | SQL/SQLite + JS session | EF, Blazor |
| Clasificación | AlertEvaluator | AlertConfig | AlertConfigRepository | BD config | — |
| Export PDF | ReportExportAppService, ExportFileService, TempExportStore | NepRecord, SavedReport | Nep*, Saved* | Temp memoria | QuestPDF |
| Export Excel/CSV | idem | idem | idem | Temp | ClosedXML |
| Share Android | MAUI MainPage + API temp | — | — | Cache device | MAUI Share |
| Auth | AuthService | AppUser | UserRepository | Cookie+BD | BCrypt |
| Analytics | AnalyticsService | NepRecord | Nep* | BD | — |
| Migración | HistoricalDataMigrationService | Firestore DTOs → entidades | DbContext | JSON→SQL | (Node firebase-admin en script) |

---

## 42. Hallazgos de la auditoría

### Funcionalidades confirmadas

- Stack Blazor Server + EF + SQLite/SQL Server operativo en código.
- Captura con sesión, aislamiento, share por IDs, informe de sesión.
- Clasificación Normal/Advertencia/Crítico con umbrales configurables.
- Export PDF (completo/clásico), Excel, CSV.
- Matriz de permisos editable por SuperAdmin con revalidación.
- Cliente Android WebView + share nativo.
- Migración histórica Firestore→SQL como herramienta.

### Funcionalidades parciales

- Tests UI/E2E ausentes.
- README desactualizado respecto a Mobile y a la ubicación del Flutter hermano.
- `docs/ANDROID_APK.md` vs TFM `net10.0-android`.
- UI Usuarios «mín. 6» vs backend ≥8.
- Analytics PDF con charts depende de captura JS (fallback sin imágenes).
- Informe guardado: export/compartir solo estilo **completo** (no hay par «compartir clásico» de informe guardado).

### Funcionalidades legacy

- Share texto; `/reportes` redirect; campos ExternalUserId; `ManageSettings` reservado; formatter de texto no usado en share de archivos Captura.

### Funcionalidades sin tests aparentes

- Pantallas Blazor E2E, Migración UI, Login E2E, Gráficas JS, Telas import UI, Informes UI, MAUI bridge.

### Implementadas pero no evidenciadas en uso actual

- `RecordShareFormatter` / `BuildShareTextForIdsAsync` en flujo de archivo Captura (sí en tests).
- `regnepsUi.shareText` frente a share de archivos.
- `ManageSettings`.

### Posibles inconsistencias documentales

- Pedido de auditoría asume Flutter + etiquetas OK/Mención/2da Calidad — **no aplican** a este repo (clasificación real: Normal / Advertencia / Crítico).
- `classifyNeps()` / `NepsQualityCriteria` / `ReportShareHelper` / `FileShareHelper` / `share_plus` — **AUSENTES**; equivalentes .NET documentados arriba.
- `README.md` afirma que «la app Flutter original permanece en la raíz» — **en este workspace no hay proyecto Flutter**; el hermano está fuera (`Documents\regneps`).
- `README.md` estructura omite `RegNeps.Mobile`, `tests/`, `tools/`, `scripts/`.
- `docs/ANDROID_APK.md` puede citar TFM distinto al `net10.0-android` del `.csproj` actual.
- UI reset contraseña «mín. 6» vs validación backend ≥8 + letras y números.
- `VentanaReincidenciasHoras` mostrada en Config no alimenta `HasCriticalRecurrence` (usa días).

### Dependencias críticas

- EF Core, QuestPDF, ClosedXML, Blazor Server/SignalR, BCrypt, (opcional) SQL Server, MAUI para APK.

### Riesgos técnicos detectados (solo documentados)

- Sin offline: dependencia total del servidor.
- Secretos de Firebase en `secrets/` si se usan para export — riesgo de filtrado si se versionan.
- TempExport en memoria de un solo nodo (limitación en farm multi-instancia — NO DETERMINADO si hay balanceo).
- Circuit Blazor: archivos grandes limitados (SignalR 15 MB configurado).

---

## 43. Conclusión e inventario final

| Métrica | Valor derivado del análisis |
| ------- | --------------------------- |
| Módulos documentados | 16 (Auth, Dashboard, Captura, Registros, Alertas, Analítica, Informes, Export, Telas, Lotes, Usuarios, Roles, Config, Migración, Mobile, Sistema) |
| Pantallas con ruta | 17 |
| Componentes Shared/Layout (sin ruta) | 16 |
| Funcionalidades con ID | 52 (matriz maestra §39) |
| Subfuncionalidades (aprox.) | ~110 (incl. Apéndice E Captura) |
| Flujos E2E documentados | 14 + 4 adicionales |
| Servicios / clases de aplicación principales | 12+ |
| Modelos/entidades EF | 9 |
| Repositorios | 7 |
| Endpoints HTTP API | 11 |
| Archivos de test | 10 (clases de test ≥12) |
| Líneas `.cs` + `.razor` | 16 937 |
| Plataformas soportadas | Web (Blazor Server), Android (MAUI WebView); servidor hospedable en Windows/Linux |
| Integraciones externas | SQL Server opcional; Firebase solo como origen de migración (herramientas Node/CLI) |

### Inventario funcional final

RegNeps.Net es un sistema **intranet .NET** de control de calidad de neps con autenticación local, permisos granulares, captura concurrente multi-usuario, analítica, alertas y exportación profesional PDF/Excel/CSV, más un shell Android que reutiliza la misma UI servidor. **No** incluye runtime Firebase ni las etiquetas de clasificación del producto Flutter hermano.

---

## Apéndice A — Endpoints HTTP

| Método | Ruta | Permiso / notas |
| ------ | ---- | --------------- |
| POST | `/api/login` | Anónimo |
| POST | `/api/logout` | Autenticado |
| GET | `/api/export/{format}` | ExportReports |
| GET | `/api/export/saved/{id}/{format}` | ManageReports \| ExportReports |
| GET | `/api/export/analytics/{format}` | ViewDashboard |
| GET | `/api/export/temp/{id}` | Varios permisos lectura/export + owner |
| GET | `/api/export/fabrics/{format}` | ManageFabrics |
| GET | `/api/export/lotes/{format}` | ManageFabrics |
| GET | `/api/import/template` | (uso desde Registros con Edit) |
| POST | `/api/migration/import` | SuperAdmin (migración) |

## Apéndice B — Referencias de documentación existente

- `README.md`
- `docs/DESPLIEGUE_INTRANET.md`
- `docs/MIGRACION_FIRESTORE.md`
- `docs/ANDROID_APK.md`
- `src/RegNeps.Mobile/README.md`

## Apéndice C — Criterio de estados usados

| Estado | Significado |
| ------ | ----------- |
| COMPLETA | Implementada y con evidencia de uso en UI/API/tests |
| PARCIAL | Existe pero incompleta o con gaps |
| LEGACY | Conservada por compatibilidad, uso secundario |
| IMPLEMENTADA PERO NO EVIDENCIADA EN USO | Código presente sin enlace claro desde UI principal |
| DOCUMENTADA PERO NO IMPLEMENTADA | Mencionada en docs/pedido pero sin código |
| NO DETERMINADA | Sin evidencia suficiente |
| AUSENTE | No existe en este repositorio |

## Apéndice D — Correspondencia pedido Flutter → RegNeps.Net

| Concepto del pedido | Estado en este repo |
| ------------------- | ------------------- |
| `lib/`, `pubspec.yaml`, Flutter platforms | AUSENTE |
| Firebase Auth / Firestore runtime | AUSENTE (solo migración) |
| `classifyNeps()` | AUSENTE → `AlertEvaluator.GetLevel` |
| OK / Mención / Crítico — Realizar Ajuste / 2da Calidad | AUSENTE → Normal / Advertencia / Crítico |
| `NepsQualityCriteria` | AUSENTE |
| `FileShareHelper` / `ReportShareHelper` / `share_plus` | AUSENTE → `regnepsDownload` + MAUI Share + `ShareDeliveryPolicy` |
| `RecordExportCoordinator` | AUSENTE → `ReportExportAppService` |
| EXTRA_TEXT Android con archivos | Sin constante explícita; archivos se comparten **sin** texto adjunto (Web y MAUI) |
| Offline / sync Firestore | NO DISPONIBLE |
| iOS / desktop client | NO DISPONIBLE |

## Apéndice E — Subfuncionalidades de Captura (desglose)

| ID padre | Subfunción | Evidencia |
| -------- | ---------- | --------- |
| CAP-001 | Ingresar telar / neps / tela / lote | Formulario `Captura.razor` |
| CAP-001 | Ingresar turno / operario / línea / observación | Panel «Datos adicionales» |
| CAP-001 | Asegurar tela/lote activos en catálogo | `EnsureActiveByNameAsync` / `EnsureActiveByCodeAsync` |
| CAP-001 | Idempotencia ClientOperationId | `CreateNepRecordRequest` + índice único |
| CAP-001 | Asociar CaptureSessionId | Campo en registro + JS session |
| CAP-002 | Preview mts en vivo | `Neps / 0.09` en UI |
| CAP-003 | Evaluar alerta post-guardado | `CreateWithOutcomeAsync` + `ToDisplayLabel` |
| CAP-003 | Recomendaciones / reincidencia | `AlertEvaluator` vía servicio |
| CAP-004 | Confirmación si hay borrador pendiente | Modales `SaveAndStartNewAsync` |
| CAP-005 | forceNew session GUID | `EnsureCaptureSessionAsync(forceNew: true)` |
| CAP-005 | Limpiar tabla/KPIs/selección de sesión | `BeginFreshCaptureSessionAsync` |
| CAP-006 | ConcurrencyStamp en update | `ExpectedConcurrencyStamp` |
| CAP-008 | Toggle selección + persistencia relativa a lista | `HashSet<Guid>` |
| CAP-009…011 | Formato PDF/Excel/CSV | Modales de formato |
| CAP-012 | Snapshot + FiltersJson | `SaveReportAsync` |

---

*Fin del inventario. Auditoría solo lectura — código de aplicación no modificado. Fecha de revisión: 2026-10-01.*
