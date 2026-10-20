using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BulkStepBuilder
{
    public sealed class AttributeFilterDialog : Form
    {
        private readonly ListBox tableList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly TextBox search = new TextBox { Dock = DockStyle.Fill };
        private readonly CheckedListBox fields = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        private readonly RadioButton allUpdates = new RadioButton { Text = "Run on every Update (no attribute filter)", AutoSize = true };
        private readonly RadioButton selectedOnly = new RadioButton { Text = "Run only when a selected attribute is included in the Update request", AutoSize = true };
        private readonly Label heading = new Label { AutoSize = true, Font = new Font("Segoe UI", 14, FontStyle.Bold) };
        private readonly Label count = new Label { AutoSize = true, ForeColor = Color.FromArgb(77, 97, 124) };
        private readonly Label summary = new Label { AutoSize = true, MaximumSize = new Size(600, 0) };
        private readonly Button save = new Button { Text = "Apply filters", AutoSize = true, MinimumSize = new Size(110, 34) };
        private readonly Dictionary<string, List<AttributeChoice>> available;
        private readonly Dictionary<string, HashSet<string>> selected = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> everyUpdate = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly List<TableInfo> tables;
        private bool refreshing;
        public Dictionary<string, string> Result { get; private set; }
        private TableInfo Current => tableList.SelectedItem as TableInfo;

        public AttributeFilterDialog(IList<TableInfo> tables, Dictionary<string, List<AttributeChoice>> available, IDictionary<string, string> initial)
        {
            this.tables = tables.ToList(); this.available = available;
            Text = "Update filtering attributes by table"; Font = new Font("Segoe UI", 10); BackColor = Color.White;
            StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(1080, 700); MinimumSize = new Size(940, 600);
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; Padding = new Padding(18);
            foreach (var table in tables)
            {
                if (!available.TryGetValue(table.LogicalName, out var choices)) throw new ArgumentException("Missing attribute metadata for " + table.LogicalName);
                var values = initial != null && initial.TryGetValue(table.LogicalName, out var value) ? RegistrationService.ParseAttributes(value) : new string[0];
                selected[table.LogicalName] = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
                everyUpdate[table.LogicalName] = values.Length == 0;
            }
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            root.Controls.Add(new Label { Text = "Choose filtering attributes separately for each table. Selecting a table does not change the others.", AutoSize = true,
                Margin = new Padding(3, 3, 3, 15), ForeColor = Color.FromArgb(77, 97, 124) }, 0, 0);
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
            body.Controls.Add(tableList, 0, 0);
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(12, 0, 0, 0) };
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 8; i++) right.RowStyles.Add(new RowStyle(i == 6 ? SizeType.Percent : SizeType.AutoSize, i == 6 ? 100 : 0));
            right.Controls.Add(heading, 0, 0);
            // Both radio buttons share one parent, enforcing exactly one mode per table.
            right.Controls.Add(allUpdates, 0, 1); right.Controls.Add(selectedOnly, 0, 2);
            right.Controls.Add(new Label { Text = "Search by display name or logical name:", AutoSize = true, Margin = new Padding(3, 15, 3, 5) }, 0, 3);
            right.Controls.Add(search, 0, 4);
            var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            var check = new Button { Text = "Check filtered", AutoSize = true }; var uncheck = new Button { Text = "Uncheck filtered", AutoSize = true };
            check.Click += (s, e) => CheckVisible(true); uncheck.Click += (s, e) => CheckVisible(false);
            tools.Controls.AddRange(new Control[] { check, uncheck }); right.Controls.Add(tools, 0, 5);
            right.Controls.Add(fields, 0, 6); right.Controls.Add(count, 0, 7); body.Controls.Add(right, 1, 0); root.Controls.Add(body, 0, 1);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 10, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Controls.Add(summary, 0, 0);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 34) };
            actions.Controls.Add(cancel); actions.Controls.Add(save); footer.Controls.Add(actions, 1, 0); root.Controls.Add(footer, 0, 2); Controls.Add(root);
            CancelButton = cancel; AcceptButton = save;
            tableList.SelectedIndexChanged += (s, e) => ShowTable(); search.TextChanged += (s, e) => RefreshFields();
            allUpdates.CheckedChanged += (s, e) => SetMode(); selectedOnly.CheckedChanged += (s, e) => SetMode();
            fields.ItemCheck += (s, e) =>
            {
                if (refreshing || Current == null) return;
                var item = (AttributeChoice)fields.Items[e.Index];
                if (e.NewValue == CheckState.Checked) selected[Current.LogicalName].Add(item.LogicalName); else selected[Current.LogicalName].Remove(item.LogicalName);
                UpdateSummary();
            };
            save.Click += (s, e) =>
            {
                if (!CanSave()) return;
                Result = this.tables.ToDictionary(t => t.LogicalName, t => everyUpdate[t.LogicalName] ? "" : string.Join(",", selected[t.LogicalName].OrderBy(n => n)), StringComparer.OrdinalIgnoreCase);
                DialogResult = DialogResult.OK;
            };
            tableList.Items.AddRange(this.tables.ToArray()); if (tableList.Items.Count > 0) tableList.SelectedIndex = 0; UpdateSummary();
        }
        private void ShowTable()
        {
            if (Current == null) return;
            refreshing = true; heading.Text = Current.ToString(); search.Clear();
            allUpdates.Checked = everyUpdate[Current.LogicalName]; selectedOnly.Checked = !everyUpdate[Current.LogicalName]; refreshing = false;
            RefreshFields();
        }
        private void SetMode()
        {
            if (refreshing || Current == null || (!allUpdates.Checked && !selectedOnly.Checked)) return;
            everyUpdate[Current.LogicalName] = allUpdates.Checked; fields.Enabled = selectedOnly.Checked; UpdateSummary();
        }
        private void RefreshFields()
        {
            if (Current == null) return;
            refreshing = true; fields.BeginUpdate(); fields.Items.Clear();
            foreach (var field in available[Current.LogicalName].Where(a => a.ToString().IndexOf(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))
                fields.Items.Add(field, selected[Current.LogicalName].Contains(field.LogicalName));
            fields.EndUpdate(); refreshing = false; fields.Enabled = !everyUpdate[Current.LogicalName]; UpdateSummary();
        }
        private void CheckVisible(bool value)
        {
            selectedOnly.Checked = true;
            for (int i = 0; i < fields.Items.Count; i++) fields.SetItemChecked(i, value);
        }
        private bool CanSave() => tables.All(t => everyUpdate[t.LogicalName] ||
            (selected[t.LogicalName].Count > 0 && selected[t.LogicalName].All(n => available[t.LogicalName].Any(a => a.LogicalName.Equals(n, StringComparison.OrdinalIgnoreCase)))));
        private void UpdateSummary()
        {
            if (Current != null) count.Text = everyUpdate[Current.LogicalName] ? "Current table: every Update request will run this plugin." :
                $"{selected[Current.LogicalName].Count} attribute(s) selected for {Current.LogicalName}. Selection is preserved when searching.";
            var allCount = tables.Count(t => everyUpdate[t.LogicalName]);
            summary.Text = $"{tables.Count - allCount} table(s) with selected attributes · {allCount} table(s) on every Update." +
                (CanSave() ? "" : "\nSelect at least one valid attribute for each table in filtered mode.");
            save.Enabled = CanSave();
        }
    }
}
