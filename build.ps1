param([string]$OutputDirectory = 'dist')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $compiler = Join-Path $framework 'csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x compiler was not found.' }
$buildDirectory = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }
New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
$outputFile = Join-Path $buildDirectory 'DesktopTodo.exe'
$arguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', '/codepage:65001', "/out:$outputFile", "/win32manifest:$(Join-Path $projectRoot 'app.manifest')", "/resource:$(Join-Path $projectRoot 'MainWindow.xaml'),MainWindow.xaml", '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Runtime.Serialization.dll', '/reference:System.Xaml.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', "/reference:$(Join-Path $framework 'WPF\WindowsBase.dll')", "/reference:$(Join-Path $framework 'WPF\PresentationCore.dll')", "/reference:$(Join-Path $framework 'WPF\PresentationFramework.dll')")
$icon = Join-Path $projectRoot 'App.ico'
$arguments += '/reference:System.Web.Extensions.dll'
if (Test-Path -LiteralPath $icon) { $arguments += "/win32icon:$icon"; $arguments += "/resource:$icon,App.ico" }
$arguments += @((Join-Path $projectRoot 'App.cs'), (Join-Path $projectRoot 'Storage.cs'))
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Compilation failed ($LASTEXITCODE)." }
$guide = Join-Path $projectRoot '使用说明.txt'
if (Test-Path -LiteralPath $guide) { Copy-Item -LiteralPath $guide -Destination $buildDirectory -Force }
Write-Output "Built: $outputFile"
