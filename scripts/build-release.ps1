param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Push-Location $projectRoot
try {
    if (-not $SkipTests) {
        dotnet run --project Luma.Checks -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
    }
    & (Join-Path $PSScriptRoot 'publish.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $releaseDirectory = [IO.Path]::GetFullPath((Join-Path $projectRoot 'release'))
    $packageDirectory = [IO.Path]::GetFullPath((Join-Path $releaseDirectory 'package'))
    if ((Split-Path -Parent $packageDirectory) -ne $releaseDirectory -or (Split-Path -Leaf $packageDirectory) -ne 'package') { throw 'Invalid package directory.' }
    if (Test-Path -LiteralPath $packageDirectory) { Remove-Item -LiteralPath $packageDirectory -Recurse -Force }
    New-Item -ItemType Directory -Path (Join-Path $packageDirectory 'scripts') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'dist\Luma.exe') -Destination (Join-Path $packageDirectory 'Luma.exe')
    foreach ($name in 'README.md','LICENSE','KUR.cmd') { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination (Join-Path $packageDirectory $name) }
    foreach ($name in 'install.ps1','uninstall.ps1','setup.ps1') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $packageDirectory 'scripts') }
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $runtime = Get-ChildItem -LiteralPath (Join-Path $nugetRoot 'microsoft.netcore.app.runtime.win-x64') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    $desktop = Get-ChildItem -LiteralPath (Join-Path $nugetRoot 'microsoft.windowsdesktop.app.runtime.win-x64') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    Copy-Item -LiteralPath (Join-Path $runtime.FullName 'LICENSE.TXT') -Destination (Join-Path $packageDirectory 'DOTNET-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $runtime.FullName 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $packageDirectory 'DOTNET-THIRD-PARTY-NOTICES.txt')
    Copy-Item -LiteralPath (Join-Path $desktop.FullName 'LICENSE') -Destination (Join-Path $packageDirectory 'WINDOWS-DESKTOP-LICENSE.txt')
    $archive = Join-Path $releaseDirectory 'Luma-win-x64.zip'
    Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $packageDirectory | Select-Object -ExpandProperty FullName) -DestinationPath $archive -CompressionLevel Optimal -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $releaseDirectory 'SHA256SUMS.txt'), $hash + '  Luma-win-x64.zip' + [Environment]::NewLine, [Text.Encoding]::ASCII)
    Write-Output ('Release package: ' + $archive)
}
finally { Pop-Location }
