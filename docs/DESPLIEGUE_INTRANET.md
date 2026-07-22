# Despliegue intranet — RegNeps.Web

## Publicar

```powershell
cd C:\Users\BRS\Documents\regneps\RegNeps.Net
dotnet publish src/RegNeps.Web -c Release -o publish
```

## Ejecutar en servidor interno (Kestrel)

```powershell
cd publish
.\RegNeps.Web.exe
```

Escucha en `http://0.0.0.0:5080` (configurable en `appsettings.json` → `Urls`).

Desde otro PC: `http://IP-DEL-SERVIDOR:5080`

## Base de datos

Por defecto SQLite en `App_Data/regneps_v2.db` (ruta absoluta bajo el ContentRoot).

Para SQL Server en intranet, edite `appsettings.json` / `appsettings.Production.json`:

```json
{
  "ConnectionStrings": {
    "RegNeps": "Server=SERVIDOR\\INSTANCIA;Database=RegNeps;Trusted_Connection=True;TrustServerCertificate=True"
  },
  "Database": {
    "UseSqlServer": true
  },
  "Urls": "http://0.0.0.0:5080"
}
```

## Firewall

Abrir puerto **5080** (o 80 si pone un reverse proxy IIS) solo en la red corporativa.

## IIS (opcional)

1. Instalar [ASP.NET Core Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/8.0)
2. Crear sitio apuntando a la carpeta `publish`
3. Application Pool → No Managed Code
4. Binding HTTP en la IP interna

## Checklist post-despliegue

- [ ] Login admin / usuarios migrados
- [ ] Captura de un registro de prueba
- [ ] Ver registros históricos (11+)
- [ ] Exportar PDF/Excel
- [ ] Cambiar contraseñas temporales de usuarios migrados
