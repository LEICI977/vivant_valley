function New-ModArchive {
    param(
        [Parameter(Mandatory = $true)][string]$PackageDirectory,
        [Parameter(Mandatory = $true)][string]$ArchivePath
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $package = Get-Item -LiteralPath $PackageDirectory
    if (-not $package.PSIsContainer) { throw 'The mod package must be a directory.' }
    $rootName = $package.Name + '/'
    $prefixLength = $package.FullName.Length + 1
    # Windows PowerShell Compress-Archive can write backslashes into ZIP entry names.
    # Android treats those as literal filename characters. Always write '/' explicitly.
    $zip = [System.IO.Compression.ZipFile]::Open($ArchivePath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $null = $zip.CreateEntry($rootName)
        foreach ($item in (Get-ChildItem -LiteralPath $package.FullName -Recurse -Force | Sort-Object FullName)) {
            $relative = $item.FullName.Substring($prefixLength).Replace('\', '/')
            $entryName = $rootName + $relative
            if ($item.PSIsContainer) {
                $null = $zip.CreateEntry($entryName + '/')
            } else {
                $null = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $zip, $item.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
            }
        }
    } finally { $zip.Dispose() }

    $zip = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $names = @($zip.Entries | ForEach-Object { $_.FullName })
        foreach ($name in $names) {
            # Validate the stored names without normalizing them first.
            if ($name.Contains('\') -or -not $name.StartsWith($rootName, [StringComparison]::Ordinal)) {
                throw "Invalid Android ZIP entry: $name"
            }
        }
        foreach ($required in @('manifest.json', 'VivantValley.dll')) {
            if ($names -cnotcontains ($rootName + $required)) { throw "Missing mod entry: $required" }
        }
    } finally { $zip.Dispose() }
}
