$ErrorActionPreference = 'Stop'
try { & (Join-Path $PSScriptRoot 'install.ps1') }
catch {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Luma kurulumu', 'OK', 'Error') | Out-Null
    exit 1
}
