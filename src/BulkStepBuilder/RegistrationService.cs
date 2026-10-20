using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace BulkStepBuilder
{
    public sealed class RegistrationService
    {
        private readonly IOrganizationService service;
        public RegistrationService(IOrganizationService service) { this.service = service ?? throw new ArgumentNullException(nameof(service)); }

        public List<TableInfo> LoadTables()
        {
            var response = (RetrieveAllEntitiesResponse)service.Execute(new RetrieveAllEntitiesRequest
            { EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = false });
            return response.EntityMetadata.Where(m => !string.IsNullOrEmpty(m.LogicalName))
                .Select(m => new TableInfo { LogicalName = m.LogicalName, ObjectTypeCode = m.ObjectTypeCode,
                    Label = m.DisplayName?.UserLocalizedLabel?.Label ?? m.LogicalName })
                .OrderBy(t => t.Label).ToList();
        }

        public RegistrationPlan Prepare(AssemblyInfo assembly, StepOptions options, IList<TableInfo> tables)
        {
            Validate(assembly, options, tables);
            var plan = new RegistrationPlan { Assembly = assembly, Options = options };
            var existing = FindAssembly(assembly);
            plan.AssemblyId = existing?.Id;
            plan.ExistingHash = ContentHash(existing);
            plan.AssemblyAction = existing == null ? "Register new assembly" :
                plan.ExistingHash == assembly.Hash ? "Reuse identical assembly" : "Update existing assembly DLL (affects its current steps)";
            plan.AssemblyState = existing == null ? "Not registered yet. Register batch will upload and register this DLL first." :
                plan.ExistingHash == assembly.Hash ? "Already registered. The identical DLL will be reused." :
                "Already registered. The DLL content will be updated when you confirm Register batch.";
            if (existing != null && plan.ExistingHash != assembly.Hash && !options.AllowAssemblyUpdate)
                throw new InvalidOperationException("An assembly with this identity already exists with different content. Enable the update option to review this change.");
            if (existing != null && plan.ExistingHash != assembly.Hash)
            {
                var types = Query("plugintype", new ColumnSet("typename"), "pluginassemblyid", existing.Id);
                var removed = types.Select(t => t.GetAttributeValue<string>("typename"))
                    .Except(assembly.PluginTypes, StringComparer.Ordinal).ToArray();
                if (removed.Length > 0) throw new InvalidOperationException("Update blocked: the DLL is missing previously registered classes: " + string.Join(", ", removed));
            }

            // Resolve message IDs directly; do not depend on joined alias shapes returned by a host service adapter.
            var messageQuery = new QueryExpression("sdkmessage") { ColumnSet = new ColumnSet("name") };
            messageQuery.Criteria.AddCondition("name", ConditionOperator.In, options.Messages.Cast<object>().ToArray());
            var messages = All(messageQuery);
            var messageIds = options.Messages.ToDictionary(name => name, name =>
            {
                var found = messages.SingleOrDefault(m => string.Equals(m.GetAttributeValue<string>("name"), name, StringComparison.OrdinalIgnoreCase));
                if (found == null) throw new InvalidOperationException($"Registration validation failed: the environment did not return the standard message '{name}'. The DLL has not been uploaded. Refresh the connection and generate the preview again.");
                return found.Id;
            });
            var filterQuery = new QueryExpression("sdkmessagefilter")
            { ColumnSet = new ColumnSet("sdkmessageid", "primaryobjecttypecode", "secondaryobjecttypecode") };
            filterQuery.Criteria.AddCondition("iscustomprocessingstepallowed", ConditionOperator.Equal, true);
            filterQuery.Criteria.AddCondition("sdkmessageid", ConditionOperator.In, messageIds.Values.Cast<object>().ToArray());
            var filters = All(filterQuery);
            if (filters.Count == 0) throw new InvalidOperationException("Registration validation failed: the environment returned no eligible table/message filters. This does not mean that the DLL must be registered first. No changes were made. Refresh the connection and try generating the preview again.");
            var typeId = existing == null ? (Guid?)null : FindType(existing.Id, options.TypeName)?.Id;
            plan.ClassState = typeId.HasValue ? "Already registered. Existing class registration will be reused." :
                "Not registered yet. Register batch will register this class before creating its steps.";
            var steps = typeId.HasValue ? Query("sdkmessageprocessingstep", StepColumns(), "eventhandler", typeId.Value) : new List<Entity>();
            foreach (var table in tables)
            {
                var requested = ParseAttributes(options.FilteringAttributesByTable != null && options.FilteringAttributesByTable.TryGetValue(table.LogicalName, out var perTable)
                    ? perTable : options.FilteringAttributes);
                if (requested.Length > 0 && options.Messages.Contains("Update"))
                {
                    var metadata = ((RetrieveEntityResponse)service.Execute(new RetrieveEntityRequest
                    { LogicalName = table.LogicalName, EntityFilters = EntityFilters.Attributes, RetrieveAsIfPublished = false })).EntityMetadata;
                    var names = new HashSet<string>(metadata.Attributes.Select(a => a.LogicalName), StringComparer.OrdinalIgnoreCase);
                    var missing = requested.Where(a => !names.Contains(a) || a == metadata.PrimaryIdAttribute).ToArray();
                    if (missing.Length > 0) throw new InvalidOperationException($"Invalid filtering attributes in '{table.LogicalName}': {string.Join(", ", missing)}. Open Select attributes and choose fields belonging to this table, excluding its primary key.");
                }
                foreach (var message in options.Messages)
                foreach (var stage in options.Stages)
                {
                    var filter = filters.FirstOrDefault(f => MatchesTable(f, table) &&
                        NoSecondaryTable(f.GetAttributeValue<string>("secondaryobjecttypecode")) &&
                        f.GetAttributeValue<EntityReference>("sdkmessageid")?.Id == messageIds[message]);
                    var row = new StepRow
                    {
                        Table = table.LogicalName, Message = message, StageValue = stage,
                        Stage = StageName(stage), ModeValue = stage == 40 && options.AsyncPost ? 1 : 0,
                        Mode = stage == 40 && options.AsyncPost ? "Asynchronous" : "Synchronous",
                        Filtering = message == "Update" ? (requested.Length == 0 ? "All updates" : string.Join(", ", requested)) : "—",
                        FilterId = filter?.Id ?? Guid.Empty,
                        MessageId = filter?.GetAttributeValue<EntityReference>("sdkmessageid")?.Id ?? Guid.Empty,
                        FilterAttributes = message == "Update" ? string.Join(",", requested) : null,
                        Supported = filter != null, Status = filter == null ? "Skipped" : "Create",
                        Detail = filter == null ? $"No eligible '{message}' registration was returned for table '{table.LogicalName}'. This step will be skipped. This check is independent of uploading or registering your DLL." :
                            "Eligible table/message. Will be created when you confirm Register batch."
                    };
                    if (row.Supported && IsDuplicate(steps, row)) { row.Status = "Existing"; row.Detail = "Same class, table, message, stage and mode. Preserved even if disabled or configured with different attributes."; }
                    plan.Rows.Add(row);
                }
            }
            return plan;
        }

        public Dictionary<string, List<AttributeChoice>> LoadAttributeChoices(IEnumerable<TableInfo> tables)
        {
            var result = new Dictionary<string, List<AttributeChoice>>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in tables)
            {
                var metadata = ((RetrieveEntityResponse)service.Execute(new RetrieveEntityRequest
                { LogicalName = table.LogicalName, EntityFilters = EntityFilters.Attributes, RetrieveAsIfPublished = false })).EntityMetadata;
                result[table.LogicalName] = metadata.Attributes
                    .Where(a => !string.IsNullOrEmpty(a.LogicalName) && a.LogicalName != metadata.PrimaryIdAttribute &&
                        string.IsNullOrEmpty(a.AttributeOf) && a.IsLogical != true)
                    .Select(a => new AttributeChoice { LogicalName = a.LogicalName, Label = a.DisplayName?.UserLocalizedLabel?.Label ?? a.LogicalName })
                    .OrderBy(a => a.Label).ThenBy(a => a.LogicalName).ToList();
            }
            return result;
        }

        public void Register(RegistrationPlan plan, Action<string> report = null)
        {
            var operation = "Checking assembly registration";
            var writeAttempted = false;
            Guid typeId;
            List<Entity> existing;
            try
            {
                Validate(plan.Assembly, plan.Options, plan.Rows.Select(r => new TableInfo { LogicalName = r.Table }).ToList());
                // Verify environment state again before any write; content is the exact byte[] inspected earlier.
                var current = FindAssembly(plan.Assembly);
                if (current?.Id != plan.AssemblyId || ContentHash(current) != plan.ExistingHash)
                    throw new InvalidOperationException("The assembly changed in the environment after the review. Generate a new preview.");
                var assemblyId = current?.Id ?? Guid.Empty;
                if (current == null)
                {
                    operation = $"Registering DLL '{plan.Assembly.Name}'";
                    report?.Invoke("Registering assembly...");
                    var entity = new Entity("pluginassembly");
                    entity["name"] = plan.Assembly.Name; entity["version"] = plan.Assembly.Version;
                    entity["culture"] = plan.Assembly.Culture; entity["publickeytoken"] = plan.Assembly.PublicKeyToken;
                    entity["content"] = Convert.ToBase64String(plan.Assembly.Content);
                    entity["isolationmode"] = new OptionSetValue(2); entity["sourcetype"] = new OptionSetValue(0);
                    writeAttempted = true;
                    assemblyId = service.Create(entity);
                }
                else if (plan.ExistingHash != plan.Assembly.Hash)
                {
                    operation = $"Updating DLL '{plan.Assembly.Name}'";
                    if (!plan.Options.AllowAssemblyUpdate) throw new InvalidOperationException("Assembly update was not authorized in the review.");
                    report?.Invoke("Updating assembly...");
                    var entity = new Entity("pluginassembly", assemblyId);
                    entity["content"] = Convert.ToBase64String(plan.Assembly.Content);
                    writeAttempted = true;
                    service.Update(entity);
                }
                // Some servers register types automatically; otherwise explicitly register the selected class.
                operation = $"Checking class '{plan.Options.TypeName}'";
                var type = FindType(assemblyId, plan.Options.TypeName);
                typeId = type?.Id ?? Guid.Empty;
                if (type == null)
                {
                    operation = $"Registering class '{plan.Options.TypeName}'";
                    writeAttempted = true;
                    typeId = service.Create(new Entity("plugintype")
                    {
                        ["pluginassemblyid"] = new EntityReference("pluginassembly", assemblyId),
                        ["typename"] = plan.Options.TypeName, ["name"] = plan.Options.TypeName,
                        ["friendlyname"] = plan.Options.TypeName
                    });
                }
                operation = "Checking existing steps";
                existing = Query("sdkmessageprocessingstep", StepColumns(), "eventhandler", typeId);
            }
            catch (Exception ex)
            {
                var outcome = "No step creation was attempted. " + (writeAttempted ?
                    "Assembly or class changes may have been saved. Generate a fresh preview before retrying." :
                    "No assembly or class writes were attempted. Correct the issue and generate a fresh preview.");
                var detail = ErrorMessages.Describe(ex, operation) + "\n\nRegistration outcome: " + outcome;
                foreach (var row in plan.Rows.Where(r => r.Status == "Create")) { row.Status = "Not attempted"; row.Detail = detail; }
                throw new InvalidOperationException(detail, ex);
            }
            foreach (var row in plan.Rows.Where(r => r.Supported && r.Status == "Create"))
            {
                report?.Invoke(row.ToString());
                try
                {
                    if (IsDuplicate(existing, row)) { row.Status = "Existing"; row.Detail = "Found again before creation."; continue; }
                    var step = new Entity("sdkmessageprocessingstep")
                    {
                        ["name"] = $"{plan.Options.TypeName}: {row.Message} of {row.Table} ({row.Stage}, {row.Mode})",
                        ["eventhandler"] = new EntityReference("plugintype", typeId),
                        ["sdkmessageid"] = new EntityReference("sdkmessage", row.MessageId),
                        ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", row.FilterId),
                        ["stage"] = new OptionSetValue(row.StageValue), ["mode"] = new OptionSetValue(row.ModeValue),
                        ["rank"] = plan.Options.Rank, ["supporteddeployment"] = new OptionSetValue(0)
                    };
                    if (row.ModeValue == 1) step["asyncautodelete"] = true;
                    if (!string.IsNullOrEmpty(row.FilterAttributes)) step["filteringattributes"] = row.FilterAttributes;
                    if (!string.IsNullOrWhiteSpace(plan.Options.Configuration)) step["configuration"] = plan.Options.Configuration;
                    row.CreatedId = service.Create(step); row.Status = "Created"; row.Detail = row.CreatedId.ToString();
                    existing.Add(step);
                }
                catch (Exception ex) { row.Status = "Failed"; row.Detail = ErrorMessages.Describe(ex, $"Creating {row.Message} step for '{row.Table}' ({row.Stage}, {row.Mode})") +
                    "\n\nOther rows are processed independently. Successful registrations are preserved. Generate a fresh preview before retrying."; }
            }
        }

        private Entity FindAssembly(AssemblyInfo assembly)
        {
            var assemblies = Query("pluginassembly", new ColumnSet("name", "version", "culture", "publickeytoken", "content", "ismanaged"), "name", assembly.Name);
            if (assemblies.Count > 1) throw new InvalidOperationException("More than one assembly has this name. This version requires a unique assembly identity per name in the environment.");
            var found = assemblies.SingleOrDefault();
            if (found == null) return null;
            var registeredToken = found.GetAttributeValue<string>("publickeytoken") ?? "";
            if (!string.Equals(registeredToken, assembly.PublicKeyToken, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The DLL '{assembly.Name}' uses a different strong-name key from the registered assembly. " +
                    $"Registered public key token: {registeredToken}; selected DLL token: {assembly.PublicKeyToken}. " +
                    "To update this assembly, rebuild with its original signing key and select the rebuilt DLL. This tool does not migrate signing identities. No changes have been made.");
            if (found.GetAttributeValue<string>("version") != assembly.Version ||
                NormalizeCulture(found.GetAttributeValue<string>("culture")) != NormalizeCulture(assembly.Culture))
                throw new InvalidOperationException($"An assembly named '{assembly.Name}' is already registered with version {found.GetAttributeValue<string>("version")} " +
                    $"and culture {NormalizeCulture(found.GetAttributeValue<string>("culture"))}. The selected DLL uses version {assembly.Version} and culture {NormalizeCulture(assembly.Culture)}. " +
                    "Select a DLL with the registered identity, or manage the version migration in Plugin Registration Tool. This tool does not migrate assembly identities. No changes have been made.");
            if (found.GetAttributeValue<bool>("ismanaged") && ContentHash(found) != assembly.Hash)
                throw new InvalidOperationException("This assembly is managed and cannot be updated by this tool.");
            return found;
        }
        private Entity FindType(Guid assemblyId, string name) => Query("plugintype", new ColumnSet("typename"), "pluginassemblyid", assemblyId)
            .SingleOrDefault(e => e.GetAttributeValue<string>("typename") == name);
        private static string NormalizeCulture(string value) => string.IsNullOrEmpty(value) ? "neutral" : value.ToLowerInvariant();
        private static bool NoSecondaryTable(string value) => string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase);
        private static bool MatchesTable(Entity filter, TableInfo table)
        {
            if (!filter.Attributes.TryGetValue("primaryobjecttypecode", out var value)) return false;
            if (value is AliasedValue alias) value = alias.Value;
            if (value is OptionSetValue option) value = option.Value;
            var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)?.Trim();
            return string.Equals(text, table.LogicalName, StringComparison.OrdinalIgnoreCase) ||
                (table.ObjectTypeCode.HasValue && text == table.ObjectTypeCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        private static string ContentHash(Entity assembly)
        {
            if (assembly == null) return null;
            var content = assembly.GetAttributeValue<string>("content");
            if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("Could not read the existing assembly content to compare the DLL.");
            return AssemblyInfo.Digest(Convert.FromBase64String(content));
        }
        private static ColumnSet StepColumns() => new ColumnSet("sdkmessageid", "sdkmessagefilterid", "stage", "mode");
        private static bool IsDuplicate(IEnumerable<Entity> steps, StepRow row) => steps.Any(e =>
            e.GetAttributeValue<EntityReference>("sdkmessageid")?.Id == row.MessageId &&
            e.GetAttributeValue<EntityReference>("sdkmessagefilterid")?.Id == row.FilterId &&
            e.GetAttributeValue<OptionSetValue>("stage")?.Value == row.StageValue &&
            e.GetAttributeValue<OptionSetValue>("mode")?.Value == row.ModeValue);
        private List<Entity> Query(string table, ColumnSet columns, string attribute, object value)
        {
            var query = new QueryExpression(table) { ColumnSet = columns };
            query.Criteria.AddCondition(attribute, ConditionOperator.Equal, value);
            return All(query);
        }
        private List<Entity> All(QueryExpression query)
        {
            var result = new List<Entity>();
            query.PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 };
            while (true)
            {
                var page = service.RetrieveMultiple(query); result.AddRange(page.Entities);
                if (!page.MoreRecords) return result;
                query.PageInfo.PageNumber++; query.PageInfo.PagingCookie = page.PagingCookie;
            }
        }
        public static string StageName(int stage) => stage == 10 ? "PreValidation" : stage == 20 ? "PreOperation" : "PostOperation";
        public static string[] ParseAttributes(string value) => (value ?? "").Split(new[] { ',', ';', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim().ToLowerInvariant()).Distinct().OrderBy(v => v).ToArray();
        public static void Validate(AssemblyInfo assembly, StepOptions options, IList<TableInfo> tables)
        {
            if (assembly == null || options == null || !assembly.PluginTypes.Contains(options.TypeName)) throw new InvalidOperationException("Select a class from the loaded DLL.");
            if (string.IsNullOrWhiteSpace(assembly.PublicKeyToken) || !assembly.HasStrongNameSignature) throw new InvalidOperationException(ErrorMessages.SigningRequired(assembly.Name));
            if (tables == null || tables.Count == 0) throw new InvalidOperationException("Select at least one table.");
            if (options.Messages == null || options.Messages.Length == 0 || options.Messages.Any(m => m != "Create" && m != "Update" && m != "Delete")) throw new InvalidOperationException("Choose Create, Update and/or Delete.");
            if (options.Stages == null || options.Stages.Length == 0 || options.Stages.Any(s => s != 10 && s != 20 && s != 40)) throw new InvalidOperationException("Select a valid stage.");
            if (options.Rank < 1) throw new InvalidOperationException("Execution order must be greater than zero.");
            if (options.AsyncPost && !options.Stages.Contains(40)) throw new InvalidOperationException("Asynchronous execution requires PostOperation.");
        }
    }
}
