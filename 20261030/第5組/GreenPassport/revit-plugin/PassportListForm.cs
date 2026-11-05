using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TemporalBim
{
    public class PassportListForm : Form
    {
        private readonly DataGridView _grid = new DataGridView();
        public PassportInfo SelectedPassport => _grid.CurrentRow?.Tag as PassportInfo;
        public string SelectedAction { get; private set; } = "";

        public PassportListForm(List<PassportInfo> passports)
        {
            Text = "Revit 護照清單與審核管理";
            Size = new Size(980, 560);
            MinimumSize = new Size(760, 440);
            StartPosition = FormStartPosition.CenterParent;

            _grid.Dock = DockStyle.Fill;
            _grid.AutoGenerateColumns = false;
            _grid.AllowUserToAddRows = false;
            _grid.ReadOnly = true;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            Add("PassportId", "護照", 90); Add("Reuse", "審核結果", 125);
            Add("Type", "族群／類型", 210); Add("Status", "供應狀態", 85);
            Add("Dimensions", "尺寸", 120); Add("Condition", "狀況", 120);
            Add("Source", "來源模型／Element", 210);
            _grid.Columns.Insert(0, new DataGridViewButtonColumn
            {
                Name = "ReviewAction",
                HeaderText = "個別審核",
                Text = "審核此項",
                UseColumnTextForButtonValue = false,
                Width = 100,
            });
            _grid.Columns["ReviewAction"].Frozen = true;
            _grid.Columns["PassportId"].Frozen = true;
            _grid.Columns["Reuse"].Frozen = true;
            _grid.CellContentClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && _grid.Columns[e.ColumnIndex].Name == "ReviewAction")
                {
                    _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                    Finish("Review");
                }
            };
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) Finish("Review"); };
            foreach (var p in passports)
            {
                int i = _grid.Rows.Add(p.ReuseStatus == "Approved" ? "查看審核" : "審核此項", p.PassportId, ReviewStatusDisplay.Label(p.ReuseStatus), p.Family + " / " + p.TypeName, p.Status,
                    Format(p.WidthMm) + "×" + Format(p.HeightMm) + " mm", p.ConditionGrade + " " + p.Condition,
                    p.SourceModel + " / " + p.SourceElementId);
                _grid.Rows[i].Tag = p;
                var cell = _grid.Rows[i].Cells["Reuse"];
                cell.Style.BackColor = cell.Style.SelectionBackColor = ReviewStatusDisplay.Background(p.ReuseStatus);
                cell.Style.ForeColor = cell.Style.SelectionForeColor = ReviewStatusDisplay.Foreground(p.ReuseStatus);
                cell.ToolTipText = $"{ReviewStatusDisplay.Label(p.ReuseStatus)}｜檢核人：{p.InspectedBy}｜日期：{p.InspectedOn}";
            }

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, MinimumSize = new Size(0, 56), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var review = new Button { Text = "審核選取項目", Width = 155, Height = 36 };
            review.Click += (s, e) => Finish("Review");
            var approveAll = new Button { Text = "全部通過審核…", Width = 175, Height = 36, BackColor = Color.Honeydew };
            approveAll.Click += (s, e) => { SelectedAction = "ApproveAll"; DialogResult = DialogResult.OK; };
            var details = new Button { Text = "定位並查看完整資料", Width = 180, Height = 32 };
            details.Click += (s, e) => Finish("Details");
            bottom.Controls.Add(approveAll); bottom.Controls.Add(review); bottom.Controls.Add(details);
            Controls.Add(_grid); Controls.Add(bottom);
        }

        private void Add(string name, string header, int width) =>
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, ReadOnly = true });

        private void Finish(string action)
        {
            if (SelectedPassport == null) { MessageBox.Show("請先選一筆護照。"); return; }
            SelectedAction = action;
            DialogResult = DialogResult.OK;
        }

        private static string Format(double value) => value > 0 ? value.ToString("0.#") : "待確認";
    }
}
