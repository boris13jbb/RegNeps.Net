# Checklist de despliegue — paridad Flutter (`feature/parity-flutter`)

Documento operativo. **No ejecutar** estos comandos contra producción desde el agente; son guía para el operador.

## 1. Backup previo

### SQLite

Localizar el archivo (por defecto `App_Data/regneps.db` bajo el content root de `RegNeps.Web`, o la ruta de `ConnectionStrings:RegNeps`).

```powershell
# Con la app detenida:
Copy-Item ".\src\RegNeps.Web\App_Data\regneps.db" ".\backups\regneps-$(Get-Date -Format yyyyMMdd_HHmmss).db"
```

### SQL Server

```sql
BACKUP DATABASE [RegNeps]
TO DISK = N'D:\Backups\RegNeps_pre_parity.bak'
WITH COPY_ONLY, INIT, COMPRESSION, STATS = 10;
```

Verificar restauración en un entorno de prueba antes del corte.

## 2. Orden de despliegue y parche de roles

1. Backup (paso 1).
2. Desplegar el build de `RegNeps.Web` (y APK MAUI si aplica, paso 6).
3. Arrancar la app: `DatabaseInitializer.InitializeAsync` ejecuta `EnsureCreated` (solo BD nueva), **parches aditivos** y `DbSeeder` / `RoleBootstrap`.
4. Smoke test con SuperAdmin.

### Qué hace el parche (`RoleSchemaPatches` + `RoleBootstrap`)

Idempotente: se puede reiniciar la app sin duplicar roles/permisos ni pisar `IsEnabled` ya editados.

| Cambio | SQLite | SQL Server |
|--------|--------|------------|
| Tabla `Roles` | `CREATE TABLE IF NOT EXISTS` + índice único `Code` | `IF OBJECT_ID ... CREATE TABLE` + índice |
| Columna `Users.RoleCode` | `ALTER TABLE ... ADD COLUMN` si no existe | `ALTER TABLE ... ADD` si no existe |
| `RolePermissions` enum → `RoleId` | Rebuild a tabla nueva + `INSERT` mapeando `Role` 0–4 → códigos | Igual con `sp_rename` |
| `RolePermissionAudits` | Análogo | Análogo |
| Semilla 5 roles sistema | `INSERT OR IGNORE` (Guid formato D) | `IF NOT EXISTS ... INSERT` (`NEWID()`) |
| Reparación Guid hex | Convierte `Roles.Id` / `RoleId` de 32 hex sin `-` a formato D | N/A (uniqueidentifier) |
| `RoleCode` en usuarios legacy | `RoleBootstrap.SyncUserRoleCodesAsync` (solo nulos/vacíos) | Igual |
| Permisos faltantes | Solo inserta filas inexistentes; **no** cambia `IsEnabled` | Igual |

Roles de sistema: Operario, Supervisor, Admin, Gerencia, SuperAdmin (`SystemRoleCodes.Definitions`).

### Parche transaccional (rebuild DROP + rename)

El rebuild de `RolePermissions` / `RolePermissionAudits` (crear `_new` → insertar → `DROP` tabla original → rename) corre dentro de una **transacción explícita** (`Database.BeginTransactionAsync`). El proyecto no usa `EnableRetryOnFailure`, así que no hace falta `CreateExecutionStrategy`.

- **Si el parche falla a mitad:** se hace `ROLLBACK`. La BD queda como antes del rebuild (sin tabla `_new` huérfana y sin haber perdido la tabla original).
- **Si el parche termina bien:** `COMMIT`. Un segundo arranque no vuelve a rebuild (ya no existe la columna enum `Role`).
- **Backup previo (paso 1) sigue siendo obligatorio** ante cualquier despliegue: la transacción protege un fallo a mitad del rebuild, no sustituye un backup ante corrupción de disco, despliegue incorrecto o necesidad de volver atrás de versión.

### Prueba manual SQL Server (staging) — rollback del rebuild

NO VERIFICABLE en CI/local sin SQL Server/LocalDB. En staging:

