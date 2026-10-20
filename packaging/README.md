# Bulk Step Builder

**By Gabriel Patatt** · Version 1.0.0 · An XrmToolBox tool for Microsoft Dataverse.

One class. Multiple tables. All steps in one batch.

## What it does

- Inspect a local business plugin DLL without executing its classes.
- Choose one class from the DLL and multiple tables with search and checkboxes.
- Combine Create, Update and Delete with PreValidation, PreOperation and PostOperation.
- Choose synchronous execution or asynchronous PostOperation.
- Select Update filtering attributes independently for each table.
- Review all combinations before making changes to Dataverse.
- Register the assembly and class when needed, preserve existing steps and export results to CSV.
- Read signing and registration errors with a reason and recommended action.

## Installation

After the package is approved in the XrmToolBox Tool Library, open **Configuration > Tool Library**, search for **Bulk Step Builder**, and install it.

For manual installation, close XrmToolBox and copy `lib/net48/Plugins/` from this package into the `Plugins` folder in your XrmToolBox data directory. Keep the accompanying license and third-party notice files. Reopen XrmToolBox.

This package is an XrmToolBox extension, not a library to reference in a .NET application. Do not register `BulkStepBuilder.dll` as a business plugin in Dataverse.

## Requirements and use

Windows, .NET Framework 4.8, XrmToolBox 1.2025.10.74 or later, and a Dataverse user with registration permissions.

1. Open the tool and select a business plugin DLL containing public `IPlugin` classes.
2. Select one class, connect to a development environment, and choose tables.
3. Configure messages, stages, execution order, Update attributes and optional unsecure configuration.
4. Generate a preview and check the target environment and each planned action.
5. Confirm **Register batch** to register the DLL/class if needed, then create eligible steps.

Business DLLs must be fully strong-name signed. An Authenticode certificate is separate from strong-name signing. No prior manual registration in PRT is required.

Existing steps are preserved. Updates to changed DLL content require explicit selection of the update option and an identical assembly identity. Registration can partially succeed; generate a fresh preview before retrying. Successful registrations are not rolled back.

## Current scope

Individual DLLs only: no plugin packages or automatic runtime dependency deployment. No images, secure configuration, impersonation, solution membership or assembly identity migrations.

Gabriel Patatt confirmed on 2026-10-20 that the complete flow is working in a development environment (user-reported validation for 0.1.7). Automated local validation uses a simulated organization service. Package preparation does not imply XrmToolBox approval.

Version 1.0.0 promotes the same implementation to the 1.0 release line, with assembly version 1.0.0.0. Registration behavior is unchanged from 0.1.7.

[Project and documentation](https://github.com/G-Patatt/BulkStepBuilder) · [Report an issue](https://github.com/G-Patatt/BulkStepBuilder/issues)

Licensed under MIT. Mono.Cecil is merged into the extension; its license is included in `THIRD-PARTY-NOTICES.txt`.
