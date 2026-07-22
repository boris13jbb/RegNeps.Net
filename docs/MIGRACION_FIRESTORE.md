# Migración Firestore → RegNeps.Net (SQL)

## Paso 1 — Exportar desde Firebase

Exporta:

- `workspaces/vicunha/records`
- `workspaces/vicunha/users`
- `workspaces/vicunha/reports`
- `workspaces/vicunha/meta/fabrics`
- `workspaces/vicunha/meta/config`

**No exporta contraseñas** (Firebase Auth no lo permite).

### Autenticación del export

1. **Service account (recomendado)**  
   Firebase Console → Project settings → Service accounts → generar clave JSON.  
   Guárdela fuera del git (p. ej. `secrets/serviceAccountKey.json`).

```powershell
node scripts/export_firestore_history.js --credentials secrets/serviceAccountKey.json --out FTS/firestore_export.json
```

2. **Variable de entorno**

```powershell
$env:GOOGLE_APPLICATION_CREDENTIALS="C:\ruta\serviceAccountKey.json"
node scripts/export_firestore_history.js --out FTS/firestore_export.json
```

3. **Firebase CLI** (si el token está vigente)

```powershell
firebase login
node scripts/export_firestore_history.js --out FTS/firestore_export.json
```

Si ve `Unauthorized` / token vencido: use service account o vuelva a ejecutar `firebase login`.

Hay un JSON de ejemplo (sin datos reales) en `FTS/firestore_export_sample.json`.

## Paso 2 — Importar a SQL

### Opción A — UI (super admin)

1. Ejecutar RegNeps.Web  
2. Entrar como super admin  
3. Menú **Migración histórica** (`/migracion`)  
4. Subir `FTS/firestore_export.json`  
5. Confirmar contraseña temporal  
6. Importar  

### Opción B — CLI

```powershell
cd RegNeps.Net
dotnet run --project tools/RegNeps.Migrate -- --file ..\FTS\firestore_export.json --db src\RegNeps.Web\regneps_v2.db
```

### Opción C — API

```http
POST /api/migration/import
Content-Type: multipart/form-data
file=firestore_export.json
tempPassword=Migracion123!
```

## Comportamiento

| Entidad | Regla |
|---------|--------|
| Registros | Upsert por id Firestore (GUID determinístico) |
| Usuarios | Upsert por username/email; password temporal |
| Telas | Inserta si no existe el nombre |
| Informes | Metadatos + filtros; snapshot resumido |
| Alertas config | Actualiza umbrales si vienen en el JSON |

La importación es **idempotente**: puede repetirse sin duplicar registros.

## Notas

- Tras migrar usuarios, cambie las contraseñas temporales desde **Usuarios**.
- Los `createdByUid` de registros se conservan como texto (uid Firebase).
- Para intranet productiva, apunte `ConnectionStrings:RegNeps` a SQL Server antes de importar.
