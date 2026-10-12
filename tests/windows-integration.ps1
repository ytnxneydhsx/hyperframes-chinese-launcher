param([Parameter(Mandatory=$true)][string]$Launcher)
$ErrorActionPreference = 'Stop'
if (-not $env:GITHUB_ACTIONS) { throw 'This integration check is only for a disposable GitHub Windows runner.' }
$root = Split-Path $PSScriptRoot -Parent
$folder = Join-Path $env:RUNNER_TEMP 'hyperframes-official'
$installer = Join-Path $env:RUNNER_TEMP 'Install-HyperFrames.exe'
$metadata = Invoke-RestMethod 'https://static.heygen.ai/hyperframes-oss/desktop/latest.json'
if (-not $metadata.windows.url.StartsWith('https://static.heygen.ai/hyperframes-oss/desktop/')) { throw 'Unexpected official download URL' }
Invoke-WebRequest $metadata.windows.url -OutFile $installer
if ((Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant() -ne $metadata.windows.sha256) { throw 'Official installer checksum mismatch' }
$setup = Start-Process $installer -ArgumentList @('/S', "/D=$folder") -PassThru
if (-not $setup.WaitForExit(120000)) { throw 'Official installer did not complete within two minutes' }
if ($setup.ExitCode -ne 0) { throw "Official installer exit code $($setup.ExitCode)" }
$original = Join-Path $folder 'HyperFrames.exe'
if (-not (Test-Path $original)) { throw 'Official installer did not create the requested application' }
$hashes = @{}
foreach ($file in @($original, (Join-Path $folder 'resources/app.asar'))) {
    if (Test-Path $file) { $hashes[$file] = (Get-FileHash $file -Algorithm SHA256).Hash }
}
$configDir = Join-Path $env:LOCALAPPDATA 'HyperFramesChinese'
New-Item $configDir -ItemType Directory -Force | Out-Null
@{executable=$original} | ConvertTo-Json | Set-Content (Join-Path $configDir 'config.json') -Encoding utf8NoBOM
$helper = $null
try {
    $helper = Start-Process (Resolve-Path $Launcher).Path -PassThru
    $statePath = Join-Path $configDir 'state.json'
    $state = $null
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        if ($helper.HasExited) { throw 'Launcher exited before establishing translation' }
        if (Test-Path $statePath) {
            $candidate = Get-Content $statePath -Raw | ConvertFrom-Json
            if ($candidate.version -eq '1.1.0' -and $candidate.status -eq 'connected' -and $candidate.translatedCount -gt 0) { $state=$candidate; break }
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $state) { if (Test-Path $statePath) { Get-Content $statePath }; throw 'No translated official Windows application within 90 seconds' }
    dotnet run --project "$root/tests/TransportTests/TransportTests.csproj" -c Release -- --probe $state.port "$PSScriptRoot/runtime-fixture.js"
    if ($LASTEXITCODE -ne 0) { throw 'Official Windows UI translation check failed' }
    $quit = Start-Process (Resolve-Path $Launcher).Path -ArgumentList '--quit' -PassThru
    if (-not $quit.WaitForExit(10000)) { throw 'Quit command did not complete' }
    if (-not $helper.WaitForExit(15000)) { throw 'Launcher did not restore and exit' }
    if (-not (Get-Process -Id $state.pid -ErrorAction SilentlyContinue)) { throw 'Original application unexpectedly exited with the launcher' }
    dotnet run --project "$root/tests/TransportTests/TransportTests.csproj" -c Release -- --restoration $state.port
    if ($LASTEXITCODE -ne 0) { throw 'Original English interface was not restored' }
    foreach ($file in $hashes.Keys) {
        if ((Get-FileHash $file -Algorithm SHA256).Hash -ne $hashes[$file]) { throw 'Official application file changed during translation' }
    }
    $report=@{passed=$true; platform='Windows'; launcherVersion='1.1.0'; officialBuild=$metadata.windows.build; realApplicationCdp=$true; uiFixture=$true; normalQuitRestoresEnglish=$true; originalApplicationRemainsRunning=$true; originalFilesUnchanged=$true; businessActions=0}
    New-Item "$root/dist/verification" -ItemType Directory -Force | Out-Null
    $report | ConvertTo-Json | Set-Content "$root/dist/verification/windows-integration.json" -Encoding utf8NoBOM
    $report | ConvertTo-Json
} finally {
    # These processes are newly installed on this disposable runner; no user projects exist here.
    if ($helper -and -not $helper.HasExited) { Stop-Process -Id $helper.Id -Force -ErrorAction SilentlyContinue }
    Get-Process HyperFrames -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
