$ErrorActionPreference = 'Stop'
try { & (Join-Path $PSScriptRoot 'install.ps1') }
catch {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, $(switch ([Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName) { 'tr' { 'Luma kurulumu' } 'es' { 'Instalación de Luma' } 'zh' { 'Luma 安装' } default { 'Luma setup' } }), 'OK', 'Error') | Out-Null
    exit 1
}
