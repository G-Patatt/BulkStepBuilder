param([switch]$Test)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src/BulkStepBuilder/BulkStepBuilder.csproj'
& dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if ($Test) {
    & dotnet build (Join-Path $PSScriptRoot 'tests/BulkStepBuilder.Tests/BulkStepBuilder.Tests.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & (Join-Path $PSScriptRoot 'tests/BulkStepBuilder.Tests/bin/Release/net48/BulkStepBuilder.Tests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $hostProject = Join-Path $PSScriptRoot 'tests/InspectorHost/InspectorHost.csproj'
    & dotnet build $hostProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Inspector host build failed.' }
    $hostDirectory = Join-Path $PSScriptRoot 'tests/InspectorHost/bin/Release/net48'
    $firstContext = Join-Path $hostDirectory 'contexts/one'
    $secondContext = Join-Path $hostDirectory 'contexts/two'
    New-Item -ItemType Directory -Force $firstContext,$secondContext | Out-Null
    $sourceDll = Join-Path $PSScriptRoot 'src/BulkStepBuilder/bin/Release/net48/BulkStepBuilder.dll'
    Copy-Item -LiteralPath $sourceDll -Destination (Join-Path $firstContext 'BulkStepBuilder.dll') -Force
    Copy-Item -LiteralPath $sourceDll -Destination (Join-Path $secondContext 'BulkStepBuilder.dll') -Force
    & (Join-Path $hostDirectory 'InspectorHost.exe') (Join-Path $firstContext 'BulkStepBuilder.dll') (Join-Path $PSScriptRoot 'tests/Fixture/bin/Release/net48/Fixture.dll') (Join-Path $PSScriptRoot 'src/BulkStepBuilder/bin/Release/net48') (Join-Path $secondContext 'BulkStepBuilder.dll')
    if ($LASTEXITCODE -ne 0) { throw 'DLL inspection failed across separate host load contexts.' }
}
$destination = Join-Path $PSScriptRoot 'dist/Plugins'
New-Item -ItemType Directory -Force $destination | Out-Null
# Host dependencies already belong to XrmToolBox. Ship only our assembly.
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src/BulkStepBuilder/bin/Release/net48/BulkStepBuilder.dll') -Destination $destination -Force
Write-Host "Ready: $destination"
