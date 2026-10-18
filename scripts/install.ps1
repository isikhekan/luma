param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceExe = Join-Path $projectRoot 'Luma.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) { $sourceExe = Join-Path $projectRoot 'dist\Luma.exe' }
if (-not (Test-Path -LiteralPath $sourceExe)) { throw 'Önce scripts\publish.ps1 komutunu çalıştır.' }
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\Luma'
$installedExe = Join-Path $installDirectory 'Luma.exe'
if (Test-Path -LiteralPath $installedExe) {
    Start-Process -FilePath $installedExe -ArgumentList '--quit' -WindowStyle Hidden -Wait
    $deadline = [DateTime]::Now.AddSeconds(12)
    do {
        $runningLuma = Get-Process -Name Luma -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExe }
        if ($runningLuma) { Start-Sleep -Milliseconds 250 }
    } while ($runningLuma -and [DateTime]::Now -lt $deadline)
    if ($runningLuma) { throw 'Luma kapanmadı. Tepsi menüsünden çıkış yapıp kurulumu yeniden çalıştır.' }
}
New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item -LiteralPath $sourceExe -Destination $installedExe -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $installDirectory 'Kullanim.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination (Join-Path $installDirectory 'uninstall.ps1') -Force
$shortcutShell = New-Object -ComObject WScript.Shell
$startShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Luma.lnk'
$link = $shortcutShell.CreateShortcut($startShortcut)
$link.TargetPath = $installedExe
$link.WorkingDirectory = $installDirectory
$link.IconLocation = "$installedExe,0"
$link.Description = 'Ekranın, tam kararında. Monitör ve renk kontrolü.'
$link.Save()
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Luma.lnk'
$link = $shortcutShell.CreateShortcut($desktopShortcut)
$link.TargetPath = $installedExe
$link.WorkingDirectory = $installDirectory
$link.IconLocation = "$installedExe,0"
$link.Save()
$runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-Item -Path $runPath -Force | Out-Null
New-ItemProperty -Path $runPath -Name 'Luma' -Value ('"' + $installedExe + '" --tray') -PropertyType String -Force | Out-Null
$uninstallPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Luma'
New-Item -Path $uninstallPath -Force | Out-Null
$uninstallValues = @{
    DisplayName = 'Luma'
    DisplayVersion = '1.4.0'
    Publisher = 'Luma'
    DisplayIcon = "$installedExe,0"
    InstallLocation = $installDirectory
    UninstallString = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Join-Path $installDirectory 'uninstall.ps1') + '"'
}
foreach ($entry in $uninstallValues.GetEnumerator()) { New-ItemProperty -Path $uninstallPath -Name $entry.Key -Value $entry.Value -PropertyType String -Force | Out-Null }
New-ItemProperty -Path $uninstallPath -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallPath -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
if (-not $NoLaunch) { Start-Process -FilePath $installedExe }
Write-Output "Luma kuruldu: $installedExe"
Write-Output 'Masaüstü ve Başlat menüsü kısayolları hazır. Windows başlangıcı etkin.'
