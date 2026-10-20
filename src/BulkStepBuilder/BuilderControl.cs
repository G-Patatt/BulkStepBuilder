using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;
using Label = System.Windows.Forms.Label;

namespace BulkStepBuilder
{
    public sealed class BuilderControl : PluginControlBase
    {
        private readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        private readonly Label environment = new Label { AutoSize = true, Text = "Environment: connect through XrmToolBox", ForeColor = Color.FromArgb(77, 97, 124) };
        private readonly Label status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        private readonly Button back = Button("Back"), next = Button("Continue"), execute = Button("Register batch"), export = Button("Export CSV"), reset = Button("New batch");
        private readonly TextBox file = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
        private readonly Label assemblyLabel = new Label { AutoSize = true, MaximumSize = new Size(950, 0) };
        private readonly FlowLayoutPanel classes = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(12) };
        private readonly Label classSelection = new Label { Text = "Select one class to continue.", AutoSize = true,
            Margin = new Padding(3, 10, 3, 3), MaximumSize = new Size(1050, 0) };
        private string selectedClass;
        private readonly TextBox search = new TextBox { Dock = DockStyle.Fill };
        private readonly CheckedListBox tableList = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        private readonly Label selectedCount = new Label { AutoSize = true };
        private readonly HashSet<string> selected = new HashSet<string>();
        private readonly CheckedListBox messages = new CheckedListBox { CheckOnClick = true, Height = 85, Width = 220, BorderStyle = BorderStyle.None };
        private readonly CheckedListBox stages = new CheckedListBox { CheckOnClick = true, Height = 85, Width = 220, BorderStyle = BorderStyle.None };
        private readonly CheckBox asyncPost = new CheckBox { Text = "Asynchronous PostOperation", AutoSize = true };
        private readonly CheckBox allowUpdate = new CheckBox { Text = "Allow updating an existing DLL with the same identity (may affect existing steps)", AutoSize = true };
        private readonly NumericUpDown rank = new NumericUpDown { Minimum = 1, Maximum = int.MaxValue, Value = 1, Width = 160 };
        private readonly TextBox attributes = new TextBox { Width = 650, Height = 65, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private readonly Button selectAttributes = Button("Select attributes…");
        private readonly Dictionary<string, string> attributeFilters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly TextBox configuration = new TextBox { Width = 650, Height = 70, Multiline = true, ScrollBars = ScrollBars.Vertical };
        private readonly Label review = new Label { AutoSize = true, MaximumSize = new Size(1050, 0) };
        private readonly Label reviewExplanation = new Label { AutoSize = true, MaximumSize = new Size(1050, 0),
            Margin = new Padding(3, 5, 3, 8), ForeColor = Color.FromArgb(77, 97, 124) };
        private readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, AutoGenerateColumns = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect };
        private AssemblyInfo assembly;
        private List<TableInfo> tables = new List<TableInfo>();
        private RegistrationPlan plan;
        private bool busy, refreshing, completed;
        private int connectionEpoch;
        private string planEnvironment;

