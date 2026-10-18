param([switch]$DeleteSettings)
$ErrorActionPreference = 'Stop'
$installDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\Luma'))
$expectedDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\Luma'))
if ($installDirectory -ne $expectedDirectory -or (Split-Path -Leaf $installDirectory) -ne 'Luma') { throw 'Kurulum yolu doğrulanamadı.' }
$exe = Join-Path $installDirectory 'Luma.exe'
if (Test-Path -LiteralPath $exe) { Start-Process -FilePath $exe -ArgumentList '--quit' -WindowStyle Hidden -Wait }
$deadline = [DateTime]::Now.AddSeconds(12)
do {
    $runningLuma = Get-Process -Name Luma -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
    if ($runningLuma) { Start-Sleep -Milliseconds 250 }
} while ($runningLuma -and [DateTime]::Now -lt $deadline)
if ($runningLuma) { throw 'Önce Luma tepsi menüsünden çıkış yap.' }
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Luma' -ErrorAction SilentlyContinue
$uninstallRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Luma'
if (Test-Path -LiteralPath $uninstallRegistryPath) { Remove-Item -LiteralPath $uninstallRegistryPath -Force }
foreach ($shortcut in @((Join-Path ([Environment]::GetFolderPath('Programs')) 'Luma.lnk'), (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Luma.lnk'))) {
    if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
}
if (Test-Path -LiteralPath $installDirectory) { Remove-Item -LiteralPath $installDirectory -Recurse -Force }
if ($DeleteSettings) {
    $settingsDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Luma'))
    if ((Split-Path -Parent $settingsDirectory) -ne [IO.Path]::GetFullPath($env:LOCALAPPDATA) -or (Split-Path -Leaf $settingsDirectory) -ne 'Luma') { throw 'Ayar yolu doğrulanamadı.' }
    if (Test-Path -LiteralPath $settingsDirectory) { Remove-Item -LiteralPath $settingsDirectory -Recurse -Force }
}
Write-Output 'Luma kaldırıldı. Varsayılan olarak ayar yedeğin korunur.'
