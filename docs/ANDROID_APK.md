# RegNeps.Net — APK Android de QA

## Qué es esta APK

`RegNeps.Mobile` es un cliente Android **.NET MAUI** (`net10.0-android`) que:

1. Abre `RegNeps.Web` en un **WebView** (modo online: cookie = auth real).
2. Mantiene un **OfflineStore SQLite local** (Outbox + réplica + sync Push/Pull al backend propio).
3. Usa **SignalR** (`/hubs/alerts`) para notificar recuperación → el cliente hace **Pull** (SignalR no transporta change logs).
4. Guarda material auxiliar en **SecureStorage** (no cookies/Bearer; ver FASE 2G).

No es el proyecto Flutter hermano. La BD de negocio (usuarios, permisos, registros canónicos) vive en el **servidor** (SQLite Web o SQL Server). El SQLite del teléfono es réplica/Outbox offline, no el sustituto del servidor.

## Versiones (fuente de verdad)

| Ítem | Valor |
|------|--------|
| Proyecto | `src/RegNeps.Mobile/RegNeps.Mobile.csproj` |
| TargetFramework | **`net10.0-android`** |
| MauiVersion | `10.0.101` |
| ApplicationId | `com.burbatech.regneps` |
| Min API | 24 |
| SDK host | .NET SDK **10+** |
| Workload | `maui-android` |
| Script QA | `scripts/build_android_apk.ps1` |
| Bootstrap local | `LocalStoreInitializer` → **`MigrateAsync`** (EF OfflineStore) |

Bootstrap del servidor Web (esquema de negocio) es otro mecanismo: ver [`FASE2I_BOOTSTRAP_AND_MIGRATION.md`](FASE2I_BOOTSTRAP_AND_MIGRATION.md).

## Requisitos de red

1. Arrancar el servidor en el PC/intranet:

```powershell
cd C:\Users\BRS\Documents\RegNeps.Net
dotnet run --project src\RegNeps.Web
```

Escucha por defecto `http://0.0.0.0:5080`.

2. El dispositivo debe alcanzar esa URL (misma LAN). Probar primero en Chrome del teléfono.

3. La URL de QA se configura en la UI superior de la APK (persistida en el dispositivo). Ejemplo histórico de red local: `http://192.168.100.140:5080` — sustituir por la IP real del host.

4. Manifest: `INTERNET`, `ACCESS_NETWORK_STATE`, `android:usesCleartextTraffic="true"` (HTTP LAN de QA).

## Generar APK localmente (reproducible)

Desde la **rama/commit que se quiere validar** (no hace falta `feature/android-apk-shell`):

```powershell
cd C:\Users\BRS\Documents\RegNeps.Net
powershell -ExecutionPolicy Bypass -File .\scripts\build_android_apk.ps1
```

El script:

- exige SDK major ≥ 10;
- instala/restaura workload `maui-android`;
- compila `-f net10.0-android -c Debug -p:AndroidPackageFormat=apk` **`-p:EmbedAssembliesIntoApk=true`**.

**Importante:** sin `EmbedAssembliesIntoApk=true`, Fast Deployment deja assemblies fuera del APK y `adb install` puede producir una app que cierra al instante.

Salida esperada:

```text
src\RegNeps.Mobile\bin\Debug\net10.0-android\**\*.apk
```

La firma Debug de Android sirve para instalar y probar; no para distribución productiva.

## Generar APK en GitHub Actions

Workflow: **Android APK QA** (`.github/workflows/android-apk.yml`).

- Build: `net10.0-android`, Debug, `EmbedAssembliesIntoApk=true`.
- Artefacto: **RegNeps-Android-QA**.
- Trigger `push` histórico: rama `feature/android-apk-shell` (paths Mobile/script).
- En cualquier otra rama (p. ej. `feature/fase-2d5-offline-update`): usar **Actions → Run workflow** (`workflow_dispatch`) seleccionando la rama.

Este workflow **no** es el status check obligatorio de PRs a `main` (ese es `.NET CI` / `dotnet-ci.yml` sobre net8.0).

## Instalación

```powershell
adb devices
adb install -r RUTA\AL\ARCHIVO.apk
```

O copiar el `.apk` al teléfono e instalar permitiendo la fuente correspondiente.

## Release firmada (producción)

No usar firma Debug. Keystore fuera del repo; no commitear contraseñas.

```powershell
dotnet publish src\RegNeps.Mobile\RegNeps.Mobile.csproj `
  -f net10.0-android `
  -c Release `
  -p:AndroidPackageFormats=apk `
  -p:AndroidKeyStore=true `
  -p:AndroidSigningKeyStore=C:\seguro\regneps.keystore `
  -p:AndroidSigningKeyAlias=regneps `
  -p:AndroidSigningKeyPass=file:C:\seguro\keypass.txt `
  -p:AndroidSigningStorePass=file:C:\seguro\storepass.txt
```

## E2E offline (resumen)

Procedimiento detallado y escenarios A–H: [`FASE2H_VALIDATION_READINESS.md`](FASE2H_VALIDATION_READINESS.md).

```text
login online → captura → desconexión → mutación offline → reconexión → Push → Pull
(+ SignalR recovery, conflictos, ClearAll, logout/login)
```

Estado del gate E2E: **PENDING** hasta disponer de dispositivo/emulador real.
