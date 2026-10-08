$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath($PSScriptRoot)
& (Join-Path $taskRoot 'build.ps1')
$exe = Join-Path $taskRoot 'dist\CDriveCacheCleaner.exe'
$testReport = Join-Path $taskRoot 'self-test.json'
$process = Start-Process -FilePath $exe -ArgumentList '--self-test', ('"' + $testReport + '"') -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Safety tests failed: $testReport" }
$report = Get-Content -Raw -LiteralPath $testReport | ConvertFrom-Json
if (-not $report.passed) { throw 'Safety test report did not pass' }
$version = [Reflection.AssemblyName]::GetAssemblyName($exe).Version.ToString(3)
$output = Join-Path $taskRoot 'artifacts'
[IO.Directory]::CreateDirectory($output) | Out-Null
$zip = Join-Path $output "CDriveCacheCleaner-v$version-portable.zip"
Compress-Archive -LiteralPath @($exe, (Join-Path $taskRoot 'README.md'), (Join-Path $taskRoot 'LICENSE')) -DestinationPath $zip -Force
$lines = @(
    ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant() + '  CDriveCacheCleaner.exe')
    ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() + "  CDriveCacheCleaner-v$version-portable.zip")
)
[IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), $lines, [Text.UTF8Encoding]::new($false))
Write-Host "Verified portable package: $zip"
