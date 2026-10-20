param([switch]$SkipBuild, [switch]$VerifyPublicUrls)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Test
    if ($LASTEXITCODE -ne 0) { throw 'Build or tests failed.' }
}
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'packaging/package.json') -Raw | ConvertFrom-Json
foreach ($field in @('id','title','authors','projectUrl','repositoryUrl','iconUrl','licenseExpression','minimumXrmToolBoxVersion','description','tags','releaseNotes')) {
    if ([string]::IsNullOrWhiteSpace($config.$field)) { throw "Missing package metadata: $field" }
}
foreach ($field in @('projectUrl','repositoryUrl','iconUrl')) {
    $uri = [uri]$config.$field
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne 'https') { throw "$field must be an absolute HTTPS URL." }
}
if ($config.licenseExpression -ne 'MIT') { throw 'The configured license must match the approved MIT license.' }
$dll = Join-Path $PSScriptRoot 'dist/Plugins/BulkStepBuilder.dll'
$identity = [Reflection.AssemblyName]::GetAssemblyName($dll)
$version = if ($identity.Version.Revision -eq 0) { $identity.Version.ToString(3) } else { $identity.Version.ToString() }
$stage = Join-Path $PSScriptRoot 'packaging/obj/stage'
$destination = Join-Path $PSScriptRoot 'dist/nuget'
New-Item -ItemType Directory -Force $stage,$destination | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'packaging/README.md') -Destination (Join-Path $stage 'README.md') -Force

function Escape-Xml([string]$Value) { [Security.SecurityElement]::Escape($Value) }
$commit = ''
$gitRoot = & git -C $PSScriptRoot rev-parse --show-toplevel 2>$null
if ($LASTEXITCODE -eq 0 -and [IO.Path]::GetFullPath($gitRoot).TrimEnd('\','/') -eq [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\','/')) {
    $gitStatus = @(& git -C $PSScriptRoot status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -eq 0 -and $gitStatus.Count -eq 0) { $commit = & git -C $PSScriptRoot rev-parse HEAD }
}
$repository = '<repository type="git" url="' + (Escape-Xml $config.repositoryUrl) + '"' + $(if ($commit) { ' commit="' + $commit + '"' }) + ' />'
$nuspec = @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>$(Escape-Xml $config.id)</id>
    <version>$version</version>
    <title>$(Escape-Xml $config.title)</title>
    <authors>$(Escape-Xml $config.authors)</authors>
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
    <license type="expression">MIT</license>
    <icon>icon.png</icon>
    <iconUrl>$(Escape-Xml $config.iconUrl)</iconUrl>
    <projectUrl>$(Escape-Xml $config.projectUrl)</projectUrl>
    <readme>README.md</readme>
    <description>$(Escape-Xml $config.description)</description>
    <releaseNotes>$(Escape-Xml $config.releaseNotes)</releaseNotes>
    <copyright>Copyright 2026 Gabriel Patatt</copyright>
    <tags>$(Escape-Xml $config.tags)</tags>
    $repository
    <dependencies>
      <group targetFramework=".NETFramework4.8">
        <dependency id="XrmToolBox" version="[$($config.minimumXrmToolBoxVersion),)" />
      </group>
    </dependencies>
  </metadata>
  <files>
    <file src="$(Escape-Xml $dll)" target="lib/net48/Plugins/BulkStepBuilder.dll" />
    <file src="$(Escape-Xml (Join-Path $PSScriptRoot 'icon.png'))" target="icon.png" />
    <file src="$(Escape-Xml (Join-Path $stage 'README.md'))" target="README.md" />
    <file src="$(Escape-Xml (Join-Path $PSScriptRoot 'LICENSE'))" target="LICENSE" />
    <file src="$(Escape-Xml (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt'))" target="THIRD-PARTY-NOTICES.txt" />
    <file src="$(Escape-Xml (Join-Path $PSScriptRoot 'LICENSE'))" target="lib/net48/Plugins/BulkStepBuilder/LICENSE" />
    <file src="$(Escape-Xml (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt'))" target="lib/net48/Plugins/BulkStepBuilder/THIRD-PARTY-NOTICES.txt" />
  </files>
</package>
"@
$nuspecPath = Join-Path $stage ($config.id + '.nuspec')
[IO.File]::WriteAllText($nuspecPath, $nuspec, [Text.UTF8Encoding]::new($false))
$project = Join-Path $PSScriptRoot 'packaging/Distribution.csproj'
& dotnet restore $project --nologo
if ($LASTEXITCODE -ne 0) { throw 'Packaging project restore failed.' }
& dotnet pack $project --no-build --nologo "-p:NuspecFile=$nuspecPath" "-p:NuspecBasePath=$stage" --output $destination
if ($LASTEXITCODE -ne 0) { throw 'NuGet packaging failed.' }
$package = Join-Path $destination ($config.id + '.' + $version + '.nupkg')
& (Join-Path $PSScriptRoot 'Test-Package.ps1') -PackagePath $package -VerifyPublicUrls:$VerifyPublicUrls
Write-Host "Prepared: $package"
Write-Host 'This command does not publish to NuGet or submit to XrmToolBox.'
