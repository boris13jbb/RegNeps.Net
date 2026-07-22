# RegNeps.Net — Migración C# completa (Web + PC / Intranet)

Aplicación **ASP.NET Core 8 + Blazor Server** para control de calidad de neps VICUNHA.
Uso en **navegador en PCs de la intranet** (oficina y planta).

La app Flutter original permanece en la raíz del repositorio como referencia / transición.

## Estructura

```
RegNeps.Net/
├── RegNeps.sln
└── src/
    ├── RegNeps.Domain/          # Entidades, permisos, alertas, filtros
    ├── RegNeps.Application/     # Auth, captura, analytics, reportes, usuarios
    ├── RegNeps.Infrastructure/  # EF Core, SQLite/SQL Server, CSV/Excel/PDF, import
    └── RegNeps.Web/             # UI Blazor + cookie auth + API export
```

## Cómo ejecutar

```powershell
cd RegNeps.Net
dotnet restore
dotnet run --project src/RegNeps.Web
```

- Local: http://localhost:5080  
- Intranet: `http://<IP-servidor>:5080`  
- Usuario seed (solo si la BD está vacía): ver `DbSeeder` / pantalla de login

## Módulos migrados

| Módulo Flutter | Ruta .NET | Estado |
|----------------|-----------|--------|
| Login / AuthGate | `/login` + cookies | Operativo (usuarios locales) |
| Dashboard | `/dashboard` | Operativo |
| Gráficas / Analytics | `/graficas` | Operativo (KPIs + tablas) |
| Captura | `/captura` | Operativo |
| Registros + filtros + import | `/registros` | Operativo |
| Alertas + correctivas | `/alertas` | Operativo |
| Telas | `/telas` | Operativo |
| Informes guardados | `/informes` | Operativo |
| Reportes profesionales | `/reportes` | Operativo (periodo, preview, export) |
| Exportar CSV/Excel/PDF | `/exportar` + `/api/export/*` | Operativo |
| Usuarios | `/usuarios` | Operativo |
| Config alertas | `/config` | Operativo |
| Roles / permisos | `RolePermissions` | Paridad con Flutter |
| Fórmula Mts = Neps / 0.09 | Domain | Operativa |
| Umbrales 30 / 60 + reincidencia | Domain | Operativos |

## Base de datos

- Desarrollo: SQLite (`regneps_v2.db` en carpeta de ejecución)
- Intranet: SQL Server — en `appsettings.json`:

```json
"ConnectionStrings": {
  "RegNeps": "Server=SERVIDOR;Database=RegNeps;Trusted_Connection=True;TrustServerCertificate=True"
},
"Database": { "UseSqlServer": true }
```

## Despliegue intranet

Ver [docs/DESPLIEGUE_INTRANET.md](docs/DESPLIEGUE_INTRANET.md).

```powershell
dotnet publish src/RegNeps.Web -c Release -o publish
```

## Migración de datos históricos (Firestore → SQL)

Ver [docs/MIGRACION_FIRESTORE.md](docs/MIGRACION_FIRESTORE.md).

Resumen:

```powershell
# 1) Export (raíz del repo, con service account o firebase login)
node scripts/export_firestore_history.js --credentials secrets/serviceAccountKey.json --out FTS/firestore_export.json

# 2) Import por UI: /migracion  (super admin)
#    o por CLI:
cd RegNeps.Net
dotnet run --project tools/RegNeps.Migrate -- --file ..\FTS\firestore_export.json --db src\RegNeps.Web\regneps_v2.db
```

## Fuera de alcance web/PC (o siguiente fase)

- App Android / offline Hive
- Firebase Auth, Firestore, FCM push
- Active Directory / Entra ID (hoy: usuarios locales + cookies)
- Los 25+ tipos de gráfica del builder Flutter (hay analytics + export profesional)
- Migración automática de datos históricos Firestore → SQL

## Publicar intranet

```powershell
dotnet publish src/RegNeps.Web -c Release -o publish
```

Ejecutar el host en servidor interno (Kestrel o IIS), puerto solo en red corporativa.
