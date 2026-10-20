param([Parameter(Mandatory)][string]$PackagePath, [switch]$VerifyPublicUrls)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'packaging/package.json') -Raw | ConvertFrom-Json
$package = [IO.Path]::GetFullPath($PackagePath)
$zip = [IO.Compression.ZipFile]::OpenRead($package)
function Assert-Package([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Read-Entry([IO.Compression.ZipArchiveEntry]$Entry) {
    $reader = [IO.StreamReader]::new($Entry.Open())
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}
try {
    $nuspecEntries = @($zip.Entries | Where-Object FullName -like '*.nuspec')
    Assert-Package ($nuspecEntries.Count -eq 1) 'Expected one NuGet manifest.'
    [xml]$manifest = Read-Entry $nuspecEntries[0]
    $metadata = $manifest.package.metadata
    Assert-Package ($metadata.id -ceq $config.id -and $metadata.authors -ceq $config.authors) 'Package ID or author mismatch.'
    Assert-Package ($metadata.projectUrl -ceq $config.projectUrl -and $metadata.iconUrl -ceq $config.iconUrl) 'Public URLs mismatch.'
    Assert-Package ($metadata.license.type -eq 'expression' -and $metadata.license.InnerText -eq 'MIT') 'Expected MIT license metadata.'
    Assert-Package ($metadata.repository.url -ceq $config.repositoryUrl) 'Repository metadata mismatch.'
    $dependencies = @($metadata.dependencies.group.dependency)
    $minimumRanges = @($config.minimumXrmToolBoxVersion, "[$($config.minimumXrmToolBoxVersion),)")
    Assert-Package ($dependencies.Count -eq 1 -and $dependencies[0].id -ceq 'XrmToolBox' -and $dependencies[0].version -in $minimumRanges) 'Expected only the XrmToolBox host compatibility dependency.'
    Assert-Package ($metadata.tags -match '\bXrmToolBox\b') 'Missing XrmToolBox discovery tag.'
    foreach ($name in @('lib/net48/Plugins/BulkStepBuilder.dll','icon.png','README.md','LICENSE','THIRD-PARTY-NOTICES.txt','lib/net48/Plugins/BulkStepBuilder/LICENSE','lib/net48/Plugins/BulkStepBuilder/THIRD-PARTY-NOTICES.txt')) {
        Assert-Package ($null -ne $zip.GetEntry($name)) "Missing package entry: $name"
    }
    Assert-Package ($metadata.icon -eq 'icon.png' -and $metadata.readme -eq 'README.md') 'Embedded icon or README reference mismatch.'
    foreach ($licenseFile in @('LICENSE','THIRD-PARTY-NOTICES.txt')) {
        $expected = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $licenseFile))
        Assert-Package ((Read-Entry $zip.GetEntry($licenseFile)) -ceq $expected) "License file content mismatch: $licenseFile"
        Assert-Package ((Read-Entry $zip.GetEntry('lib/net48/Plugins/BulkStepBuilder/' + $licenseFile)) -ceq $expected) "Installed license content mismatch: $licenseFile"
    }
    Assert-Package ($zip.GetEntry('icon.png').Length -lt 1MB) 'Package icon exceeds NuGet size limit.'
    Assert-Package (@($zip.Entries | Where-Object FullName -like '*.dll').Count -eq 1) 'Package must contain only the merged tool DLL; host and test DLLs must not ship.'
    Assert-Package (@($zip.Entries | Where-Object FullName -match '(?i)(^|/)(bin|obj|tests|install-backups|\.git)(/|$)|\.(snk|pdb|cs|ps1)$').Count -eq 0) 'Unexpected development or backup files in package.'
    $stream = $zip.GetEntry('lib/net48/Plugins/BulkStepBuilder.dll').Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') } finally { $stream.Dispose(); $sha.Dispose() }
    $dll = Join-Path $PSScriptRoot 'dist/Plugins/BulkStepBuilder.dll'
    Assert-Package ($hash -eq (Get-FileHash -LiteralPath $dll).Hash) 'Packaged DLL differs from the validated release DLL.'
    $identity = [Reflection.AssemblyName]::GetAssemblyName($dll)
    $packageVersion = [version]$metadata.version
    Assert-Package ($packageVersion.Major -eq $identity.Version.Major -and $packageVersion.Minor -eq $identity.Version.Minor -and $packageVersion.Build -eq $identity.Version.Build -and [Math]::Max(0,$packageVersion.Revision) -eq $identity.Version.Revision) 'Package and assembly versions do not match.'
    $urlsVerified = $false
    if ($VerifyPublicUrls) {
        $page = Invoke-WebRequest -Uri $config.projectUrl -TimeoutSec 30 -UseBasicParsing
        Assert-Package ($page.StatusCode -eq 200) 'Project page must be publicly accessible.'
        $icon = Invoke-WebRequest -Uri $config.iconUrl -TimeoutSec 30 -UseBasicParsing
        Assert-Package ($icon.StatusCode -eq 200 -and $icon.Headers['Content-Type'] -match '^image/(png|jpeg)') 'Icon URL must return a public image.'
        $urlsVerified = $true
    }
    $report = [ordered]@{
        PackageId = [string]$metadata.id
        Version = [string]$metadata.version
        AssemblyVersion = $identity.Version.ToString()
        Author = [string]$metadata.authors
        License = 'MIT'
        ProjectUrl = [string]$metadata.projectUrl
        IconUrl = [string]$metadata.iconUrl
        HostDependency = 'XrmToolBox >= ' + $config.minimumXrmToolBoxVersion
        PackageStructurePassed = $true
        PublicUrlsVerified = $urlsVerified
        PackagedDllSha256 = $hash
        PackageSha256 = (Get-FileHash -LiteralPath $package).Hash
        RealDataverseRegistrationValidation = 'Pending; simulated service tests do not establish real-environment validation.'
        PublicationStatus = 'Prepared locally; not uploaded to NuGet or submitted to XrmToolBox.'
    }
    $reportPath = [IO.Path]::ChangeExtension($package, '.validation.json')
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    Write-Host "PASS: package structure, identity, licenses, payload and dependency checked. Public URLs verified: $urlsVerified"
    Write-Host "Validation report: $reportPath"
} finally { $zip.Dispose() }
