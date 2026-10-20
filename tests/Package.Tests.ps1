param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path $PSScriptRoot -Parent
$directory = Join-Path $root 'packaging/obj/package-regressions'
New-Item -ItemType Directory -Force $directory | Out-Null
$cases = @(
    @{ Name = 'ExtraDll'; Expected = 'only the merged tool DLL' },
    @{ Name = 'WrongVersion'; Expected = 'versions do not match' },
    @{ Name = 'WrongDependency'; Expected = 'host compatibility dependency' },
    @{ Name = 'MissingLicense'; Expected = 'Missing package entry: LICENSE' },
    @{ Name = 'ChangedDll'; Expected = 'differs from the validated release DLL' }
)
foreach ($case in $cases) {
    $copy = Join-Path $directory ($case.Name + '.nupkg')
    Copy-Item -LiteralPath $PackagePath -Destination $copy -Force
    $zip = [IO.Compression.ZipFile]::Open($copy, [IO.Compression.ZipArchiveMode]::Update)
    try {
        switch ($case.Name) {
            'ExtraDll' {
                $writer = [IO.StreamWriter]::new($zip.CreateEntry('lib/net48/Plugins/Unexpected.dll').Open())
                try { $writer.Write('Unexpected payload') } finally { $writer.Dispose() }
            }
            'WrongVersion' { }
            'WrongDependency' { }
            'MissingLicense' { $zip.GetEntry('LICENSE').Delete() }
            'ChangedDll' {
                $entry = $zip.GetEntry('lib/net48/Plugins/BulkStepBuilder.dll')
                $memory = [IO.MemoryStream]::new(); $source = $entry.Open()
                try { $source.CopyTo($memory); $bytes = $memory.ToArray() } finally { $source.Dispose(); $memory.Dispose() }
                $entry.Delete(); $bytes[0] = $bytes[0] -bxor 1
                $target = $zip.CreateEntry('lib/net48/Plugins/BulkStepBuilder.dll').Open()
                try { $target.Write($bytes,0,$bytes.Length) } finally { $target.Dispose() }
            }
        }
        if ($case.Name -in @('WrongVersion','WrongDependency')) {
            $entry = $zip.Entries | Where-Object FullName -like '*.nuspec'
            $name = $entry.FullName
            $reader = [IO.StreamReader]::new($entry.Open())
            try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $entry.Delete()
            if ($case.Name -eq 'WrongVersion') { $text = [regex]::Replace($text,'<version>[^<]+</version>','<version>99.0.0</version>') }
            else { $text = $text.Replace('id="XrmToolBox"','id="XrmToolBoxPackage"') }
            $writer = [IO.StreamWriter]::new($zip.CreateEntry($name).Open())
            try { $writer.Write($text) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
    $rejected = $false
    try { & (Join-Path $root 'Test-Package.ps1') -PackagePath $copy }
    catch {
        if (-not $_.Exception.Message.Contains($case.Expected)) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Package validator accepted invalid package: $($case.Name)" }
    Write-Host "PASS: rejects $($case.Name)"
}
