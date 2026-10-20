using System.ComponentModel.Composition;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;

namespace BulkStepBuilder
{
    [Export(typeof(IXrmToolBoxPlugin))]
    [ExportMetadata("Name", "Bulk Step Builder")]
    [ExportMetadata("Description", "Register multiple steps for one plugin class: local DLL, tables, messages and stages.")]
    [ExportMetadata("SmallImageBase64", Icons.Small)]
    [ExportMetadata("BigImageBase64", Icons.Big)]
    [ExportMetadata("BackgroundColor", "#F5F7FB")]
    [ExportMetadata("PrimaryFontColor", "#1C3151")]
    [ExportMetadata("SecondaryFontColor", "#4D617C")]
    public sealed class Plugin : PluginBase
    {
        public override System.Guid GetId() => new System.Guid("dcc348dc-fbc1-4312-9a2d-4861a73137ce");
        public override IXrmToolBoxPluginControl GetControl() => new BuilderControl();
    }
}
