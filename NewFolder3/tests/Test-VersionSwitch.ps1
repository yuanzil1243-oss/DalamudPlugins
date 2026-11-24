$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('nf3-switch-test-'+[guid]::NewGuid().ToString('N'))
$tool=Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\Repair-NewFolder3.ps1'
function Assert($value,$message){if(!$value){throw $message}}
# Isolated fixture only. Do not inspect or stop any real process.
function global:Get-Process { param($Name,$ErrorAction) @() }
try {
    foreach($mode in @('Official','Local')) {
        $case=Join-Path $root $mode
        New-Item -ItemType Directory -Path (Join-Path $case 'pluginConfigs\NewFolder3\DetailedMapCatalogs'),(Join-Path $case 'installedPlugins\NewFolder3\0.7.55.20'),(Join-Path $case 'installedPlugins\NewFolder3\0.7.55.83'),(Join-Path $case 'package') -Force | Out-Null
        $config=Join-Path $case 'pluginConfigs\NewFolder3.json'
        '{"Version":1,"keep":"unchanged","Fsd":{"Farming":{"Mode":4,"Preferences":{"x":{"Cycles":3}}}}}' | Set-Content -LiteralPath $config -Encoding UTF8
        'map sentinel' | Set-Content -LiteralPath (Join-Path $case 'pluginConfigs\NewFolder3\DetailedMapCatalogs\sentinel.txt')
        $settings=@{DevPluginLoadLocations=@{'$values'=@(@{Path='C:\old\NewFolder3.dll';IsEnabled=$true},@{Path='C:\other\Other.dll';IsEnabled=$true})};DevPluginSettings=@{};SavedProfiles=@{'$values'=@()};DefaultProfile=@{Plugins=@{'$values'=@(@{InternalName='NewFolder3';WorkingPluginId='11111111-1111-1111-1111-111111111111';IsEnabled=$true},@{InternalName='Other';WorkingPluginId='33333333-3333-3333-3333-333333333333';IsEnabled=$true})}}}
        $settings | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $case 'dalamudConfig.json') -Encoding UTF8
        @{InternalName='NewFolder3';AssemblyVersion='0.7.55.20';InstalledFromUrl='https://raw.githubusercontent.com/transparent-duck/NewFolder3/main/NewFolder3-release.json';WorkingPluginId='11111111-1111-1111-1111-111111111111'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $case 'installedPlugins\NewFolder3\0.7.55.20\NewFolder3.json') -Encoding UTF8
        @{InternalName='NewFolder3';AssemblyVersion='0.7.55.83';InstalledFromUrl='https://raw.githubusercontent.com/yuanzil1243-oss/DalamudPlugins/main/pluginmaster.json';WorkingPluginId='22222222-2222-2222-2222-222222222222'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $case 'installedPlugins\NewFolder3\0.7.55.83\NewFolder3.json') -Encoding UTF8
        @{InternalName='NewFolder3';AssemblyVersion='0.7.55.2001'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $case 'package\NewFolder3.json') -Encoding UTF8
        'binary sentinel' | Set-Content -LiteralPath (Join-Path $case 'package\NewFolder3.dll')
        $before=(Get-FileHash -LiteralPath $config).Hash
        $null=& $tool -Mode $mode -LauncherRoot $case -LocalDll (Join-Path $case 'package\NewFolder3.dll')
        Assert ((Get-FileHash -LiteralPath $config).Hash -eq $before) 'Inspect mutated config'
        $result=(& $tool -Mode $mode -LauncherRoot $case -LocalDll (Join-Path $case 'package\NewFolder3.dll') -Apply)|ConvertFrom-Json
        Assert ((Get-FileHash -LiteralPath (Join-Path $result.Backup 'NewFolder3.json')).Hash -eq $before) 'Backup mismatch'
        $new=Get-Content -LiteralPath $config -Raw|ConvertFrom-Json
        Assert ($new.keep -eq 'unchanged' -and $new.Fsd.Farming.Preferences.x.Cycles -eq 3) 'Unrelated settings changed'
        Assert (Test-Path -LiteralPath (Join-Path $case 'pluginConfigs\NewFolder3\DetailedMapCatalogs\sentinel.txt')) 'User data removed'
        Assert (!(Test-Path -LiteralPath (Join-Path $case 'installedPlugins\NewFolder3\0.7.55.83'))) 'Retired binary still selected'
        Assert (Test-Path -LiteralPath (Join-Path $result.Backup 'binary-0.7.55.83\NewFolder3.json')) 'Retired binary not preserved'
        $newSettings=Get-Content -LiteralPath (Join-Path $case 'dalamudConfig.json') -Raw|ConvertFrom-Json
        $enabled=@($newSettings.DevPluginLoadLocations.'$values'|Where-Object {$_.IsEnabled -and [IO.Path]::GetFileName($_.Path) -eq 'NewFolder3.dll'})
        Assert ($enabled.Count -eq $(if($mode -eq 'Local'){1}else{0})) 'Development selection invalid'
        Assert (@($newSettings.DefaultProfile.Plugins.'$values'|Where-Object {$_.InternalName -eq 'NewFolder3' -and $_.IsEnabled}).Count -eq 1) 'Profile selection invalid'
        Assert (@($newSettings.DefaultProfile.Plugins.'$values'|Where-Object {$_.InternalName -eq 'Other' -and $_.IsEnabled}).Count -eq 1) 'Other plugin changed'
        Write-Output "PASS version-switch-$mode"
    }
} finally {
    Remove-Item Function:\Get-Process -ErrorAction SilentlyContinue
    $resolved=[IO.Path]::GetFullPath($root)
    if($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) -and [IO.Path]::GetFileName($resolved).StartsWith('nf3-switch-test-') -and (Test-Path -LiteralPath $resolved)){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
