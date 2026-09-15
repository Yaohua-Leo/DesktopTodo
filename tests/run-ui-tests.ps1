param([string]$AppBinary = '')
$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkDirectory 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $compilerPath = Join-Path $frameworkDirectory 'csc.exe'
}
if ([string]::IsNullOrWhiteSpace($AppBinary)) { $AppBinary = Join-Path $projectDirectory 'dist\DesktopTodo.exe' }
$appBinary = [IO.Path]::GetFullPath($AppBinary)
if (-not (Test-Path -LiteralPath $appBinary)) { throw 'Build the application with build.ps1 first.' }
$testOutputDirectory = Join-Path $PSScriptRoot 'bin'
$testDataDirectory = Join-Path $PSScriptRoot 'data'
New-Item -ItemType Directory -Path $testOutputDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $testDataDirectory -Force | Out-Null
Copy-Item -LiteralPath $appBinary -Destination (Join-Path $testOutputDirectory 'DesktopTodo.exe') -Force
$testBinary = Join-Path $testOutputDirectory 'UiTests.exe'
$compilerArguments = @('/nologo', '/target:exe', '/main:UiTests', '/utf8output', '/codepage:65001', "/out:$testBinary", "/reference:$appBinary", '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Xaml.dll', '/reference:System.Runtime.Serialization.dll', "/reference:$(Join-Path $frameworkDirectory 'WPF\WindowsBase.dll')", "/reference:$(Join-Path $frameworkDirectory 'WPF\PresentationCore.dll')", "/reference:$(Join-Path $frameworkDirectory 'WPF\PresentationFramework.dll')", (Join-Path $PSScriptRoot 'UiTests.cs'))
& $compilerPath @compilerArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $testBinary $testDataDirectory
exit $LASTEXITCODE
