$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root 'src\RegNeps.Mobile\RegNeps.Mobile.csproj'
$Output = Join-Path $Root 'src\RegNeps.Mobile\bin\Debug\net8.0-android'

Write-Host '== RegNeps Android QA APK ==' -ForegroundColor Cyan
Write-Host "Proyecto: $Project"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'No se encontró dotnet en PATH.'
}

Write-Host "SDK .NET: $(dotnet --version)"
Write-Host 'Instalando/verificando workload MAUI Android...'
dotnet workload install maui-android

Write-Host 'Restaurando dependencias...'
dotnet restore $Project

Write-Host 'Compilando APK Debug instalable...'
dotnet build $Project -f net8.0-android -c Debug -p:AndroidPackageFormat=apk

$Apks = Get-ChildItem -Path $Output -Filter '*.apk' -Recurse -ErrorAction SilentlyContinue
if (-not $Apks) {
    throw "La compilación terminó pero no se encontró ningún APK en $Output"
}

Write-Host ''
Write-Host 'APK generado:' -ForegroundColor Green
$Apks | ForEach-Object { Write-Host $_.FullName -ForegroundColor Green }
