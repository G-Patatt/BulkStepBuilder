using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace BulkStepBuilder
{
    [Serializable]
    public sealed class AssemblyInfo
    {
        public string Name, Version, Culture, PublicKeyToken;
        public string[] PluginTypes;
        public byte[] Content;
        public bool HasStrongNameSignature;
        public string SigningStatus => string.IsNullOrWhiteSpace(PublicKeyToken) ? "Missing strong-name signature. Sign the DLL before registration." :
            !HasStrongNameSignature ? "Public key present, but full signing was not detected. Rebuild with the signing key." :
            "Strong-name signature metadata detected. Public key token: " + PublicKeyToken + ". Dataverse validates the signature on upload.";
        public string Hash => Digest(Content);
        public static string Digest(byte[] bytes)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
        }
        public override string ToString() => $"{Name} · {Version} · {PluginTypes.Length} plugin class(es)";
    }

    public sealed class TableInfo
    {
        public string LogicalName, Label;
        public int? ObjectTypeCode;
        public HashSet<string> Attributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public override string ToString() => $"{Label} ({LogicalName})";
    }

    public sealed class StepOptions
    {
        public string TypeName;
        public string[] Messages;
        public int[] Stages;
        public bool AsyncPost;
        public int Rank = 1;
        public string FilteringAttributes = "";
        public Dictionary<string, string> FilteringAttributesByTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Configuration = "";
        public bool AllowAssemblyUpdate;
    }

    public sealed class AttributeChoice
    {
        public string LogicalName, Label;
        public override string ToString() => $"{Label} ({LogicalName})";
    }

    public sealed class StepRow
    {
        public string Table { get; set; }
        public string Message { get; set; }
        public string Stage { get; set; }
        public string Mode { get; set; }
        public string Filtering { get; set; }
        public string Status { get; set; }
        public string Action => Status == "Create" ? "Will be created" : Status == "Skipped" ? "Will be skipped" :
            Status == "Existing" ? "Already registered" : Status;
        public string Detail { get; set; }
        public Guid? CreatedId { get; set; }
        internal Guid MessageId, FilterId;
        internal int StageValue, ModeValue;
        internal string FilterAttributes;
        internal bool Supported;
        public override string ToString() => $"{Message} · {Table} · {Stage} · {Mode}";
    }

    public sealed class RegistrationPlan
    {
        public AssemblyInfo Assembly;
        public StepOptions Options;
        public Guid? AssemblyId;
        public string AssemblyAction;
        public string AssemblyState, ClassState;
        public string ExistingHash;
        public List<StepRow> Rows = new List<StepRow>();
        public int NewCount => Rows.Count(r => r.Status == "Create");
    }
}
