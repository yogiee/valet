# Publish a self-contained Valet.exe and (if Inno Setup is present) compile the installer.
# Output:
#   src\Valet\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\Valet.exe
#   dist\Valet-Setup-<version>.exe
#
# Run from repo root:
#   .\build.ps1

$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot

Write-Host "==> Publishing Valet.exe (Release, self-contained, single-file)" -ForegroundColor Cyan
dotnet publish src/Valet/Valet.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true

$publishExe = Join-Path $PSScriptRoot "src\Valet\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\Valet.exe"
if (-not (Test-Path $publishExe)) {
    throw "Expected publish output not found at $publishExe"
}
$size = [math]::Round((Get-Item $publishExe).Length / 1MB, 2)
Write-Host "    Published: $publishExe ($size MB)" -ForegroundColor Green

$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning "Inno Setup 6 (ISCC.exe) not found in any of:"
    $isccCandidates | ForEach-Object { Write-Warning "    $_" }
    Write-Host "    Install with: winget install --id JRSoftware.InnoSetup --source winget" -ForegroundColor Yellow
    Write-Host "    Then re-run .\build.ps1 to produce the installer." -ForegroundColor Yellow
    exit 0
}

Write-Host "==> Compiling installer" -ForegroundColor Cyan
& $iscc (Join-Path $PSScriptRoot "installer\Valet.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit $LASTEXITCODE" }

$setup = Get-ChildItem (Join-Path $PSScriptRoot "dist") -Filter "Valet-Setup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($setup) {
    $size = [math]::Round($setup.Length / 1MB, 2)
    Write-Host "    Built: $($setup.FullName) ($size MB)" -ForegroundColor Green
}
