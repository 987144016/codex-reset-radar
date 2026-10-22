param(
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$sourceRoot = $PSScriptRoot
$outputPath = Join-Path $sourceRoot 'CodexResetRadar.exe'
$compilerCandidates = @(
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $compiler) {
    throw 'Built-in .NET Framework C# compiler was not found.'
}

if ($Clean -and (Test-Path -LiteralPath $outputPath)) {
    [System.IO.File]::Delete($outputPath)
}

& $compiler `
    /nologo `
    /codepage:65001 `
    /target:winexe `
    /optimize+ `
    /platform:anycpu `
    "/out:$outputPath" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    (Join-Path $sourceRoot 'Program.cs')

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

$file = Get-Item -LiteralPath $outputPath
Write-Output ("BUILD PASS: {0} ({1} bytes)" -f $file.FullName, $file.Length)
