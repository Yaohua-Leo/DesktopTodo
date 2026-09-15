$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$compilerPath = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testBinary = Join-Path $PSScriptRoot 'StorageTests.exe'
& $compilerPath /nologo /target:exe /codepage:65001 /r:System.Runtime.Serialization.dll /r:System.Web.Extensions.dll "/out:$testBinary" (Join-Path $projectDirectory 'Storage.cs') (Join-Path $PSScriptRoot 'StorageTests.cs')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $testBinary
exit $LASTEXITCODE
