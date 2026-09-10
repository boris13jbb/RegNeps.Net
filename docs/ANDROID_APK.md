# RegNeps.Net - APK Android de QA

## Qué es esta APK

`RegNeps.Mobile` es un cliente Android .NET MAUI que abre la aplicación `RegNeps.Web` dentro de un WebView.

- No mezcla código con el proyecto Flutter anterior.
- No mueve la base de datos al teléfono.
- Usuarios, permisos, capturas, reportes y SQLite/SQL Server continúan en `RegNeps.Web`.
- La APK necesita poder acceder al servidor RegNeps por red.
- URL inicial de QA: `http://192.168.100.140:5080`.
- La URL se puede cambiar desde la parte superior de la aplicación y queda guardada en el dispositivo.

## Requisito previo

En el equipo que ejecuta RegNeps.Net:

```powershell
cd C:\Users\BRS\Documents\RegNeps.Net
dotnet run --project src\RegNeps.Web
```

El teléfono debe estar en la misma red y poder abrir en Chrome:

```text
http://192.168.100.140:5080
```

Si no abre en Chrome, la APK tampoco podrá conectarse. Revisa firewall, IP del PC y puerto 5080.

## Generar APK localmente

Desde PowerShell:

```powershell
cd C:\Users\BRS\Documents\RegNeps.Net
git checkout feature/android-apk-shell
git pull
powershell -ExecutionPolicy Bypass -File .\scripts\build_android_apk.ps1
```

El APK se genera bajo:

```text
src\RegNeps.Mobile\bin\Debug\net8.0-android\
```

La compilación `Debug` usa la firma de desarrollo de Android y sirve para instalar y probar manualmente.

## Generar APK en GitHub Actions

El workflow se llama `Android APK QA`.

1. Abre el repositorio en GitHub.
2. Entra en **Actions**.
3. Selecciona **Android APK QA**.
4. Ejecuta **Run workflow** sobre `feature/android-apk-shell`.
5. Cuando termine, descarga el artefacto **RegNeps-Android-QA**.

## Instalación en el teléfono

Puedes copiar el `.apk` al teléfono e instalarlo habilitando temporalmente la instalación desde la fuente utilizada para abrir el archivo.

Para instalar por ADB:

```powershell
adb devices
adb install -r RUTA\AL\ARCHIVO.apk
```

## APK de producción

Para distribución real no se debe usar la firma Debug. Crea y conserva un keystore propio y genera una compilación Release firmada. No guardes contraseñas ni el keystore directamente en el repositorio.

Ejemplo de publicación Release firmada:

```powershell
dotnet publish src\RegNeps.Mobile\RegNeps.Mobile.csproj `
  -f net8.0-android `
  -c Release `
  -p:AndroidPackageFormats=apk `
  -p:AndroidKeyStore=true `
  -p:AndroidSigningKeyStore=C:\seguro\regneps.keystore `
  -p:AndroidSigningKeyAlias=regneps `
  -p:AndroidSigningKeyPass=file:C:\seguro\keypass.txt `
  -p:AndroidSigningStorePass=file:C:\seguro\storepass.txt
```

## Importante: no es una APK offline

Esta fase empaqueta un cliente Android para el sistema Blazor Server actual. Si se requiere que RegNeps funcione sin PC/servidor y con SQLite dentro del teléfono, hay que crear una segunda fase con .NET MAUI Blazor Hybrid y extraer la UI/servicios reutilizables del proyecto Web.
