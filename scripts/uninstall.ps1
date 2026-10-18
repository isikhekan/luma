param([switch]$DeleteSettings)
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

$installDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\Luma'))
$expectedDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\Luma'))
if ($installDirectory -ne $expectedDirectory -or (Split-Path -Leaf $installDirectory) -ne 'Luma') { throw (Get-LumaText @('Kurulum yolu doğrulanamadı.','Installation path could not be verified.','No se pudo verificar la ruta de instalación.','无法验证安装路径。')) }
$exe = Join-Path $installDirectory 'Luma.exe'
if (Test-Path -LiteralPath $exe) { Start-Process -FilePath $exe -ArgumentList '--quit' -WindowStyle Hidden -Wait }
$deadline = [DateTime]::Now.AddSeconds(12)
do {
    $runningLuma = Get-Process -Name Luma -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
    if ($runningLuma) { Start-Sleep -Milliseconds 250 }
} while ($runningLuma -and [DateTime]::Now -lt $deadline)
if ($runningLuma) { throw (Get-LumaText @('Önce Luma tepsi menüsünden çıkış yap.','Exit Luma from the tray menu first.','Sal primero de Luma desde la bandeja.','请先从托盘菜单退出 Luma。')) }
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Luma' -ErrorAction SilentlyContinue
$uninstallRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Luma'
if (Test-Path -LiteralPath $uninstallRegistryPath) { Remove-Item -LiteralPath $uninstallRegistryPath -Force }
foreach ($shortcut in @((Join-Path ([Environment]::GetFolderPath('Programs')) 'Luma.lnk'), (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Luma.lnk'))) {
    if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
}
if (Test-Path -LiteralPath $installDirectory) { Remove-Item -LiteralPath $installDirectory -Recurse -Force }
if ($DeleteSettings) {
    $settingsDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Luma'))
    if ((Split-Path -Parent $settingsDirectory) -ne [IO.Path]::GetFullPath($env:LOCALAPPDATA) -or (Split-Path -Leaf $settingsDirectory) -ne 'Luma') { throw (Get-LumaText @('Ayar yolu doğrulanamadı.','Settings path could not be verified.','No se pudo verificar la ruta de ajustes.','无法验证设置路径。')) }
    if (Test-Path -LiteralPath $settingsDirectory) { Remove-Item -LiteralPath $settingsDirectory -Recurse -Force }
}
Write-Output (Get-LumaText @('Luma kaldırıldı. Varsayılan olarak ayar yedeğin korunur.','Luma uninstalled. Settings are preserved by default.','Luma desinstalado. Los ajustes se conservan por defecto.','Luma 已卸载。默认保留设置。'))