1. Backup de la BD.
2. Dejar (o restaurar) un esquema con `RolePermissions.Role` (enum) y sin `RoleId`.
3. Forzar un fallo controlado a mitad del rebuild (p. ej. breakpoint/`THROW` tras el `DROP`, o simular error de permisos DDL) y confirmar que tras el error:
   - sigue existiendo `RolePermissions` con columna `Role`;
   - no queda `RolePermissions_new`;
   - los datos enum previos siguen legibles.
4. Arrancar de nuevo sin el fallo: el rebuild debe completar e idempotencia en un segundo arranque.

## 3. Rollback

1. Detener la app.
2. Restaurar el backup de BD del paso 1.
3. Desplegar el binario anterior (sin código de Fase 6).

**Nota sobre `Users.RoleCode`:** si se restaura un backup **anterior** al parche, la columna no existía. Si se revierte solo el código pero se deja la BD ya parchada, la columna `RoleCode` sobra pero no rompe un binario antiguo que la ignore; un binario antiguo que aún lea `RolePermissions.Role` (enum) **sí** fallará si la tabla ya se migró a `RoleId`. Por eso el rollback seguro es **código anterior + backup de BD**.

## 4. Reverse proxy / WebSockets (`/hubs/alerts`)

SignalR necesita WebSockets (o al menos un transporte estable). Objetivo: no caer a long polling en intranet.

### IIS (ARR / site)

- Instalar WebSocket Protocol en el servidor.
- En el site/aplicación: **WebSockets = Enabled**.
- Si hay ARR: habilitar proxy y no bufferizar respuestas largas.
- Ejemplo de verificación: DevTools → Network → WS hacia `/hubs/alerts` (estado 101).

### nginx

```nginx
location /hubs/alerts {
    proxy_pass         http://regneps_upstream;
    proxy_http_version 1.1;
    proxy_set_header   Upgrade $http_upgrade;
    proxy_set_header   Connection "upgrade";
    proxy_set_header   Host $host;
    proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header   X-Forwarded-Proto $scheme;
    proxy_read_timeout 3600s;
    proxy_send_timeout 3600s;
}
```

Verificación: conexión WS 101; en logs del cliente no debe aparecer transporte `LongPolling` de forma permanente.

## 5. Variables / archivos a revisar

| Ítem | Dónde | Notas |
|------|--------|--------|
| Cadena de conexión | `ConnectionStrings:RegNeps` | SQLite o SQL Server |
| `Database:UseSqlServer` | appsettings | `true` en intranet SQL |
| `Urls` | appsettings | p. ej. `http://0.0.0.0:5080` |
| SignalR max message | `Program.cs` | `MaximumReceiveMessageSize = 15 MB` (gráficas/PDF) |
| TTL `TempExportStore` | `TempExportStore` | 5 minutos; descargas ligadas al usuario |
| Cookie auth | `RegNeps.Auth` | SameAsRequest; LoginPath `/login` |
| `appsettings.Production.json` | ya en repo | Revisar ConnectionStrings en el servidor (no commitear secretos nuevos) |

## 6. APK Android (MAUI)

Si cambió el puente de compartir (`RegNeps.Mobile` / WebView):

1. Recompilar con `scripts/build_android_apk.ps1` y `-p:EmbedAssembliesIntoApk=true` (ver `docs/ANDROID_APK.md`).
2. Confirmar TFM `net8.0-android` según ese documento.
3. Instalar en dispositivo de QA y probar compartir PDF/Excel/CSV desde Captura.

## 7. Pruebas manuales post-despliegue

- [ ] Backup restaurable verificado en staging
- [ ] Arranque en BD antigua (pre-roles) sin error; segundo arranque idempotente
- [ ] SQL Server staging: mismos checks de roles/permisos
- [ ] IDOR: Operario abre `/registros?reportId=` o export de informe ajeno → «no encontrado»
- [ ] Rol inactivo / permiso revocado: deja de ver menús y operaciones en backend
- [ ] Toast SignalR de alerta crítica en dos sesiones con `ViewAlerts`
- [ ] Usuario sin `ViewAlerts` no recibe push
- [ ] Compartir en Android (APK) genera archivo, no solo texto
- [ ] Constructor de informes: PDF y Excel
- [ ] Importar CSV en Registros
- [ ] Informe abierto en Registros: solo lectura (sin editar/borrar/guardar informe)
- [ ] Proxy: `/hubs/alerts` por WebSocket (101)
