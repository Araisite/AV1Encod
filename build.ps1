# Construit l'exécutable autonome (aucune installation de .NET nécessaire) dans .\dist\AV1Encod.exe
#   .\build.ps1                 → version du .csproj
#   .\build.ps1 -Version 1.2.0  → version précise (c'est ce que fait la CI à partir du tag v1.2.0)
param([string]$Version)

$ErrorActionPreference = 'Stop'
$props = @(
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none', '-o', 'dist'
)
if ($Version) { $props += "-p:Version=$Version" }

Push-Location $PSScriptRoot
try {
    dotnet publish AV1Encod.csproj @props
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish a échoué ($LASTEXITCODE)" }
    $exe = Get-Item dist\AV1Encod.exe
    "{0}  ({1:N1} Mo)" -f $exe.FullName, ($exe.Length / 1MB)
}
finally { Pop-Location }
