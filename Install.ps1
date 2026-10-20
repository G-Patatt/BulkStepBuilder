param(
    [string]$PluginsPath = (Join-Path $env:APPDATA 'MscrmTools/XrmToolBox/Plugins')
)
$ErrorActionPreference = 'Stop'

# Replacing a loaded DLL lets the host cache old metadata with the new on-disk version.
if (Get-Process -Name XrmToolBox -ErrorAction SilentlyContinue) {
    throw 'Close all XrmToolBox windows before installing. The running host can retain the previous assembly and write stale catalog metadata.'
}
$source = Join-Path $PSScriptRoot 'dist/Plugins/BulkStepBuilder.dll'
if (-not (Test-Path -LiteralPath $source)) { throw 'Release DLL not found. Run build.ps1 first.' }
if (-not (Test-Path -LiteralPath $PluginsPath -PathType Container)) { throw 'XrmToolBox Plugins folder not found. Specify -PluginsPath for your installation.' }
$identity = [System.Reflection.AssemblyName]::GetAssemblyName($source)
$version = $identity.Version.ToString()
$author = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($source).CompanyName
$destination = Join-Path $PluginsPath 'BulkStepBuilder.dll'
$manifestPath = Join-Path $PluginsPath 'manifest.json'
$backupPath = Join-Path $PSScriptRoot ('install-backups/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Force $backupPath | Out-Null
if (Test-Path -LiteralPath $destination) { Copy-Item -LiteralPath $destination -Destination (Join-Path $backupPath 'BulkStepBuilder.dll.bak') }
if (Test-Path -LiteralPath $manifestPath) { Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $backupPath 'manifest.json.bak') }
Copy-Item -LiteralPath $source -Destination $destination -Force
if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) { throw 'Installed DLL verification failed.' }

if (Test-Path -LiteralPath $manifestPath) {
    $original = [System.IO.File]::ReadAllText($manifestPath)
    $manifest = $original | ConvertFrom-Json
    $entries = @($manifest.PluginMetadata | Where-Object { $_.PluginType -eq 'BulkStepBuilder.Plugin' })
    if ($entries.Count -eq 1) {
        $entry = $entries[0]
        $entry.Version = $version
        $entry.AssemblyQualifiedName = $identity.FullName
        $entry.AssemblyFilename = [System.IO.Path]::GetFullPath($destination)
        $entry.Company = $author
        $entry.Description = 'Register multiple steps for one plugin class: local DLL, tables, messages and stages.'
        foreach ($scan in @($manifest.ScannedAssemblies)) {
            if ([System.IO.Path]::GetFileName($scan.Name) -ieq 'BulkStepBuilder.dll') { $scan.Version = $version }
        }
    } else {
        # A first installation has no metadata entry yet; let the host discover it.
        $manifest.ScannedAssemblies = @($manifest.ScannedAssemblies | Where-Object { [System.IO.Path]::GetFileName($_.Name) -ine 'BulkStepBuilder.dll' })
    }
    if ([System.IO.File]::ReadAllText($manifestPath) -ne $original) { throw 'The catalog changed during installation. Close XrmToolBox and run Install.ps1 again.' }
    $temporary = Join-Path $PluginsPath 'manifest.bulkstep-install.tmp'
    [System.IO.File]::WriteAllText($temporary, ($manifest | ConvertTo-Json -Depth 100 -Compress), (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporary -Destination $manifestPath -Force
    $verified = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($entry in @($verified.PluginMetadata | Where-Object { $_.PluginType -eq 'BulkStepBuilder.Plugin' })) {
        if ($entry.Version -ne $version -or $entry.AssemblyQualifiedName -ne $identity.FullName) { throw 'Catalog verification failed.' }
    }
}
Write-Host "Installed Bulk Step Builder $version by $author. You can now open XrmToolBox."
