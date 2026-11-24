# Run in Windows PowerShell 5.1 or newer, with the game closed. Inspect is the default.
param(
    [ValidateSet('Inspect','Official','Local')][string]$Mode = 'Inspect',
    [string]$LauncherRoot = (Join-Path $env:APPDATA 'XIVLauncherCN'),
    [string]$LocalDll,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$LauncherRoot = [IO.Path]::GetFullPath($LauncherRoot)
$configFile = Join-Path $LauncherRoot 'pluginConfigs\NewFolder3.json'
$launcherFile = Join-Path $LauncherRoot 'dalamudConfig.json'
$installed = Join-Path $LauncherRoot 'installedPlugins\NewFolder3'
$config = if (Test-Path -LiteralPath $configFile) { Get-Content -LiteralPath $configFile -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$versions = @(if (Test-Path -LiteralPath $installed) { Get-ChildItem -LiteralPath $installed -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'NewFolder3.json') } })
$summary = [ordered]@{ Mode=$Mode; Apply=[bool]$Apply; LegacyFarmingMode=$(if($config){$config.Fsd.Farming.Mode}else{$null}); InstalledVersions=@($versions.Name); UserDataDeleted=$false }
if (-not $Apply -or $Mode -eq 'Inspect') { $summary | ConvertTo-Json -Depth 5; return }
if (Get-Process -Name ffxiv_dx11,ffxiv -ErrorAction SilentlyContinue) { throw 'Close the game first. Live updates must use the in-game plugin manager; never edit its settings behind it.' }
if (!(Test-Path -LiteralPath $launcherFile)) { throw 'Dalamud configuration not found.' }
$settings = Get-Content -LiteralPath $launcherFile -Raw -Encoding UTF8 | ConvertFrom-Json
if ($Mode -eq 'Local') {
    if (!$LocalDll -or [IO.Path]::GetFileName($LocalDll) -ne 'NewFolder3.dll' -or !(Test-Path -LiteralPath $LocalDll -PathType Leaf)) { throw 'Provide a complete local NewFolder3.dll path.' }
    $LocalDll = [IO.Path]::GetFullPath($LocalDll)
    $manifest = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $LocalDll) 'NewFolder3.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.InternalName -ne 'NewFolder3') { throw 'Local plugin identity mismatch.' }
}
$backup = Join-Path $LauncherRoot ('backups\NewFolder3-switch-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $backup | Out-Null
foreach ($file in @($configFile,$launcherFile)) {
    if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination $backup; if((Get-FileHash -LiteralPath $file).Hash -ne (Get-FileHash -LiteralPath (Join-Path $backup (Split-Path -Leaf $file))).Hash){throw 'Backup hash mismatch.'} }
}
# All user config directories, map caches, pause checkpoints and game saves stay in place.
$locations = @($settings.DevPluginLoadLocations.'$values')
foreach ($location in $locations) { if ([IO.Path]::GetFileName($location.Path) -eq 'NewFolder3.dll') { $location.IsEnabled = $false } }
if ($Mode -eq 'Local') {
    $chosen = @($locations | Where-Object { $_.Path -eq $LocalDll })
    if($chosen.Count -gt 1){throw 'Duplicate selected development path.'}
    if($chosen.Count -eq 1){$chosen[0].IsEnabled=$true}
    else {$locations += [pscustomobject]@{ '$type'='Dalamud.Configuration.DevPluginLocationSettings, Dalamud'; Path=$LocalDll; IsEnabled=$true; Nickname=('NewFolder3 local ' + $manifest.AssemblyVersion) }}
}
$settings.DevPluginLoadLocations.'$values' = @($locations)
$selectedId = $null
if($Mode -eq 'Local') {
    $entry=$settings.DevPluginSettings.PSObject.Properties[$LocalDll]
    if(!$entry) {
        $dev=[pscustomobject]@{StartOnBoot=$true;NotifyForErrors=$true;AutomaticReloading=$false;WorkingPluginId=[guid]::NewGuid().ToString();DismissedValidationProblems=@()}
        $settings.DevPluginSettings | Add-Member -NotePropertyName $LocalDll -NotePropertyValue $dev
    } else { $dev=$entry.Value; $dev.StartOnBoot=$true }
    $selectedId=$dev.WorkingPluginId
} else {
    $official=@($versions | ForEach-Object { Get-Content -LiteralPath (Join-Path $_.FullName 'NewFolder3.json') -Raw -Encoding UTF8 | ConvertFrom-Json } | Where-Object { $_.InstalledFromUrl -like '*transparent-duck/NewFolder3/*' } | Sort-Object {[version]$_.AssemblyVersion} -Descending)
    if($official.Count -eq 0){throw 'No installed official version was found; no change applied.'}
    $selectedId=$official[0].WorkingPluginId
}
$profiles=@($settings.DefaultProfile)+@($settings.SavedProfiles.'$values')
foreach($profile in $profiles) {
    if(!$profile -or !$profile.Plugins){continue}
    foreach($entry in $profile.Plugins.'$values') {if($entry.InternalName -eq 'NewFolder3'){$entry.IsEnabled=$false}}
}
$entries=@($settings.DefaultProfile.Plugins.'$values')
$targetEntries=@($entries | Where-Object { $_.InternalName -eq 'NewFolder3' -and $_.WorkingPluginId -eq $selectedId })
if($targetEntries.Count -gt 1){throw 'Duplicate selected plugin profile identity.'}
if($targetEntries.Count -eq 1){$targetEntries[0].IsEnabled=$true}
else {$entries += [pscustomobject]@{InternalName='NewFolder3';WorkingPluginId=$selectedId;IsEnabled=$true};$settings.DefaultProfile.Plugins.'$values'=@($entries)}
# Retired .83 packages must not win the installed-version selection. Quarantine, never delete.
$moves=@()
foreach($version in $versions) {
    $m = Get-Content -LiteralPath (Join-Path $version.FullName 'NewFolder3.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if($m.InstalledFromUrl -like '*yuanzil1243-oss/DalamudPlugins*') {
        $target=Join-Path $backup ('binary-' + $version.Name)
        if([IO.Path]::GetFullPath($version.Parent.FullName) -ne [IO.Path]::GetFullPath($installed)){throw 'Unexpected binary parent.'}
        $moves += @{Source=$version.FullName;Target=$target}
    }
}
if($Mode -eq 'Official' -and $config -and $config.Fsd.Farming.Mode -in @(4,5)) { $config.Fsd.Farming.Mode=3 }
$encoding = New-Object Text.UTF8Encoding($false)
$changed=@()
try {
    foreach($move in $moves){Move-Item -LiteralPath $move.Source -Destination $move.Target}
    if($config){[IO.File]::WriteAllText($configFile,($config|ConvertTo-Json -Depth 100),$encoding);$changed+=$configFile}
    [IO.File]::WriteAllText($launcherFile,($settings|ConvertTo-Json -Depth 100),$encoding);$changed+=$launcherFile
} catch {
    foreach($file in $changed){Copy-Item -LiteralPath (Join-Path $backup (Split-Path -Leaf $file)) -Destination $file -Force}
    foreach($move in $moves){if(Test-Path -LiteralPath $move.Target){Move-Item -LiteralPath $move.Target -Destination $move.Source}}
    throw
}
$summary.Backup=$backup
$summary.QuarantinedBinaries=$moves
$summary.Note='Configuration backups are reversible. Original user-data directories were neither replaced nor deleted. Start the game and enable only the selected NewFolder3 version.'
$summary | ConvertTo-Json -Depth 6
