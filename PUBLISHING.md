# Publish Bulk Step Builder

Author: **Gabriel Patatt** · License: **MIT**

## Publication identity

| Field | Value |
| --- | --- |
| Tool name | Bulk Step Builder |
| NuGet package ID | BulkStepBuilder |
| Current package version | 1.0.0 |
| Assembly version | 1.0.0.0 |
| Project | https://github.com/G-Patatt/BulkStepBuilder |
| Icon URL | https://raw.githubusercontent.com/G-Patatt/BulkStepBuilder/main/icon.png |
| Host compatibility dependency | XrmToolBox >= 1.2025.10.74 |
| Runtime | Windows, .NET Framework 4.8 |

NuGet normalizes a trailing zero in four-part versions. Package 1.0.0 and assembly 1.0.0.0 represent the same release. `Pack.ps1` reads the actual release DLL version; `Test-Package.ps1` compares all four version components.

Version 0.1.7 was first published under GabrielPatatt.BulkStepBuilder. The current package ID is BulkStepBuilder. Upload BulkStepBuilder 1.0.0 as a separate NuGet package, not as a new version of the previous ID. The previous publication remains under its original ID. The functional implementation remains the one validated by Gabriel Patatt in 0.1.7; package identity, release numbering and documentation changed.

## Build the package

Use PowerShell on Windows with Git and .NET SDK 10 installed:

```powershell
./Pack.ps1 -VerifyPublicUrls
```

This builds the tool, runs simulated-service and DLL-inspection tests, creates the package and validates its metadata and contents. Use `-SkipBuild` only after `build.ps1 -Test` succeeds against the same sources. `-VerifyPublicUrls` checks the project page and icon without authentication.

The workflow also runs `tests/Package.Tests.ps1`, which verifies rejection of extra DLLs, mismatched versions/dependencies, missing license files and changed DLL content.

Output:

```text
dist/nuget/BulkStepBuilder.1.0.0.nupkg
dist/nuget/BulkStepBuilder.1.0.0.validation.json
```

The package contains the merged extension at `lib/net48/Plugins/BulkStepBuilder.dll`, an embedded icon and README, the MIT license and the Mono.Cecil notice. License copies also accompany the installed plugin. SDK, host, test and example DLLs, keys, local paths, credentials and backups are not distributed in the NuGet package.

`XrmToolBox` is the Tool Library host compatibility dependency. `XrmToolBoxPackage` is used to compile the extension and must not replace it in the distribution metadata. Install the package using XrmToolBox or manual extraction, rather than referencing it from a .NET application.

Metadata is maintained in `packaging/package.json`. Generated NuGet manifests are under `packaging/obj/` and are not committed. The repository commit is recorded only when packaging a clean Git working tree.

## Validate before submitting

- [x] Tool name, author, icon and English description are defined.
- [x] Own small and large XrmToolBox icons are embedded.
- [x] Package layout, single DLL payload, license, readme, version and host dependency are validated locally.
- [x] Local simulated-service tests cover preview, assembly/class/step registration, duplicates, per-table attributes and individual failures.
- [x] Local wizard rendering and attribute picker tests are available.
- [x] The installed host's catalog loader has instantiated the tool locally.
- [x] Gabriel Patatt confirmed on 2026-10-20 that the complete flow is tested and working in a development environment (user-reported validation for 0.1.7; recorded in `packaging/validation.json`).
- [ ] Check connection selection and resize behavior interactively in the supported XrmToolBox host.
- [x] Project page and raw icon URL were checked without authentication after the main branch was made public. Repeat `Pack.ps1 -VerifyPublicUrls` for each release.

The development flow confirmation is user-reported and separate from the automated simulated-service checks. Interactive resize behavior has no explicit validation record yet. Package preparation is separate from NuGet publication and XrmToolBox approval.

## Publish on NuGet.org

1. Sign in to your own NuGet.org account.
2. Open https://www.nuget.org/packages/manage/upload and select the `.nupkg` above.
3. Check the package ID, author, version, MIT license, project URL, icon and XrmToolBox dependency in the upload preview.
4. Publish the package and wait until its page is available and indexed.
5. Keep the package ID and verify ownership. A successful availability lookup is not a reservation of a package ID.

Never upload the source ZIP, test fixture DLL or example business DLL as this package. NuGet versions are immutable; publish a new version for a changed release. Do not commit API keys or access tokens. No API key is needed for the manual website upload.

Expected package page after publication:
https://www.nuget.org/packages/BulkStepBuilder

## Submit to the XrmToolBox Tool Library

1. Sign in at https://www.xrmtoolbox.com/.
2. Open https://www.xrmtoolbox.com/plugins/new/.
3. Enter the published package ID: **BulkStepBuilder**.
4. Review the information imported from NuGet and submit it for administrator validation.
5. Address review feedback and verify installation through the Tool Library after approval.

Suggested submission description:

> Bulk Step Builder creates multiple Dataverse plugin steps for one selected class. Users inspect a local DLL, choose a class and tables, configure messages, stages, execution modes and Update attributes per table, then review the batch before registration. The tool registers the assembly and selected class when needed, preserves existing steps, and provides individual results and CSV export.

Suggested categories, if offered by the portal: **Development**, **Plugin registration**.

## Subsequent releases

Update the project Version/AssemblyVersion together, update the release notes and documentation, run the checks and generate a new package. The GitHub Actions workflow builds, validates and uploads a package artifact; it does not publish to NuGet or submit to the Tool Library.

## Official references

- https://www.xrmtoolbox.com/documentation/for-developers/deploy-your-plugin-in-plugins-store/
- https://www.xrmtoolbox.com/documentation/for-developers/deploy-your-plugin-in-plugins-store/plugin-validation-check-list/
- https://learn.microsoft.com/en-us/nuget/reference/nuspec
- https://learn.microsoft.com/en-us/nuget/nuget-org/publish-a-package
