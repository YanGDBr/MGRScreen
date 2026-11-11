[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$FfmpegDirectory,
    [Parameter(Mandatory)][string]$SourceArchives,
    [Parameter(Mandatory)][string]$InnoCompiler,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$app = (Resolve-Path -LiteralPath $AppDirectory).Path
$ffmpeg = (Resolve-Path -LiteralPath $FfmpegDirectory).Path
$sources = (Resolve-Path -LiteralPath $SourceArchives).Path
$compiler = (Resolve-Path -LiteralPath $InnoCompiler).Path
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$version = (Get-Item -LiteralPath (Join-Path $app 'MGRScreen.exe')).VersionInfo
$appVersion = '{0}.{1}.{2}' -f $version.FileMajorPart, $version.FileMinorPart, $version.FileBuildPart
$binaryName = "MGRScreen-$appVersion-windows-x64"
$sourceName = "MGRScreen-$appVersion-ffmpeg-source"

foreach ($required in @('hostfxr.dll', 'coreclr.dll', 'MGRScreen.dll', 'licenses/Windows-SDK-Terms.rtf')) {
    if (!(Test-Path -LiteralPath (Join-Path $app $required) -PathType Leaf)) {
        throw "A publicação autossuficiente está incompleta: $required."
    }
}

$ffmpegBuildFiles = @('ffmpeg.exe', 'LICENSE.txt', 'FFmpeg-COPYING.GPLv2', 'x264-COPYING',
    'nv-codec-headers-LICENSE', 'AMF-LICENSE', 'libvpl-LICENSE', 'TOOLCHAIN.txt', 'GCC-COPYING3',
    'GCC-COPYING.RUNTIME', 'MinGW-w64-runtime-LICENSE', 'winpthreads-COPYING')
foreach ($required in $ffmpegBuildFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $ffmpeg $required) -PathType Leaf)) {
        throw "A compilação do FFmpeg está incompleta: $required."
    }
}

if (Test-Path -LiteralPath $output) {
    throw 'Informe uma pasta de saída nova.'
}
New-Item -ItemType Directory -Path $output | Out-Null
$package = (New-Item -ItemType Directory -Path (Join-Path $output 'portable')).FullName
$sourcePackage = (New-Item -ItemType Directory -Path (Join-Path $output 'ffmpeg-source')).FullName

Get-ChildItem -LiteralPath $app -Force |
    Where-Object { $_.Name -ne 'ffmpeg' -and $_.Extension -ne '.pdb' } |
    Copy-Item -Destination $package -Recurse
$ffmpegPackage = (New-Item -ItemType Directory -Path (Join-Path $package 'ffmpeg')).FullName
foreach ($file in $ffmpegBuildFiles) {
    Copy-Item -LiteralPath (Join-Path $ffmpeg $file) -Destination $ffmpegPackage
}
Copy-Item -LiteralPath (Join-Path $repository 'LICENSE'), (Join-Path $repository 'THIRD_PARTY_NOTICES.md') -Destination $package

$manifest = Join-Path $PSScriptRoot 'ffmpeg/SHA256SUMS'
foreach ($line in Get-Content -LiteralPath $manifest) {
    $hash, $name = $line -split '\s+', 2
    $archive = Join-Path $sources $name
    if ((Get-FileHash -LiteralPath $archive).Hash -ne $hash) {
        throw "SHA-256 incorreto para $name."
    }
    Copy-Item -LiteralPath $archive -Destination $sourcePackage
}
Copy-Item -LiteralPath $manifest, (Join-Path $PSScriptRoot 'ffmpeg/build.sh'),
    (Join-Path $PSScriptRoot 'ffmpeg/README.md'), (Join-Path $ffmpeg 'TOOLCHAIN.txt') -Destination $sourcePackage

& $compiler '/Qp' "/DAppVersion=$appVersion" "/DPackageDir=$package" "/DOutputDir=$output" (Join-Path $PSScriptRoot 'MGRScreen.iss')
if ($LASTEXITCODE -ne 0) {
    throw "Falha do Inno Setup ($LASTEXITCODE)."
}

Compress-Archive -Path (Join-Path $package '*') -DestinationPath (Join-Path $output "$binaryName.zip")
Compress-Archive -Path (Join-Path $sourcePackage '*') -DestinationPath (Join-Path $output "$sourceName.zip")
Get-ChildItem -LiteralPath $output -File | Get-FileHash -Algorithm SHA256
