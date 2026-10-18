$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
dotnet publish (Join-Path $projectRoot 'Luma\Luma.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $projectRoot 'dist')
if ($LASTEXITCODE -ne 0) { throw 'Derleme başarısız.' }
Write-Output 'Hazır: dist\Luma.exe (ayrı .NET kurulumu gerektirmez).'
