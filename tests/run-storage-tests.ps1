$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$compilerPath = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testBinary = Join-Path $PSScriptRoot 'StorageTests.exe'
& $compilerPath /nologo /target:exe /codepage:65001 /r:System.Core.dll /r:System.Runtime.Serialization.dll /r:System.Web.Extensions.dll "/out:$testBinary" (Join-Path $projectDirectory 'Storage.cs') (Join-Path $projectDirectory 'Logic.cs') (Join-Path $projectDirectory 'Localization.cs') (Join-Path $projectDirectory 'Holidays.cs') (Join-Path $projectDirectory 'Theme.cs') (Join-Path $PSScriptRoot 'StorageTests.cs')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $testBinary
exit $LASTEXITCODE
