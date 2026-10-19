param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'

# Use the saved app preference when available, otherwise the Windows UI language.
$lumaLanguage = [Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName
try {
    $preferenceFile = Join-Path $env:LOCALAPPDATA 'Luma\settings.json'
    if (Test-Path -LiteralPath $preferenceFile) {
        $savedLanguage = (Get-Content -LiteralPath $preferenceFile -Raw -Encoding UTF8 | ConvertFrom-Json).Language
        if ($savedLanguage -in @('tr','en','es','zh-Hans')) { $lumaLanguage = $savedLanguage }
    }
} catch { }
$lumaLanguageIndex = switch ($lumaLanguage) { 'tr' { 0 } 'es' { 2 } 'zh' { 3 } 'zh-Hans' { 3 } default { 1 } }
function Get-LumaText([string[]]$Messages) { return $Messages[$lumaLanguageIndex] }

$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceExe = Join-Path $projectRoot 'Luma.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) { $sourceExe = Join-Path $projectRoot 'dist\Luma.exe' }
if (-not (Test-Path -LiteralPath $sourceExe)) { throw (Get-LumaText @('Önce scripts\publish.ps1 komutunu çalıştır.','Run scripts\publish.ps1 first.','Ejecuta primero scripts\publish.ps1.','请先运行 scripts\publish.ps1。')) }
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\Luma'
$installedExe = Join-Path $installDirectory 'Luma.exe'
if (Test-Path -LiteralPath $installedExe) {
    Start-Process -FilePath $installedExe -ArgumentList '--quit' -WindowStyle Hidden -Wait
    $deadline = [DateTime]::Now.AddSeconds(12)
    do {
        $runningLuma = Get-Process -Name Luma -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExe }
        if ($runningLuma) { Start-Sleep -Milliseconds 250 }
    } while ($runningLuma -and [DateTime]::Now -lt $deadline)
    if ($runningLuma) { throw (Get-LumaText @('Luma kapanmadı. Tepsi menüsünden çıkış yapıp kurulumu yeniden çalıştır.','Luma is still running. Exit from the tray menu and retry installation.','Luma sigue abierto. Sal desde la bandeja y repite la instalación.','Luma 仍在运行。请从托盘菜单退出后重新安装。')) }
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
$link.Description = Get-LumaText @('Ekranın, tam kararında. Monitör ve renk kontrolü.','Your screen, just right. Monitor and color controls.','Tu pantalla, a tu gusto. Control de monitor y color.','恰到好处的屏幕。显示器与色彩控制。')
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
    DisplayVersion = '1.6.0'
    Publisher = 'Luma'
    DisplayIcon = "$installedExe,0"
    InstallLocation = $installDirectory
    UninstallString = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Join-Path $installDirectory 'uninstall.ps1') + '"'
}
foreach ($entry in $uninstallValues.GetEnumerator()) { New-ItemProperty -Path $uninstallPath -Name $entry.Key -Value $entry.Value -PropertyType String -Force | Out-Null }
New-ItemProperty -Path $uninstallPath -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallPath -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
if (-not $NoLaunch) { Start-Process -FilePath $installedExe }
Write-Output ((Get-LumaText @('Luma kuruldu: {0}','Luma installed: {0}','Luma instalado: {0}','Luma 已安装：{0}')) -f $installedExe)
Write-Output (Get-LumaText @('Masaüstü ve Başlat menüsü kısayolları hazır. Windows başlangıcı etkin.','Desktop and Start menu shortcuts are ready. Windows startup is enabled.','Accesos directos creados. Inicio con Windows activado.','桌面和开始菜单快捷方式已创建。已启用随 Windows 启动。'))
