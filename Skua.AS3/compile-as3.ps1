[CmdletBinding()]
param(
    [string]$MxmlcPath,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Join-Path $scriptRoot 'skua'
$sourcePath = Join-Path $projectRoot 'src'
$mainSourcePath = Join-Path $sourcePath 'skua\Main.as'
$outputDirectory = Join-Path $projectRoot 'bin'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $outputPath = Join-Path $outputDirectory 'skua.swf'
}
else {
    $outputPath = [System.IO.Path]::GetFullPath($OutputPath)
}

if (-not (Test-Path -LiteralPath $mainSourcePath)) {
    throw "AS3 entry point was not found: $mainSourcePath"
}

if ([string]::IsNullOrWhiteSpace($MxmlcPath)) {
    $mxmlcCommand = Get-Command mxmlc -ErrorAction SilentlyContinue
    if ($mxmlcCommand) {
        $MxmlcPath = $mxmlcCommand.Source
    }
    elseif ($env:FLEX_HOME) {
        $candidate = Join-Path $env:FLEX_HOME 'bin\mxmlc.bat'
        if (Test-Path -LiteralPath $candidate) {
            $MxmlcPath = $candidate
        }
    }
}

if ([string]::IsNullOrWhiteSpace($MxmlcPath)) {
    $searchRoots = @(
        'C:\MoonshineSDKs',
        'C:\ApacheFlex',
        $env:ProgramFiles,
        ${env:ProgramFiles(x86)},
        $env:LOCALAPPDATA
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_) } | Select-Object -Unique

    foreach ($searchRoot in $searchRoots) {
        $foundCompiler = Get-ChildItem -LiteralPath $searchRoot -Filter 'mxmlc.bat' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($foundCompiler) {
            $MxmlcPath = $foundCompiler.FullName
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($MxmlcPath) -or -not (Test-Path -LiteralPath $MxmlcPath)) {
    throw 'Flex mxmlc was not found. Add it to PATH, set FLEX_HOME, install a Flex SDK in a common SDK location, or pass -MxmlcPath.'
}

if (-not $env:FLEX_HOME) {
    $mxmlcDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $MxmlcPath).Path
    $env:FLEX_HOME = Split-Path -Parent $mxmlcDirectory
}
if (-not $env:PLAYERGLOBAL_HOME) {
    $env:PLAYERGLOBAL_HOME = Join-Path $env:FLEX_HOME 'frameworks\libs\player'
}

$resolvedOutputDirectory = Split-Path -Parent $outputPath
New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null

& $MxmlcPath `
    -source-path $sourcePath `
    -default-size 958 550 `
    -output $outputPath `
    $mainSourcePath `
    -target-player 28.0 `
    -optimize

if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $outputPath)) {
    throw "AS3 compilation failed: $outputPath"
}

$output = Get-Item -LiteralPath $outputPath
Write-Host "AS3 game content built: $($output.FullName) ($($output.Length) bytes)" -ForegroundColor Green
exit 0
