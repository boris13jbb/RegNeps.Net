$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root 'src\RegNeps.Mobile\RegNeps.Mobile.csproj'
$TargetFramework = 'net10.0-android'
$Output = Join-Path $Root "src\RegNeps.Mobile\bin\Debug\$TargetFramework"

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$Step
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Step falló. dotnet terminó con código $LASTEXITCODE."
    }
}

Write-Host '== RegNeps Android QA APK ==' -ForegroundColor Cyan
Write-Host "Proyecto: $Project"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'No se encontró dotnet en PATH.'
}

$SdkVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'No se pudo consultar la versión de .NET SDK.'
}

$SdkMajor = [int]($SdkVersion.Split('.')[0])
Write-Host "SDK .NET: $SdkVersion"

if ($SdkMajor -lt 10) {
    throw "RegNeps.Mobile requiere .NET SDK 10 o superior. SDK detectado: $SdkVersion"
}

Write-Host 'Instalando/verificando workload MAUI Android...'
Invoke-DotNet -Step 'La instalación/verificación de maui-android' -Arguments @('workload', 'install', 'maui-android')

Write-Host 'Restaurando workloads requeridos por el proyecto...'
Invoke-DotNet -Step 'La restauración de workloads' -Arguments @('workload', 'restore', $Project)

Write-Host 'Restaurando dependencias...'
Invoke-DotNet -Step 'La restauración de dependencias' -Arguments @('restore', $Project)

Write-Host 'Compilando APK Debug instalable...'
Invoke-DotNet -Step 'La compilación Android' -Arguments @('build', $Project, '-f', $TargetFramework, '-c', 'Debug', '-p:AndroidPackageFormat=apk')

$Apks = Get-ChildItem -Path $Output -Filter '*.apk' -Recurse -ErrorAction SilentlyContinue
if (-not $Apks) {
    throw "La compilación terminó correctamente pero no se encontró ningún APK en $Output"
}

Write-Host ''
Write-Host 'APK generado:' -ForegroundColor Green
$Apks | ForEach-Object { Write-Host $_.FullName -ForegroundColor Green }