        public BuilderControl()
        {
            Font = new Font("Segoe UI", 10F); BackColor = Color.FromArgb(245, 247, 251); Padding = new Padding(20);
            var root = CreateLayout(); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            var header = Stack();
            header.Controls.Add(new Label { Text = "Bulk Step Builder", Font = new Font("Segoe UI", 23, FontStyle.Bold), AutoSize = true, ForeColor = Color.FromArgb(28, 49, 81) });
            header.Controls.Add(Note("One class. Multiple tables. All steps in one batch.")); header.Controls.Add(Note("By Gabriel Patatt")); header.Controls.Add(environment);
            root.Controls.Add(header, 0, 0); root.Controls.Add(tabs, 0, 1);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Controls.Add(status, 0, 0); var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            actions.Controls.AddRange(new Control[] { export, reset, back, next, execute }); footer.Controls.Add(actions, 1, 0); root.Controls.Add(footer, 0, 2); Controls.Add(root);
            BuildAssemblyPage(); BuildTablesPage(); BuildOptionsPage(); BuildReviewPage();
            back.Click += (s, e) => { if (tabs.SelectedIndex > 0) tabs.SelectedIndex--; };
            next.Click += async (s, e) => await Continue();
            execute.Click += async (s, e) => await Register();
            reset.Click += (s, e) => { plan = null; completed = false; grid.DataSource = null; tabs.SelectedIndex = 0; UpdateButtons(); };
            export.Click += (s, e) => Export();
            tabs.Selecting += (s, e) => { if (busy || (e.TabPageIndex > 0 &&
                (assembly == null || !assembly.HasStrongNameSignature || selectedClass == null || (e.TabPageIndex > 1 && selected.Count == 0) || (e.TabPageIndex == 3 && plan == null)))) e.Cancel = true; };
            tabs.SelectedIndexChanged += (s, e) => UpdateButtons();
            foreach (var box in new[] { messages, stages }) box.ItemCheck += (s, e) => BeginInvokeIfReady(() => { if (!stages.CheckedIndices.Contains(2)) asyncPost.Checked = false; asyncPost.Enabled = stages.CheckedIndices.Contains(2); InvalidatePlan(); });
            asyncPost.CheckedChanged += (s, e) => InvalidatePlan(); allowUpdate.CheckedChanged += (s, e) => InvalidatePlan();
            rank.ValueChanged += (s, e) => InvalidatePlan(); configuration.TextChanged += (s, e) => InvalidatePlan();
            selectAttributes.Click += async (s, e) => await ChooseAttributes();
            UpdateButtons();
        }

