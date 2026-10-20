using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ServiceModel;
using System.Windows.Forms;
using Label = System.Windows.Forms.Label;
using BulkStepBuilder;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

internal static class Program
{
    private static int passed;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var fixture = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixture.dll");
            var inspected = AssemblyInspector.Inspect(fixture);
            Check(inspected.PluginTypes.Length == 3 && inspected.PluginTypes.Contains("Fixture.ValidateRegistration"), "Only the three public concrete plugin classes are found; constructors never execute");
            Check(inspected.PublicKeyToken.Length == 16 && inspected.HasStrongNameSignature, "Strong name identity and signing flag are read");
            var backgroundInspection = System.Threading.Tasks.Task.Run(() => AssemblyInspector.Inspect(fixture)).GetAwaiter().GetResult();
            Check(backgroundInspection.PluginTypes.SequenceEqual(inspected.PluginTypes), "DLL inspection works on the background thread used by the wizard");
            using (File.Open(fixture, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Check(true, "Inspection releases the DLL file without executing plugin code");
            var invalidDll = Path.Combine(Path.GetTempPath(), "BulkStepBuilder-invalid-" + Guid.NewGuid() + ".dll");
            try
            {
                File.WriteAllText(invalidDll, "This is not a managed DLL.");
                try { AssemblyInspector.Inspect(invalidDll); throw new Exception("Invalid DLL must be rejected."); }
                catch (InvalidOperationException ex)
                {
                    Check(ex.Message.Contains("not a supported plugin DLL") && !ex.Message.Contains("transparent proxy"), "Invalid DLL produces actionable English guidance without internal proxy details");
                }
            }
            finally { File.Delete(invalidDll); }
            try { AssemblyInspector.Inspect(fixture + ".missing"); throw new Exception("Missing file must be rejected."); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("could not be found"), "Missing DLL produces actionable English guidance"); }
            var tables = new[] { new TableInfo { LogicalName = "account", Label = "Account" }, new TableInfo { LogicalName = "contact", Label = "Contact" } };
            var unsigned = AssemblyInspector.Inspect(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UnsignedFixture.dll"));
            Check(!unsigned.HasStrongNameSignature && string.IsNullOrEmpty(unsigned.PublicKeyToken) && unsigned.SigningStatus.Contains("Missing strong-name"), "Unsigned DLL is recognized without running plugin code");
            var unsignedApi = new FakeService();
            try { new RegistrationService(unsignedApi).Prepare(unsigned, Options(unsigned), tables); throw new Exception("Unsigned assembly must be blocked"); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("Properties > Signing") && ex.Message.Contains(".snk") && ex.Message.Contains("PRT first") && unsignedApi.Creates.Count == 0,
                "Unsigned DLL gives concrete signing instructions before any registration"); }
            var delayed = AssemblyInspector.Inspect(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DelaySignedFixture.dll"));
            Check(!delayed.HasStrongNameSignature && delayed.PublicKeyToken.Length == 16 && delayed.SigningStatus.Contains("full signing was not detected"), "Delay-signed DLL is distinguished from fully signed DLL");
            Throws(() => new RegistrationService(new FakeService()).Prepare(delayed, Options(delayed), tables), "Delay signing cannot pass registration validation");
            var permission = new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040220), Message = "Missing prvCreatePluginAssembly privilege" }, "Registration denied");
            var permissionMessage = ErrorMessages.Describe(permission, "Registering DLL");
            Check(permissionMessage.Contains("administrator") && permissionMessage.Contains("prvCreatePluginAssembly") && permissionMessage.Contains("0x80040220"), "Permission errors preserve the server code and explain the required action");
            var signingFault = new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { Message = "Assembly is not fully signed" }, "Assembly rejected");
            Check(ErrorMessages.Describe(signingFault, "Registering DLL").Contains("original key"), "Server signing rejection explains strong-name identity and existing key reuse");
            Check(ErrorMessages.Describe(new TimeoutException("Operation timed out"), "Registering DLL").Contains("Some changes may have been saved"), "Timeout warns about an uncertain server outcome before retrying");
            var rejectedApi = new FakeService { RegistrationFailureEntity = "pluginassembly", RegistrationFailure = signingFault };
            var rejectedService = new RegistrationService(rejectedApi); var rejectedPlan = rejectedService.Prepare(inspected, Options(inspected), tables);
            Throws(() => rejectedService.Register(rejectedPlan), "Assembly registration rejection stops the batch");
            Check(rejectedApi.Creates.Count == 0 && rejectedPlan.Rows.All(r => r.Status == "Not attempted" && r.Detail.Contains("No step creation was attempted") && r.Detail.Contains("not fully signed")),
                "Assembly failure marks steps as not attempted and keeps the actual server signing reason");
            var classApi = new FakeService { RegistrationFailureEntity = "plugintype", RegistrationFailure = permission };
            var classService = new RegistrationService(classApi); var classPlan = classService.Prepare(inspected, Options(inspected), tables);
            Throws(() => classService.Register(classPlan), "Class registration rejection stops step creation");
            Check(classApi.Creates.Count == 1 && classApi.Creates[0].LogicalName == "pluginassembly" && classPlan.Rows.All(r => r.Status == "Not attempted" && r.Detail.Contains("Registering class") && r.Detail.Contains("may have been saved")),
                "Class failure identifies the failed phase and warns that the assembly may already be saved");
            var stepFailureApi = new FakeService { RegistrationFailureEntity = "sdkmessageprocessingstep", RegistrationFailure = permission };
            var stepFailureService = new RegistrationService(stepFailureApi); var stepFailurePlan = stepFailureService.Prepare(inspected, Options(inspected), tables); stepFailureService.Register(stepFailurePlan);
            Check(stepFailurePlan.Rows.All(r => r.Status == "Failed" && r.Detail.Contains(r.Table) && r.Detail.Contains(r.Message) && r.Detail.Contains("administrator") && r.Detail.Contains("0x80040220")),
                "Each step failure includes its table, message, phase, server code and next action");
            var api = new FakeService(); var service = new RegistrationService(api);
            var options = Options(inspected);
            var plan = service.Prepare(inspected, options, tables);
            Check(plan.Rows.Count == 12 && plan.NewCount == 12 && api.Creates.Count == 0, "Preview creates all 2 x 2 x 3 combinations without writes");
            Check(plan.AssemblyState.Contains("Not registered yet") && plan.ClassState.Contains("Not registered yet") && plan.Rows.All(r => r.Action == "Will be created"),
                "Unregistered DLL and class remain eligible and preview explains what Register batch will do");
            var reviewTables = new[] { "account", "dci_accountplan", "dfs_article", "dfs_assetlocationaddress" }.Select(n => new TableInfo { LogicalName = n, Label = n }).ToArray();
            var reviewOptions = Options(inspected); reviewOptions.Stages = new[] { 20, 40 };
            var reviewApi = new FakeService(reviewTables.Select(t => t.LogicalName).ToArray()) { SecondaryEntityName = "none" };
            var reviewPlan = new RegistrationService(reviewApi).Prepare(inspected, reviewOptions, reviewTables);
            Check(reviewPlan.NewCount == 16 && reviewPlan.Rows.All(r => r.Status == "Create") && reviewApi.Creates.Count == 0,
                "User review scenario: all sixteen Create/Update combinations accept the 'none' secondary-table marker without uploading the DLL");
            var numericApi = new FakeService { NumericPrimaryCodes = true, SecondaryEntityName = " NONE " };
            var numericTables = new[] { new TableInfo { LogicalName = "account", ObjectTypeCode = 1 }, new TableInfo { LogicalName = "contact", ObjectTypeCode = 2 } };
            Check(new RegistrationService(numericApi).Prepare(inspected, options, numericTables).NewCount == 12,
                "Registration filters match object type codes and normalize the empty-secondary marker");
            Throws(() => new RegistrationService(new FakeService { EmptyFilters = true }).Prepare(inspected, options, tables),
                "Empty metadata responses report validation failure instead of marking every step unavailable");
            Check(plan.Rows.Where(r => r.Stage == "PostOperation").All(r => r.Mode == "Asynchronous") && plan.Rows.Where(r => r.Stage != "PostOperation").All(r => r.Mode == "Synchronous"), "Async applies only to PostOperation");
            service.Register(plan);
            Check(api.Creates.Count(e => e.LogicalName == "pluginassembly") == 1 && api.Creates.Count(e => e.LogicalName == "plugintype") == 1, "One assembly and only selected plugin type registered");
            Check(plan.Rows.All(r => r.Status == "Created" && r.CreatedId.HasValue), "Every step has individual success and ID");
            Check(api.Creates.Where(e => e.LogicalName == "sdkmessageprocessingstep").All(e => e.GetAttributeValue<EntityReference>("eventhandler").Id == api.Data.Single(e2 => e2.LogicalName == "plugintype").Id), "All steps point to the selected class");
            var repeat = service.Prepare(inspected, options, tables);
            Check(repeat.NewCount == 0 && repeat.Rows.All(r => r.Status == "Existing"), "A new preview skips existing steps");
            var otherOptions = Options(inspected); otherOptions.TypeName = inspected.PluginTypes[1];
            var otherClass = service.Prepare(inspected, otherOptions, tables); service.Register(otherClass);
            Check(otherClass.Rows.All(r => r.Status == "Created") && api.Creates.Count(e => e.LogicalName == "pluginassembly") == 1 && api.Creates.Count(e => e.LogicalName == "plugintype") == 2, "Another class in the same DLL gets its own steps without uploading assembly again");
            var filterApi = new FakeService(); var filterService = new RegistrationService(filterApi);
            var filterOptions = Options(inspected); filterOptions.FilteringAttributes = "name"; filterOptions.Configuration = "{\"feature\":true}"; filterOptions.Rank = 12;
            var filterPlan = filterService.Prepare(inspected, filterOptions, tables); filterService.Register(filterPlan);
            Check(filterApi.Creates.Where(e => e.LogicalName == "sdkmessageprocessingstep").All(e => e.GetAttributeValue<int>("rank") == 12 && e.GetAttributeValue<string>("configuration") == filterOptions.Configuration), "Execution order and unsecure configuration reach created steps");
            Check(filterApi.Creates.Where(e => e.LogicalName == "sdkmessageprocessingstep").All(e => e.GetAttributeValue<string>("filteringattributes") == (e.GetAttributeValue<string>("name").Contains(": Update") ? "name" : null)), "Filtering attributes apply only to Update");
            var perTableApi = new FakeService(); var perTableService = new RegistrationService(perTableApi);
            var choicesByTable = perTableService.LoadAttributeChoices(tables);
            Check(choicesByTable["account"].Any(a => a.LogicalName == "accountnumber") && !choicesByTable["account"].Any(a => a.LogicalName == "accountid") &&
                choicesByTable["contact"].Any(a => a.LogicalName == "lastname") && !choicesByTable["contact"].Any(a => a.LogicalName == "accountnumber"),
                "Attribute picker loads table-specific metadata and excludes primary keys");
            var perTableOptions = Options(inspected);
            perTableOptions.FilteringAttributesByTable["account"] = "accountnumber"; perTableOptions.FilteringAttributesByTable["contact"] = "lastname";
            var perTablePlan = perTableService.Prepare(inspected, perTableOptions, tables); perTableService.Register(perTablePlan);
            Check(perTableApi.Creates.Where(e => e.LogicalName == "sdkmessageprocessingstep" && e.GetAttributeValue<string>("name").Contains(": Update"))
                .All(e => e.GetAttributeValue<string>("filteringattributes") == (e.GetAttributeValue<string>("name").Contains("of account") ? "accountnumber" : "lastname")),
                "Different selected attributes are sent to each table's Update steps");
            Check(perTablePlan.Rows.Where(r => r.Message == "Update").All(r => r.Filtering == (r.Table == "account" ? "accountnumber" : "lastname")),
                "Review displays the actual per-table Update filtering selection");
            var allUpdatesOptions = Options(inspected); allUpdatesOptions.FilteringAttributes = "name";
            allUpdatesOptions.FilteringAttributesByTable["account"] = "accountnumber"; allUpdatesOptions.FilteringAttributesByTable["contact"] = "";
            var allUpdatesApi = new FakeService(); var allUpdatesService = new RegistrationService(allUpdatesApi);
            var allUpdatesPlan = allUpdatesService.Prepare(inspected, allUpdatesOptions, tables); allUpdatesService.Register(allUpdatesPlan);
            Check(allUpdatesPlan.Rows.Where(r => r.Message == "Update" && r.Table == "contact").All(r => r.Filtering == "All updates") &&
                allUpdatesApi.Creates.Where(e => e.LogicalName == "sdkmessageprocessingstep" && e.GetAttributeValue<string>("name").Contains(": Update of contact"))
                    .All(e => !e.Contains("filteringattributes")), "Explicit all-updates choice remains independent from other tables and legacy shared filters");
            var invalidPerTable = Options(inspected); invalidPerTable.FilteringAttributesByTable["contact"] = "accountnumber";
            Throws(() => new RegistrationService(new FakeService()).Prepare(inspected, invalidPerTable, tables), "A selected attribute is validated against its own table");
            var missingOptions = Options(inspected); missingOptions.FilteringAttributes = "missing_column";
            Throws(() => filterService.Prepare(inspected, missingOptions, tables), "Missing filtering attributes block preview before writes");
            missingOptions.FilteringAttributes = "accountid";
            Throws(() => filterService.Prepare(inspected, missingOptions, tables), "Primary key cannot be a filtering attribute");
            var pageApi = new FakeService { PageSize = 1 }; var pageService = new RegistrationService(pageApi);
            var paged = pageService.Prepare(inspected, options, tables); pageService.Register(paged);
            Check(pageService.Prepare(inspected, options, tables).NewCount == 0 && pageApi.PageRequests > 6, "Metadata filters and existing steps are read across multiple pages");

            var unsupportedApi = new FakeService(); unsupportedApi.NoContactDelete = true;
            var unsupportedOptions = Options(inspected); unsupportedOptions.Messages = new[] { "Create", "Delete" };
            var unsupported = new RegistrationService(unsupportedApi).Prepare(inspected, unsupportedOptions, tables);
            Check(unsupported.Rows.Count(r => r.Status == "Skipped") == 3, "Unsupported table/message combinations are ignored");
            Check(unsupported.Rows.Where(r => r.Status == "Skipped").All(r => r.Detail.Contains("'Delete'") && r.Detail.Contains("'contact'") && r.Detail.Contains("independent of uploading")),
                "Skipped rows identify their table/message and distinguish metadata validation from DLL registration");

            var failApi = new FakeService { FailContact = true };
            var failService = new RegistrationService(failApi); var failPlan = failService.Prepare(inspected, options, tables);
            failService.Register(failPlan);
            Check(failPlan.Rows.Count(r => r.Status == "Failed") == 6 && failPlan.Rows.Count(r => r.Status == "Created") == 6, "Individual failure preserves successful results");
            failApi.FailContact = false;
            var retry = failService.Prepare(inspected, options, tables);
            Check(retry.NewCount == 6, "Fresh preview retries only missing steps after partial failure");
            var raceApi = new FakeService(); var raceService = new RegistrationService(raceApi);
            var racePlan = raceService.Prepare(inspected, options, tables);
            raceApi.Data.Add(AssemblyEntity(inspected));
            Throws(() => raceService.Register(racePlan), "Assembly state changes block writes after preview");
            Check(raceApi.Creates.Count == 0, "No mutation after stale preview");

            var duplicateRaceApi = new FakeService(); var duplicateRaceService = new RegistrationService(duplicateRaceApi);
            var duplicateRacePlan = duplicateRaceService.Prepare(inspected, options, tables);
            duplicateRaceService.Register(duplicateRacePlan);
            var deletion = duplicateRaceApi.Data.First(e => e.LogicalName == "sdkmessageprocessingstep");
            duplicateRaceApi.Data.Remove(deletion);
            var latePlan = duplicateRaceService.Prepare(inspected, options, tables);
            duplicateRaceApi.Data.Add(deletion);
            duplicateRaceService.Register(latePlan);
            Check(latePlan.Rows.All(r => r.Status == "Existing"), "Duplicate check repeats immediately before step creation");

            var updateApi = new FakeService(); var old = AssemblyEntity(inspected); old["content"] = Convert.ToBase64String(new byte[] { 1, 2, 3 }); updateApi.Data.Add(old);
            var updateService = new RegistrationService(updateApi);
            Throws(() => updateService.Prepare(inspected, options, tables), "Changed DLL requires explicit update option");
            var updateOptions = Options(inspected); updateOptions.AllowAssemblyUpdate = true;
            var updatePlan = updateService.Prepare(inspected, updateOptions, tables); updateService.Register(updatePlan);
            Check(updateApi.Updates == 1, "Authorized update changes assembly once");
            var removedApi = new FakeService(); var removedAssembly = AssemblyEntity(inspected); removedAssembly["content"] = "AQID"; removedApi.Data.Add(removedAssembly);
            removedApi.Data.Add(new Entity("plugintype", Guid.NewGuid()) { ["pluginassemblyid"] = removedAssembly.ToEntityReference(), ["typename"] = "Missing.Class" });
            Throws(() => new RegistrationService(removedApi).Prepare(inspected, updateOptions, tables), "Update cannot silently remove registered classes");
            var versionApi = new FakeService(); var wrong = AssemblyEntity(inspected); wrong["version"] = "99.0.0.0"; versionApi.Data.Add(wrong);
            Throws(() => new RegistrationService(versionApi).Prepare(inspected, options, tables), "Different assembly identity is blocked");
            var keyApi = new FakeService(); var wrongKey = AssemblyEntity(inspected); wrongKey["publickeytoken"] = "0123456789abcdef"; keyApi.Data.Add(wrongKey);
            try { new RegistrationService(keyApi).Prepare(inspected, options, tables); throw new Exception("Changed key must be blocked"); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("original signing key") && ex.Message.Contains("0123456789abcdef") && ex.Message.Contains(inspected.PublicKeyToken) && keyApi.Creates.Count == 0,
                "Mismatched signing key shows both public tokens and requires rebuilding with the original key"); }
            var invalid = Options(inspected); invalid.Stages = new[] { 20 };
            Throws(() => RegistrationService.Validate(inspected, invalid, tables), "Async without PostOperation is invalid");
            Check(RegistrationService.ParseAttributes("NAME; name, emailaddress1\n").SequenceEqual(new[] { "emailaddress1", "name" }), "Filtering attributes are normalized and deduplicated");
            Throws(() => RegistrationService.Validate(inspected, options, new TableInfo[0]), "Empty table selection is invalid");
            invalid = Options(inspected); invalid.TypeName = "Not.In.This.Dll";
            Throws(() => RegistrationService.Validate(inspected, invalid, tables), "Class must belong to inspected DLL");

            var catalog = new System.ComponentModel.Composition.Hosting.AssemblyCatalog(typeof(Plugin).Assembly);
            var exported = catalog.Parts.Single(p => p.ExportDefinitions.Any(e => e.ContractName.Contains("IXrmToolBoxPlugin")));
            Check(exported.ExportDefinitions.Any(e => e.Metadata.ContainsKey("SmallImageBase64") && e.Metadata.ContainsKey("BigImageBase64")), "XrmToolBox MEF export includes both required icons");
            if (args.Length > 0) Render(args[0], inspected);
            Console.WriteLine($"PASS: {passed} checks"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static StepOptions Options(AssemblyInfo assembly) => new StepOptions { TypeName = assembly.PluginTypes[0], Messages = new[] { "Create", "Update" }, Stages = new[] { 10, 20, 40 }, AsyncPost = true };
    private static void Check(bool success, string name) { if (!success) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    private static void Throws(Action action, string name) { try { action(); } catch (InvalidOperationException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
    private static Entity AssemblyEntity(AssemblyInfo assembly) => new Entity("pluginassembly", Guid.NewGuid())
    { ["name"] = assembly.Name, ["version"] = assembly.Version, ["culture"] = assembly.Culture, ["publickeytoken"] = assembly.PublicKeyToken, ["content"] = Convert.ToBase64String(assembly.Content) };
    private static void Set(object instance, string field, object value) => instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, value);
    private static T Get<T>(object instance, string field) => (T)instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);
    private static void Render(string directory, AssemblyInfo inspected)
    {
        Application.EnableVisualStyles(); Directory.CreateDirectory(directory);
        using (var form = new Form { ClientSize = new Size(1200, 820), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) })
        using (var control = new BuilderControl { Dock = DockStyle.Fill })
        {
            form.Controls.Add(control); form.Show(); Application.DoEvents();
            control.GetType().GetMethod("SetAssembly", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(control, new object[] { inspected, @"C:\Plugins\MyLibrary.dll" });
            var choices = Get<FlowLayoutPanel>(control, "classes").Controls.OfType<RadioButton>().ToArray();
            Check(choices.Length == 3 && choices.All(c => !c.Checked) && !Get<Button>(control, "next").Enabled, "Class selection is explicit and required before continuing");
            choices[1].Checked = true; choices[0].Checked = true;
            Check(choices.Count(c => c.Checked) == 1 && Get<string>(control, "selectedClass") == choices[0].Text && Get<Button>(control, "next").Enabled,
                "Selecting a class enables Continue and keeps exactly one class selected");
            var unsigned = AssemblyInspector.Inspect(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UnsignedFixture.dll"));
            control.GetType().GetMethod("SetAssembly", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(control, new object[] { unsigned, @"C:\Plugins\Unsigned.dll" });
            Get<FlowLayoutPanel>(control, "classes").Controls.OfType<RadioButton>().First().Checked = true;
            Get<TabControl>(control, "tabs").SelectedIndex = 1;
            Check(!Get<Button>(control, "next").Enabled && Get<TabControl>(control, "tabs").SelectedIndex == 0 && Get<Label>(control, "assemblyLabel").Text.Contains(".snk"),
                "Unsigned DLL displays signing guidance and cannot advance even after selecting a class");
            using (var image = new Bitmap(control.Width, control.Height))
            { control.DrawToBitmap(image, new Rectangle(Point.Empty, control.Size)); image.Save(Path.Combine(directory, "unsigned-dll.png")); }
            control.GetType().GetMethod("SetAssembly", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(control, new object[] { inspected, @"C:\Plugins\MyLibrary.dll" });
            Get<FlowLayoutPanel>(control, "classes").Controls.OfType<RadioButton>().First().Checked = true;
            var testTables = new List<TableInfo> { new TableInfo { LogicalName = "account", Label = "Account" }, new TableInfo { LogicalName = "contact", Label = "Contact" } };
            Set(control, "tables", testTables); Get<HashSet<string>>(control, "selected").UnionWith(testTables.Select(t => t.LogicalName));
            control.GetType().GetMethod("RefreshTables", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(control, null);
            var options = Options(inspected); var plan = new RegistrationService(new FakeService()).Prepare(inspected, options, testTables);
            Get<CheckedListBox>(control, "messages").SetItemChecked(0, true); Get<CheckedListBox>(control, "messages").SetItemChecked(1, true);
            for (int stage = 0; stage < 3; stage++) Get<CheckedListBox>(control, "stages").SetItemChecked(stage, true);
            Application.DoEvents(); Get<CheckBox>(control, "asyncPost").Checked = true;
            var tabs = Get<TabControl>(control, "tabs");
            for (int i = 0; i < 4; i++)
            {
                Set(control, "plan", plan); Set(control, "planEnvironment", "Environment: Local demo (not connected)");
                control.GetType().GetMethod("BindPlan", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(control, null);
                tabs.SelectedIndex = i; form.PerformLayout(); control.PerformLayout(); Application.DoEvents();
                Check(tabs.SelectedIndex == i && tabs.SelectedTab.Controls.Count > 0, $"Wizard page {i + 1} is accessible");
                using (var image = new Bitmap(control.Width, control.Height))
                { control.DrawToBitmap(image, new Rectangle(Point.Empty, control.Size)); image.Save(Path.Combine(directory, $"step-{i + 1}.png")); }
            }
            Check(true, "All four UI pages render with local demonstration data");
        }
        var available = new RegistrationService(new FakeService()).LoadAttributeChoices(new[] { new TableInfo { LogicalName = "account" }, new TableInfo { LogicalName = "contact" } });
        var pickerTables = new[] { new TableInfo { LogicalName = "account", Label = "Account" }, new TableInfo { LogicalName = "contact", Label = "Contact" } };
        var initial = new Dictionary<string, string> { ["account"] = "accountnumber", ["contact"] = "lastname" };
        using (var picker = new AttributeFilterDialog(pickerTables, available, initial))
        {
            picker.StartPosition = FormStartPosition.Manual; picker.Location = new Point(-32000, -32000); picker.Show(); Application.DoEvents();
            var pickerList = Get<ListBox>(picker, "tableList"); var pickerFields = Get<CheckedListBox>(picker, "fields"); var pickerSearch = Get<TextBox>(picker, "search");
            Check(Get<RadioButton>(picker, "selectedOnly").Checked && pickerFields.CheckedItems.Cast<AttributeChoice>().Single().LogicalName == "accountnumber", "Picker restores the current table's selected attributes");
            pickerSearch.Text = "name"; pickerSearch.Clear();
            Check(pickerFields.CheckedItems.Cast<AttributeChoice>().Single().LogicalName == "accountnumber", "Filtering search preserves selected attributes");
            pickerList.SelectedIndex = 1;
            Check(pickerFields.CheckedItems.Cast<AttributeChoice>().Single().LogicalName == "lastname", "Switching tables restores independent checkbox selections");
            Get<RadioButton>(picker, "allUpdates").Checked = true; pickerList.SelectedIndex = 0;
            Check(Get<RadioButton>(picker, "selectedOnly").Checked && pickerFields.CheckedItems.Cast<AttributeChoice>().Single().LogicalName == "accountnumber", "All-updates mode applies only to its selected table");
            using (var image = new Bitmap(picker.Width, picker.Height))
            { picker.DrawToBitmap(image, new Rectangle(Point.Empty, picker.Size)); image.Save(Path.Combine(directory, "attribute-picker.png")); }
            Get<Button>(picker, "save").PerformClick();
            Check(picker.Result["account"] == "accountnumber" && picker.Result["contact"] == "" && initial["contact"] == "lastname", "Apply returns independent filters without mutating the original choices");
        }
    }
}

internal sealed class FakeService : IOrganizationService
{
    public readonly List<Entity> Data = new List<Entity>(), Creates = new List<Entity>();
    private readonly Dictionary<string, Guid> messageIds = new Dictionary<string, Guid>();
    private readonly Dictionary<string, Guid> filterIds = new Dictionary<string, Guid>();
    private readonly string[] tableNames;
    public bool NoContactDelete, FailContact;
    public bool NumericPrimaryCodes, EmptyFilters;
    public string SecondaryEntityName = "none";
    public string RegistrationFailureEntity;
    public Exception RegistrationFailure;
    public int Updates;
    public int PageSize = 5000, PageRequests;
    public FakeService(string[] tableNames = null)
    {
        this.tableNames = tableNames ?? new[] { "account", "contact" };
        foreach (var message in new[] { "Create", "Update", "Delete" })
        {
            messageIds[message] = Guid.NewGuid();
            foreach (var table in this.tableNames) filterIds[table + message] = Guid.NewGuid();
        }
    }
    public Guid Create(Entity entity)
    {
        if (entity.LogicalName == RegistrationFailureEntity) throw RegistrationFailure;
        if (FailContact && entity.LogicalName == "sdkmessageprocessingstep" && entity.GetAttributeValue<string>("name").Contains("of contact")) throw new InvalidOperationException("Simulated row failure");
        entity.Id = Guid.NewGuid(); Data.Add(entity); Creates.Add(entity); return entity.Id;
    }
    public void Update(Entity entity)
    {
        Updates++; var existing = Data.Single(e => e.Id == entity.Id); foreach (var attribute in entity.Attributes) existing[attribute.Key] = attribute.Value;
    }
    public EntityCollection RetrieveMultiple(QueryBase queryBase)
    {
        var query = (QueryExpression)queryBase;
        if (query.EntityName == "sdkmessage")
        {
            if (query.LinkEntities.Count > 0) throw new InvalidOperationException("Test host does not expose joined aliases.");
            var names = query.Criteria.Conditions.Single(c => c.AttributeName == "name").Values.Cast<string>();
            return Page(names.Select(name => new Entity("sdkmessage", messageIds[name]) { ["name"] = name }).ToList(), query);
        }
        if (query.EntityName == "sdkmessagefilter")
        {
            if (query.LinkEntities.Count > 0) throw new InvalidOperationException("Test host does not expose joined aliases.");
            var rows = new List<Entity>();
            if (EmptyFilters) return Page(rows, query);
            var requestedIds = query.Criteria.Conditions.Single(c => c.AttributeName == "sdkmessageid").Values.Cast<Guid>().ToArray();
            foreach (var message in messageIds.Keys)
            foreach (var table in tableNames)
            {
                if (!requestedIds.Contains(messageIds[message])) continue;
                if (NoContactDelete && table == "contact" && message == "Delete") continue;
                rows.Add(new Entity("sdkmessagefilter", filterIds[table + message]) { ["primaryobjecttypecode"] = NumericPrimaryCodes ? (object)(Array.IndexOf(tableNames, table) + 1) : table,
                    ["secondaryobjecttypecode"] = SecondaryEntityName, ["sdkmessageid"] = new EntityReference("sdkmessage", messageIds[message]) });
            }
            return Page(rows, query);
        }
        return Page(Data.Where(e => e.LogicalName == query.EntityName && query.Criteria.Conditions.All(c =>
        {
            e.Attributes.TryGetValue(c.AttributeName, out var value);
            return Equals(value is EntityReference reference ? (object)reference.Id : value, c.Values[0]);
        })).ToList(), query);
    }
    private EntityCollection Page(List<Entity> rows, QueryExpression query)
    {
        PageRequests++; int skip = (query.PageInfo.PageNumber - 1) * PageSize;
        return new EntityCollection(rows.Skip(skip).Take(PageSize).ToList()) { MoreRecords = rows.Count > skip + PageSize, PagingCookie = "test-cookie" };
    }
    public OrganizationResponse Execute(OrganizationRequest request)
    {
        if (request is RetrieveEntityRequest retrieve)
        {
            var metadata = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata { LogicalName = retrieve.LogicalName };
            typeof(Microsoft.Xrm.Sdk.Metadata.EntityMetadata).GetProperty("PrimaryIdAttribute").SetValue(metadata, retrieve.LogicalName + "id");
            typeof(Microsoft.Xrm.Sdk.Metadata.EntityMetadata).GetProperty("Attributes").SetValue(metadata,
                new Microsoft.Xrm.Sdk.Metadata.AttributeMetadata[] { new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata { LogicalName = "name" },
                    new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata { LogicalName = retrieve.LogicalName == "account" ? "accountnumber" : "lastname" },
                    new Microsoft.Xrm.Sdk.Metadata.UniqueIdentifierAttributeMetadata { LogicalName = retrieve.LogicalName + "id" } });
            var response = new RetrieveEntityResponse(); response.Results["EntityMetadata"] = metadata; return response;
        }
        throw new NotSupportedException(request.RequestName);
    }
    public Entity Retrieve(string name, Guid id, ColumnSet columns) => Data.Single(e => e.LogicalName == name && e.Id == id);
    public void Delete(string name, Guid id) => throw new NotSupportedException();
    public void Associate(string name, Guid id, Relationship relationship, EntityReferenceCollection related) => throw new NotSupportedException();
    public void Disassociate(string name, Guid id, Relationship relationship, EntityReferenceCollection related) => throw new NotSupportedException();
}
