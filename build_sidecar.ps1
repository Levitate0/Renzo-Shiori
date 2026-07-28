# Stages the JVM sidecar assets into RenzoBackend/sidecar/ so the Docker image can bundle them.
# Run this BEFORE build_docker.ps1. Requires JDK 21 on PATH (gradle builds the AndroidCompat fat jar;
# it uses LinkedHashMap.firstEntry(), which is Java 21+).
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$androidLayer = Join-Path $root 'Mihon.ExtensionsBridge.Net/Android.Compatibility.Layer'
$gradlew = if ($IsWindows) { Join-Path $androidLayer 'gradlew.bat' } else { Join-Path $androidLayer 'gradlew' }

Write-Host 'Building AndroidCompat fat jar (sidecar server + bundled compression/zstd)...'
Push-Location $androidLayer
try {
    if (-not $IsWindows) { chmod +x $gradlew | Out-Null }
    & $gradlew ':AndroidCompat:shadowJar' '--no-daemon'
    if ($LASTEXITCODE -ne 0) { throw "gradle shadowJar failed ($LASTEXITCODE)" }
}
finally { Pop-Location }

$libs = Join-Path $androidLayer 'AndroidCompat/build/libs'
$fatJar = Get-ChildItem $libs -Filter '*-all.jar' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $fatJar) { throw "No AndroidCompat fat jar produced in $libs" }

$dest = Join-Path $root 'RenzoBackend/sidecar'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item $fatJar.FullName (Join-Path $dest 'AndroidCompat-1.0-all.jar') -Force

# enjarify translator (Apache-2.0, pure Python) used by the sidecar's /convert
$enjSrc = Join-Path $root 'Mihon.ExtensionsBridge.Net/tools/enjarify'
$enjDst = Join-Path $dest 'enjarify'
if (Test-Path $enjDst) { Remove-Item $enjDst -Recurse -Force }
Copy-Item $enjSrc $enjDst -Recurse -Force
Get-ChildItem $enjDst -Recurse -Directory -Filter '__pycache__' | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Sidecar assets staged to $dest :"
Get-ChildItem $dest | ForEach-Object { Write-Host "  $($_.Name)" }
Write-Host "Next: build_docker.ps1, then set RENZO_USE_SIDECAR=1 on the renzo-shiori service to cut over."
