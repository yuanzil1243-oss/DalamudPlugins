param([switch]$Package)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    $version = & python scripts/local_version.py
    if ($LASTEXITCODE -ne 0) { throw 'Version validation failed.' }
    New-Item -ItemType Directory -Path artifacts -Force | Out-Null
    & dotnet build NewFolder3.csproj -c Release -p:Platform=x64 -t:Rebuild -v:minimal *> "artifacts/final-build-$version.log"
    if ($LASTEXITCODE -ne 0) { Get-Content "artifacts/final-build-$version.log" -Tail 35; throw 'Plugin build failed.' }
    & dotnet run --project tests/NewFolder3.ContractTests -c Release -p:Platform=x64 *> "artifacts/contract-tests-$version.log"
    if ($LASTEXITCODE -ne 0) { Get-Content "artifacts/contract-tests-$version.log" -Tail 35; throw 'Contract tests failed.' }
    & dotnet run --project tests/NewFolder3.SpeedChecks -c Release *> "artifacts/speed-tests-$version.log"
    if ($LASTEXITCODE -ne 0) { Get-Content "artifacts/speed-tests-$version.log" -Tail 35; throw 'Speed tests failed.' }
    & powershell -NoProfile -ExecutionPolicy Bypass -File tests/Test-VersionSwitch.ps1 *> "artifacts/version-switch-tests-$version.log"
    if ($LASTEXITCODE -ne 0) { Get-Content "artifacts/version-switch-tests-$version.log" -Tail 35; throw 'Version switch tests failed.' }
    & python scripts/verify_local.py
    if ($LASTEXITCODE -ne 0) { throw 'Source/payload audit failed.' }
    if ($Package) {
        & python scripts/package_local.py
        if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
    }
    Write-Output "Local $version passed. No installation, game operation, publishing or feed changes."
} finally { Pop-Location }
