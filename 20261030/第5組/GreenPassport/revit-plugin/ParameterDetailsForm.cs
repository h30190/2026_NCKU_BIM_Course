using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TemporalBim
{
    /// <summary>完整保存的 Revit 實體／類型參數、欄位映射來源與警告；全程唯讀。</summary>
    public class ParameterDetailsForm : Form
    {
        public ParameterDetailsForm(PassportInfo passport)
        {
            Text = "完整模型參數與欄位來源｜" + passport.TypeName;
            Size = new Size(980, 650);
            MinimumSize = new Size(760, 500);
            StartPosition = FormStartPosition.CenterParent;

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildParameters(passport.RawParameters ?? new List<RawParameterInfo>()));
            tabs.TabPages.Add(BuildSources(passport.FieldSources ?? new Dictionary<string, FieldSourceInfo>()));
            tabs.TabPages.Add(BuildWarnings(passport.MappingWarnings ?? new List<string>()));
            Controls.Add(tabs);
        }

        private static TabPage BuildParameters(IEnumerable<RawParameterInfo> values)
        {
            var page = new TabPage("實體＋類型參數");
            var grid = Grid();
            Add(grid, "Scope", "範圍", 70); Add(grid, "Name", "參數名稱", 210);
            Add(grid, "Display", "顯示值", 220); Add(grid, "Raw", "原始值", 160);
            Add(grid, "Unit", "單位", 100); Add(grid, "Id", "Parameter ID / Built-in", 180);
            foreach (var p in values.Where(x => !RetiredPassportFields.Contains(x.Name + " " + x.Builtin)).OrderBy(x => x.Scope).ThenBy(x => x.Name))
                grid.Rows.Add(p.Scope, p.Name, p.DisplayValue, p.RawValue, p.Unit, string.IsNullOrWhiteSpace(p.Builtin) ? p.ParameterId : p.Builtin);
            page.Controls.Add(grid);
            return page;
        }

        private static TabPage BuildSources(Dictionary<string, FieldSourceInfo> values)
        {
            var page = new TabPage("核心欄位來源");
            var grid = Grid();
            Add(grid, "Field", "欄位", 140); Add(grid, "Kind", "來源種類", 80);
            Add(grid, "Parameter", "來源參數", 210); Add(grid, "Definition", "定義", 130);
            Add(grid, "Verification", "驗證狀態", 330);
            foreach (var item in values.Where(x => !RetiredPassportFields.Contains(x.Key + " " + x.Value?.ParameterName)).OrderBy(x => x.Key))
                grid.Rows.Add(item.Key, item.Value?.Kind, item.Value?.ParameterName, item.Value?.Definition, item.Value?.Verification);
            page.Controls.Add(grid);
            return page;
        }

        private static TabPage BuildWarnings(IEnumerable<string> warnings)
        {
            var page = new TabPage("映射警告");
            var list = new ListBox { Dock = DockStyle.Fill };
            var data = warnings.Where(x => !RetiredPassportFields.Contains(x)).ToArray();
            list.Items.AddRange((data.Length == 0 ? new[] { "沒有映射警告。" } : data).Cast<object>().ToArray());
            page.Controls.Add(list);
            return page;
        }

        private static DataGridView Grid() => new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };

        private static void Add(DataGridView grid, string name, string header, int width) =>
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, ReadOnly = true });
    }
}
