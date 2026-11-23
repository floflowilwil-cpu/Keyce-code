# Génère l'exécutable (publish autonome win-x64) puis l'installeur Windows (Inno Setup).
# Usage, depuis n'importe où :  .\installer\build-installer.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

Write-Host "1/2  Publication de l'application (win-x64, autonome)..."
dotnet publish src/App.Desktop -c Release -r win-x64 --self-contained true -o publish/win-x64
if ($LASTEXITCODE -ne 0) { throw "dotnet publish a échoué." }

$candidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $found = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($found) { $iscc = $found.Source }
}
if (-not $iscc) { throw "Inno Setup est introuvable. Installez-le avec : winget install JRSoftware.InnoSetup" }

Write-Host "2/2  Création du setup..."
& $iscc "installer\KeyceCode.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC a échoué." }

Write-Host "Terminé : installer\output\"
