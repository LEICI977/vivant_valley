param([string]$GamePath = '')

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'VivantValley.csproj'
$testProject = Join-Path $projectRoot 'tests\ConversationEngineSmoke\ConversationEngineSmoke.csproj'
$gameArguments = @()
if (-not [string]::IsNullOrWhiteSpace($GamePath)) { $gameArguments += "/p:GamePath=$GamePath" }

& dotnet build $project -c Release --nologo @gameArguments
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
& dotnet run --project $testProject -c Release @gameArguments
if ($LASTEXITCODE -ne 0) { throw 'Offline regression tests failed.' }

$version = (Get-Content -LiteralPath (Join-Path $projectRoot 'manifest.json') -Raw | ConvertFrom-Json).Version
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$packageName = "VivantValley-Android-$version-$stamp"
$stageRoot = Join-Path $projectRoot "dist\$packageName"
$packageRoot = Join-Path $stageRoot 'VivantValley'
$archive = Join-Path $projectRoot "dist\$packageName.zip"
if ((Test-Path -LiteralPath $stageRoot) -or (Test-Path -LiteralPath $archive)) { throw 'Preview output already exists.' }
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
$buildRoot = Join-Path $projectRoot 'bin\Release\net6.0'
foreach ($name in @('VivantValley.dll','VivantValley.pdb','manifest.json')) {
    Copy-Item -LiteralPath (Join-Path $buildRoot $name) -Destination $packageRoot
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'ANDROID.md') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'i18n') -Destination $packageRoot -Recurse
$assets = Join-Path $packageRoot 'assets'
New-Item -ItemType Directory -Path $assets | Out-Null
foreach ($name in @('social','stories','share','dreams')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "assets\$name") -Destination $assets -Recurse
}
$prompts = Join-Path $assets 'prompts'
New-Item -ItemType Directory -Path $prompts | Out-Null
foreach ($name in @('zh','en','conversation-context.json','README.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "assets\prompts\$name") -Destination $prompts -Recurse
}
. (Join-Path $PSScriptRoot 'mod-archive.ps1')
New-ModArchive -PackageDirectory $packageRoot -ArchivePath $archive
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($entry in $zip.Entries) {
        if ($entry.Name -eq 'config.json' -or $entry.Name -match '\.(exe|py|pyc)$') { throw "Unexpected package entry: $($entry.FullName)" }
    }
} finally { $zip.Dispose() }
Write-Output "Android preview package: $archive"
Write-Output 'Offline validation passed. Android device validation is still required; see ANDROID.md.'
