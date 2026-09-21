$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$sourcePath = Join-Path $projectRoot 'Program.cs'
$outputDir = Join-Path $projectRoot 'dist'
$outputPath = Join-Path $outputDir 'CDriveCacheCleaner.exe'

if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Source file not found: $sourcePath"
}

[IO.Directory]::CreateDirectory($outputDir) | Out-Null
if (Test-Path -LiteralPath $outputPath -PathType Leaf) {
    [IO.File]::Delete($outputPath)
}

$references = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll'
)

Add-Type -Path $sourcePath `
    -ReferencedAssemblies $references `
    -OutputAssembly $outputPath `
    -OutputType WindowsApplication

if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
    throw 'Compiler did not produce the EXE.'
}

$size = (Get-Item -LiteralPath $outputPath).Length
Write-Host "Build complete: $outputPath"
Write-Host ("File size: {0:N0} bytes" -f $size)
