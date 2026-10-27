param([string]$OfflineFeed = "")
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    dotnet run --project tests/EdifierManager.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw "Offline tests failed." }
    dotnet run --project tests/WindowLifecycle/WindowLifecycle.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw "Window lifecycle tests failed." }
    $buildArgs = @("tools/build_portable.py")
    if ($OfflineFeed) { $buildArgs += @("--offline-feed", $OfflineFeed) }
    python @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "Portable build failed." }
    python tools/verify_portable.py
    if ($LASTEXITCODE -ne 0) { throw "Portable verification failed." }
    python tools/package.py
    if ($LASTEXITCODE -ne 0) { throw "Packaging failed." }
}
finally { Pop-Location }