        private void BuildAssemblyPage()
        {
            var panel = Page("1 · DLL and class");
            panel.Controls.Add(Title("Choose a DLL and a plugin class"), 0, 0);
            panel.Controls.Add(Note("The DLL is inspected locally. It is uploaded only when you register the batch in the final step."), 0, 1);
            var picker = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
            picker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); picker.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var browse = Button("Select DLL…"); browse.Click += async (s, e) => await Browse();
            picker.Controls.Add(file, 0, 0); picker.Controls.Add(browse, 1, 0); panel.Controls.Add(picker, 0, 2);
            panel.Controls.Add(assemblyLabel, 0, 3); panel.Controls.Add(classes, 0, 4); panel.RowStyles[4] = new RowStyle(SizeType.Percent, 100);
            panel.Controls.Add(classSelection, 0, 5);
        }
        private void BuildTablesPage()
        {
            var panel = Page("2 · Tables"); panel.Controls.Add(Title("Select tables for this batch"), 0, 0);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            var load = Button("Load tables"); load.Click += (s, e) => ExecuteMethod(LoadTables);
            var all = Button("Check filtered"); all.Click += (s, e) => CheckVisible(true);
            var none = Button("Uncheck filtered"); none.Click += (s, e) => CheckVisible(false);
            actions.Controls.AddRange(new Control[] { load, all, none }); panel.Controls.Add(actions, 0, 1);
            panel.Controls.Add(Note("Search by display or logical name (your selection is preserved when the search changes):"), 0, 2);
            panel.Controls.Add(search, 0, 3); panel.Controls.Add(tableList, 0, 4); panel.RowStyles[4] = new RowStyle(SizeType.Percent, 100);
            panel.Controls.Add(selectedCount, 0, 5); search.TextChanged += (s, e) => RefreshTables();
            tableList.ItemCheck += (s, e) =>
            {
                if (refreshing) return;
                var table = (TableInfo)tableList.Items[e.Index];
                if (e.NewValue == CheckState.Checked) selected.Add(table.LogicalName); else selected.Remove(table.LogicalName);
                selectedCount.Text = $"{selected.Count} table(s) selected"; InvalidatePlan();
            };
        }
        private void BuildOptionsPage()
        {
            var page = Page("3 · Configuration"); var flow = Stack(); flow.Dock = DockStyle.Fill; flow.AutoScroll = true;
            page.Controls.Add(flow, 0, 0); page.RowStyles[0] = new RowStyle(SizeType.Percent, 100);
            flow.Controls.Add(Title("Configure step combinations"));
            var pairs = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            var left = Stack(); left.Controls.Add(Note("Messages")); messages.Items.AddRange(new object[] { "Create", "Update", "Delete" }); left.Controls.Add(messages);
            var right = Stack(); right.Controls.Add(Note("Stages")); stages.Items.AddRange(new object[] { "PreValidation", "PreOperation", "PostOperation" }); right.Controls.Add(stages);
            pairs.Controls.Add(left); pairs.Controls.Add(right); flow.Controls.Add(pairs);
            asyncPost.Enabled = false; flow.Controls.Add(asyncPost); flow.Controls.Add(Note("PreValidation and PreOperation are always synchronous. PostOperation uses the mode selected above."));
            flow.Controls.Add(Note("Execution order")); flow.Controls.Add(rank);
            flow.Controls.Add(Note("Update filtering attributes — configured separately for each table"));
            flow.Controls.Add(selectAttributes); flow.Controls.Add(attributes);
            flow.Controls.Add(Note("Select Update and at least one table to choose fields. For each table, choose selected attributes or explicitly run on every Update."));
            flow.Controls.Add(Note("Unsecure configuration (optional; do not include secrets)")); flow.Controls.Add(configuration);
            flow.Controls.Add(allowUpdate); flow.Controls.Add(Note("Selected filters apply to new Update steps. Existing steps are preserved. This version does not create images or add components to a solution."));
        }
        private void BuildReviewPage()
        {
            var page = Page("4 · Review and results"); page.Controls.Add(Title("Review the batch before registering"), 0, 0); page.Controls.Add(review, 0, 1);
            page.Controls.Add(reviewExplanation, 0, 2);
            foreach (var name in new[] { "Table", "Message", "Stage", "Mode", "Filtering", "Status", "Detail" })
                grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = name == "Status" ? "Action" : name, HeaderText = new Dictionary<string, string>
                { ["Table"] = "Table", ["Message"] = "Message", ["Stage"] = "Stage", ["Mode"] = "Mode", ["Filtering"] = "Requested Update attributes", ["Status"] = "Planned action / Result", ["Detail"] = "Reason / Next action" }[name], FillWeight = name == "Detail" ? 200 : name == "Status" || name == "Filtering" ? 150 : 100 });
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells; grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex < 0 || !(grid.Rows[e.RowIndex].DataBoundItem is StepRow row)) return;
                MessageBox.Show(this, row + "\nResult: " + row.Action + "\n\n" + row.Detail, "Step details",
                    MessageBoxButtons.OK, row.Status == "Failed" || row.Status == "Not attempted" ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            };
            page.Controls.Add(grid, 0, 3); page.RowStyles[3] = new RowStyle(SizeType.Percent, 100);
        }
        public override void UpdateConnection(IOrganizationService newService, ConnectionDetail detail, string actionName, object parameter)
        {
            connectionEpoch++; plan = null; completed = false; tables.Clear(); selected.Clear(); attributeFilters.Clear(); RefreshTables();
            environment.Text = "Environment: " + (detail?.ConnectionName ?? "not connected");
            base.UpdateConnection(newService, detail, actionName, parameter); UpdateButtons();
        }
        public override void ClosingPlugin(PluginCloseInfo info)
        {
            if (busy) { info.Cancel = true; status.Text = "Wait for the operation to finish before closing the tool."; }
        }
        private async Task Browse()
        {
            using (var dialog = new OpenFileDialog { Filter = "Plugin assembly (*.dll)|*.dll", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                await Run("Reading DLL classes…", () => AssemblyInspector.Inspect(dialog.FileName), result =>
                {
                    SetAssembly(result, dialog.FileName);
                }, "Unable to read DLL");
            }
        }
        private void SetAssembly(AssemblyInfo info, string path)
        {
            assembly = info; file.Text = path; selectedClass = null;
            classSelection.Text = "Select one class to continue.";
            assemblyLabel.Text = info + "\nSigning: " + info.SigningStatus + "\n" +
                (info.HasStrongNameSignature ? "Choose one plugin class for all steps in this batch. Register batch will register the assembly if needed." : ErrorMessages.SigningRequired(info.Name));
            status.Text = info.HasStrongNameSignature ? "DLL inspected. Select one plugin class to continue." : "A strong-name signature is required. Sign the DLL and select the rebuilt file.";
            classes.SuspendLayout();
            try
            {
                while (classes.Controls.Count > 0) classes.Controls[0].Dispose();
                foreach (var name in info.PluginTypes)
                {
                    var choice = new RadioButton { Text = name, AutoSize = true, AutoCheck = true,
                        Margin = new Padding(4, 8, 8, 8), Padding = new Padding(3), AccessibleName = name };
                    choice.CheckedChanged += (s, e) =>
                    {
                        if (!choice.Checked) return;
                        selectedClass = choice.Text;
                        classSelection.Text = "Selected class: " + selectedClass;
                        InvalidatePlan();
                    };
                    classes.Controls.Add(choice);
                }
            }
            finally { classes.ResumeLayout(); }
            InvalidatePlan();
        }
        public async void LoadTables()
        {
            var api = new RegistrationService(Service);
            await Run("Loading tables from the environment…", () => api.LoadTables(), result => { tables = result; selected.IntersectWith(tables.Select(t => t.LogicalName)); RefreshTables(); InvalidatePlan(); });
        }
        private async Task ChooseAttributes()
        {
            if (Service == null || selected.Count == 0 || !messages.CheckedItems.Cast<string>().Contains("Update")) return;
            var chosen = tables.Where(t => selected.Contains(t.LogicalName)).ToList();
            var api = new RegistrationService(Service);
            await Run("Loading filtering attributes for the selected tables…", () => api.LoadAttributeChoices(chosen), available =>
            {
                using (var dialog = new AttributeFilterDialog(chosen, available, attributeFilters))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    foreach (var item in dialog.Result) attributeFilters[item.Key] = item.Value;
                    InvalidatePlan();
                }
            });
        }
        private async Task Continue()
        {
            if (tabs.SelectedIndex == 0)
            {
                if (assembly == null || selectedClass == null) { ShowError("Load a DLL and select a class."); return; }
                if (!assembly.HasStrongNameSignature) { ShowError(ErrorMessages.SigningRequired(assembly.Name), "Assembly signing required"); return; }
                tabs.SelectedIndex = 1; if (tables.Count == 0) ExecuteMethod(LoadTables); return;
            }
            if (tabs.SelectedIndex == 1)
            {
                if (selected.Count == 0) { ShowError("Select at least one table."); return; }
                tabs.SelectedIndex = 2; return;
            }
            if (Service == null) { ShowError("Connect to an environment through XrmToolBox."); return; }
            var api = new RegistrationService(Service); var data = assembly;
            var options = new StepOptions { TypeName = selectedClass, Messages = messages.CheckedItems.Cast<string>().ToArray(),
                Stages = stages.CheckedIndices.Cast<int>().Select(i => new[] { 10, 20, 40 }[i]).ToArray(), AsyncPost = asyncPost.Checked,
                Rank = (int)rank.Value, FilteringAttributesByTable = selected.ToDictionary(name => name, name => attributeFilters.TryGetValue(name, out var value) ? value : "", StringComparer.OrdinalIgnoreCase),
                Configuration = configuration.Text, AllowAssemblyUpdate = allowUpdate.Checked };
            var chosen = tables.Where(t => selected.Contains(t.LogicalName)).ToList(); var target = environment.Text;
            await Run("Validating combinations and checking for existing steps…", () => api.Prepare(data, options, chosen), result =>
            {
                plan = result; completed = false; planEnvironment = target; BindPlan(); tabs.SelectedIndex = 3;
            });
        }
        private async Task Register()
        {
            if (plan == null || Service == null || completed) return;
            var batch = plan;
            var text = $"{planEnvironment}\n\n{batch.AssemblyAction}\nClass: {batch.Options.TypeName}\nNew steps: {batch.NewCount}\n\nNew steps will be active. Register this batch?";
            if (MessageBox.Show(this, text, "Register batch in Dataverse", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var api = new RegistrationService(Service);
            await Run("Registering the batch in the environment…", () =>
            {
                try
                {
                    api.Register(batch);
                    var failures = batch.Rows.Count(r => r.Status == "Failed");
                    return $"Registration finished. {batch.Rows.Count(r => r.Status == "Created")} step(s) created; {failures} failed.\n\n" +
                        (failures == 0 ? "Review the results for each step." : "Open the failed rows for the reason and recommended action. Generate a fresh preview before retrying.");
                }
                catch (Exception ex) { return "Registration stopped.\n\n" + ex.Message; }
            }, result => { completed = true; BindPlan(); status.Text = "Registration finished. Review the results.";
                var warning = batch.Rows.Any(r => r.Status == "Failed" || r.Status == "Not attempted");
                if (warning) status.Text = "Registration needs attention. Double-click a row for details and next steps.";
                MessageBox.Show(this, result, warning ? "Registration needs attention" : "Registration result", MessageBoxButtons.OK, warning ? MessageBoxIcon.Warning : MessageBoxIcon.Information); });
        }
        private async Task Run<T>(string message, Func<T> work, Action<T> done, string errorTitle = "Bulk Step Builder")
        {
            if (busy) return;
            busy = true; var epoch = connectionEpoch; status.Text = message; tabs.Enabled = false; UpdateButtons();
            try
            {
                var result = await Task.Run(work);
                if (epoch == connectionEpoch) { busy = false; status.Text = "Ready."; done(result); }
                else MessageBox.Show(this, "The operation finished on the previous connection. The connection changed; reload the data and review the environment before continuing.", "Connection changed");
            }
            catch (Exception ex) { status.Text = "Operation did not complete. Review the reason and recommended action."; ShowError(ErrorMessages.Describe(ex, message.TrimEnd('.', '…')), errorTitle); }
            finally { busy = false; tabs.Enabled = true; UpdateButtons(); }
        }
        private void BindPlan()
        {
            review.Text = $"{planEnvironment}\nDLL: {plan.Assembly.Name}\nSigning: {plan.Assembly.SigningStatus}\n" +
                (completed ? $"Reviewed DLL action: {plan.AssemblyAction}\nClass: {plan.Options.TypeName}\n" :
                    $"DLL status: {plan.AssemblyState}\nClass: {plan.Options.TypeName}\nClass status: {plan.ClassState}\n") +
                (completed ? $"{plan.Rows.Count(r => r.Status == "Created")} created · {plan.Rows.Count(r => r.Status == "Existing")} already registered · {plan.Rows.Count(r => r.Status == "Skipped")} skipped · {plan.Rows.Count(r => r.Status == "Failed")} failed · {plan.Rows.Count(r => r.Status == "Not attempted")} not attempted" :
                    $"{plan.Rows.Count} combination(s) · {plan.NewCount} ready to create · {plan.Rows.Count(r => r.Status == "Existing")} already registered · {plan.Rows.Count(r => r.Status == "Skipped")} to skip");
            reviewExplanation.Text = completed ? "Double-click a row for full details and recommended action. If an operation failed, generate a new preview before trying again." :
                "Preview only: no changes have been made. Register batch uploads/registers the DLL and selected class if needed, then creates the steps marked 'Will be created'.";
            grid.DataSource = new BindingList<StepRow>(plan.Rows);
        }
        private void RefreshTables()
        {
            refreshing = true; tableList.BeginUpdate(); tableList.Items.Clear();
            foreach (var table in tables.Where(t => t.ToString().IndexOf(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))
                tableList.Items.Add(table, selected.Contains(table.LogicalName));
            tableList.EndUpdate(); refreshing = false; selectedCount.Text = $"{selected.Count} table(s) selected · {tableList.Items.Count} shown";
        }
        private void CheckVisible(bool value) { for (int i = 0; i < tableList.Items.Count; i++) tableList.SetItemChecked(i, value); }
        private void InvalidatePlan() { plan = null; completed = false; grid.DataSource = null; review.Text = "Generate a new preview from the configuration step."; UpdateButtons(); }
        private void UpdateButtons()
        {
            var usesUpdate = messages.CheckedItems.Cast<string>().Contains("Update");
            selectAttributes.Enabled = !busy && usesUpdate && selected.Count > 0 && Service != null;
            attributes.Enabled = usesUpdate;
            attributes.Text = !usesUpdate ? "Select the Update message to configure filtering attributes." : selected.Count == 0 ? "Select tables first." :
                string.Join(Environment.NewLine, tables.Where(t => selected.Contains(t.LogicalName)).Select(t => t.LogicalName + ": " +
                    (attributeFilters.TryGetValue(t.LogicalName, out var value) && !string.IsNullOrWhiteSpace(value) ? value : "All updates (no attribute filter)")));
            back.Enabled = !busy && tabs.SelectedIndex > 0;
            next.Enabled = !busy && (tabs.SelectedIndex != 0 || (assembly != null && assembly.HasStrongNameSignature && selectedClass != null));
            next.Visible = tabs.SelectedIndex < 3;
            next.Text = tabs.SelectedIndex == 2 ? "Generate preview" : "Continue";
            execute.Visible = tabs.SelectedIndex == 3; execute.Enabled = !busy && !completed && plan != null && plan.NewCount > 0;
            export.Visible = tabs.SelectedIndex == 3; export.Enabled = !busy && plan != null;
            reset.Visible = tabs.SelectedIndex == 3; reset.Enabled = !busy;
        }
        private void Export()
        {
            if (plan == null) return;
            using (var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "bulk-step-builder.csv" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    Func<string, string> escape = value => "\"" + ((value ?? "").Length > 0 && "=+-@\t\r".Contains((value ?? "")[0]) ? "'" : "") + (value ?? "").Replace("\"", "\"\"") + "\"";
                    var lines = new List<string> { "Environment,Class,Table,Message,Stage,Mode,RequestedUpdateAttributes,Result,Detail,Id" };
                    lines.AddRange(plan.Rows.Select(r => string.Join(",", new[] { planEnvironment, plan.Options.TypeName, r.Table, r.Message, r.Stage, r.Mode, r.Filtering, r.Action, r.Detail, r.CreatedId?.ToString() }.Select(escape))));
                    File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true)); status.Text = "CSV exported.";
                }
                catch (Exception ex) { ShowError(ErrorMessages.Describe(ex, "Exporting CSV")); }
            }
        }
        private void BeginInvokeIfReady(Action action) { if (IsHandleCreated) BeginInvoke(action); else action(); }
        private void ShowError(string text, string title = "Bulk Step Builder") => MessageBox.Show(this, text, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        private TableLayoutPanel Page(string name)
        {
            var tab = new TabPage(name) { BackColor = Color.White, Padding = new Padding(18) }; tabs.TabPages.Add(tab);
            var panel = CreateLayout(); panel.RowCount = 6; for (int i = 0; i < 6; i++) panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); tab.Controls.Add(panel); return panel;
        }
        private static TableLayoutPanel CreateLayout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); return layout;
        }
        private static FlowLayoutPanel Stack() => new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Top };
        private static Label Note(string text) => new Label { Text = text, AutoSize = true, MaximumSize = new Size(1000, 0), Margin = new Padding(3, 5, 3, 8), ForeColor = Color.FromArgb(77, 97, 124) };
        private static Label Title(string text) => new Label { Text = text, Font = new Font("Segoe UI", 15, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 3, 3, 14) };
        private static Button Button(string text) => new Button { Text = text, AutoSize = true, Height = 34, MinimumSize = new Size(100, 34), FlatStyle = FlatStyle.System, Margin = new Padding(4, 5, 4, 5) };
    }
}
