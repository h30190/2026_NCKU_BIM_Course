using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace TemporalBim
{
    /// <summary>
    /// A 模型擷取與建檔；審核操作統一由護照清單的審核管理執行。
    /// </summary>
    public class PassportCandidateForm : Form
    {
        private readonly List<DoorCandidate> _candidates;
        private readonly DataGridView _grid = new DataGridView();
        private readonly Label _summary = new Label();
        private readonly Func<PassportInfo, PassportInfo> _savePassport;
        private static readonly HashSet<string> RequiredColumns = new HashSet<string>
        {
            "WidthMm", "HeightMm", "LeafThicknessMm", "OperationType", "SwingHanding",
            "UseType", "Material", "ClearWidthMm",
        };

        public List<DoorCandidate> SelectedCandidates { get; } = new List<DoorCandidate>();

        public PassportCandidateForm(List<DoorCandidate> candidates, string modelName) : this(candidates, modelName, null) { }

        public PassportCandidateForm(List<DoorCandidate> candidates, string modelName, Func<PassportInfo, PassportInfo> savePassport)
        {
            _candidates = candidates;
            _savePassport = savePassport;
            foreach (var candidate in _candidates)
                if (candidate.Passport.Id == 0) candidate.Passport.ReuseStatus = "Pending";
            BuildLayout(modelName);
        }

        private void BuildLayout(string modelName)
        {
            Text = "A 模型｜建立綠色護照";
            Size = new Size(1380, 720);
            MinimumSize = new Size(1000, 560);
            StartPosition = FormStartPosition.CenterParent;

            var help = new Label
            {
                Text = $"來源模型：{modelName}｜共 {_candidates.Count} 扇門。先勾選未建檔項目，再按『建立所選護照』；儲存成功才顯示已建檔。\n新建檔一律待審核，請到『護照清單與審核管理』審核。淡黃色為核心欄位；淡紅色為缺值，可先建檔後補齊。",
                Left = 14,
                Top = 12,
                Width = 1330,
                Height = 34,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };

            ConfigureGrid();
            _grid.SetBounds(14, 52, 1338, 500);
            _grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            foreach (var candidate in _candidates) AddRow(candidate);

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                MinimumSize = new Size(0, 92),
                Padding = new Padding(10),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
            };
            var details = new Button
            {
                Text = "查看完整參數與來源",
                Left = 14,
                Top = 624,
                Width = 180,
                Height = 32,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            details.Click += (s, e) => ShowSelectedDetails();
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) ShowSelectedDetails(); };

            _summary.AutoSize = true;
            _summary.ForeColor = Color.DimGray;
            UpdateSummary();

            var create = new Button
            {
                Text = "建立所選護照",
                Left = 1182,
                Top = 624,
                Width = 170,
                Height = 34,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            create.Click += Create_Click;

            actions.Controls.AddRange(new Control[] { details, create, _summary });
            Controls.AddRange(new Control[] { help, _grid, actions });
            AcceptButton = create;
        }

        private void ConfigureGrid()
        {
            _grid.AutoGenerateColumns = false;
            _grid.AllowUserToAddRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            _grid.ColumnHeadersHeight = 42;
            _grid.EnableHeadersVisualStyles = false;
            _grid.EditMode = DataGridViewEditMode.EditOnEnter;
            _grid.DataError += (s, e) => { e.ThrowException = false; };
            _grid.CellValueChanged += (s, e) =>
            {
                UpdateSummary();
                if (e.RowIndex >= 0) _grid.InvalidateRow(e.RowIndex);
            };
            _grid.CellFormatting += RequiredCellFormatting;
            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_grid.IsCurrentCellDirty && (_grid.CurrentCell is DataGridViewComboBoxCell || _grid.CurrentCell is DataGridViewCheckBoxCell))
                    _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _grid.CellClick += (s, e) =>
            {
                if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Selected") return;
                if (_grid.Rows[e.RowIndex].Tag is DoorCandidate saved && saved.Passport.Id > 0)
                    MessageBox.Show("這筆護照已經建檔，所以不能再次勾選『新建』。\n\n若要審核或修改，請關閉此視窗後開啟『護照清單與審核管理』。", "已建檔項目");
            };

            AddCheck("Selected", "新建", 48);
            AddText("RecordState", "建檔狀態", 78, true);
            AddText("ReviewDisplay", "審核結果（唯讀）", 165, true);
            _grid.Columns["Selected"].Frozen = true;
            _grid.Columns["RecordState"].Frozen = true;
            _grid.Columns["ReviewDisplay"].Frozen = true;
            AddText("TypeName", "族群／類型", 175, true);
            AddNumber("WidthMm", "＊門寬 mm", 78);
            AddNumber("HeightMm", "＊門高 mm", 78);
            AddCombo("DimensionDefinition", "尺寸定義", 95, "nominal", "leaf", "frame_outer");
            AddNumber("LeafThicknessMm", "＊門扇厚 mm", 92);
            AddText("OperationType", "＊開啟方式", 105);
            AddText("SwingHanding", "＊開門方向", 98);
            AddText("UseType", "＊室內外用途", 98);
            AddText("Material", "＊門扇材料", 110);
            AddNumber("ClearWidthMm", "＊有效淨寬 mm", 104);
            AddNumber("LeafCount", "扇數", 54);
            AddText("Condition", "保存狀況", 95);
            AddNumber("Quantity", "數量", 55);
            AddNumber("PriceNtd", "價格 NT$", 82);
            AddText("PlacementRequirements", "來源位置／宿主", 135);
            AddText("Warnings", "映射警告", 78, true);

            foreach (string name in RequiredColumns) MarkRequired(name);
        }

        private void MarkRequired(string name)
        {
            var column = _grid.Columns[name];
            column.DefaultCellStyle.BackColor = Color.LemonChiffon;
            column.HeaderCell.Style.BackColor = Color.Goldenrod;
            column.HeaderCell.Style.ForeColor = Color.Black;
            column.HeaderCell.Style.Font = new Font(_grid.Font, FontStyle.Bold);
        }

        private void RequiredCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            string name = _grid.Columns[e.ColumnIndex].Name;
            if (!RequiredColumns.Contains(name)) return;
            string value = e.Value?.ToString()?.Trim() ?? "";
            bool missing = string.IsNullOrWhiteSpace(value) || value == "0";
            e.CellStyle.BackColor = missing ? Color.MistyRose : Color.LemonChiffon;
            if (missing) e.CellStyle.ForeColor = Color.DarkRed;
        }

        private void AddText(string name, string header, int width, bool readOnly = false) =>
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, ReadOnly = readOnly });

        private void AddNumber(string name, string header, int width) => AddText(name, header, width);

        private void AddCheck(string name, string header, int width) =>
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = name, HeaderText = header, Width = width });

        private void AddCombo(string name, string header, int width, params string[] values)
        {
            var column = new DataGridViewComboBoxColumn { Name = name, HeaderText = header, Width = width, FlatStyle = FlatStyle.Flat };
            column.Items.AddRange(values.Cast<object>().ToArray());
            _grid.Columns.Add(column);
        }

        private void AddRow(DoorCandidate c)
        {
            var p = c.Passport;
            int rowIndex = _grid.Rows.Add(
                false,
                p.Id > 0 ? "已建檔" : "未建檔",
                ReviewStatusDisplay.Label(p.ReuseStatus),
                p.Family + " / " + p.TypeName,
                Number(p.WidthMm), Number(p.HeightMm), p.DimensionDefinition,
                Number(p.LeafThicknessMm), p.OperationType, p.SwingHanding, p.UseType,
                p.Material, Number(p.ClearWidthMm), p.LeafCount,
                p.Condition,
                p.Quantity,
                Number(p.PriceNtd), p.PlacementRequirements,
                p.MappingWarnings?.Count ?? 0);
            _grid.Rows[rowIndex].Tag = c;
            SetRecordState(_grid.Rows[rowIndex], p);
            if ((p.MappingWarnings?.Count ?? 0) > 0)
                _grid.Rows[rowIndex].Cells["Warnings"].Style.ForeColor = Color.DarkOrange;
        }

        private void SetRecordState(DataGridViewRow row, PassportInfo p)
        {
            row.Cells["RecordState"].Value = p.Id > 0 ? "已建檔" : "未建檔";
            row.Cells["Selected"].Value = false;
            if (p.Id > 0)
            {
                row.ReadOnly = true;
                row.Cells["Selected"].Style.BackColor = SystemColors.Control;
                row.Cells["Selected"].Style.SelectionBackColor = SystemColors.Control;
                row.Cells["Selected"].ToolTipText = "此門已建檔，不能重複建立；審核請至『護照清單與審核管理』。";
                row.Cells["RecordState"].Style.BackColor = Color.Gainsboro;
                row.Cells["RecordState"].Style.ForeColor = Color.DimGray;
            }
            UpdateReviewDisplay(row);
        }

        private static string Number(double value) => value <= 0 ? "" : value.ToString("0.##", CultureInfo.CurrentCulture);

        private void UpdateReviewDisplay(DataGridViewRow row)
        {
            string code = (row.Tag as DoorCandidate)?.Passport.ReuseStatus ?? "Pending";
            var cell = row.Cells["ReviewDisplay"];
            bool saved = row.Tag is DoorCandidate candidate && candidate.Passport.Id > 0;
            cell.Value = ReviewStatusDisplay.Label(code) + (!saved ? "（未建檔）" : "");
            cell.Style.BackColor = cell.Style.SelectionBackColor = ReviewStatusDisplay.Background(code);
            cell.Style.ForeColor = cell.Style.SelectionForeColor = ReviewStatusDisplay.Foreground(code);
        }

        private void UpdateSummary()
        {
            int selected = _grid.Rows.Cast<DataGridViewRow>().Count(r => Bool(r, "Selected"));
            int warningRows = _candidates.Count(c => (c.Passport.MappingWarnings?.Count ?? 0) > 0);
            int saved = _candidates.Count(c => c.Passport.Id > 0);
            int approved = _candidates.Count(c => c.Passport.Id > 0 && c.Passport.ReuseStatus == "Approved");
            _summary.Text = $"已選 {selected} 筆新項目｜已建檔 {saved} 筆、已通過審核 {approved} 筆｜{warningRows} 筆有映射警告。";
        }

        private void ShowSelectedDetails()
        {
            if (_grid.CurrentRow?.Tag is DoorCandidate candidate)
            {
                using (var form = new ParameterDetailsForm(candidate.Passport)) form.ShowDialog(this);
            }
        }

        private void Create_Click(object sender, EventArgs e)
        {
            _grid.EndEdit();
            SelectedCandidates.Clear();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (!Bool(row, "Selected")) continue;
                var candidate = row.Tag as DoorCandidate;
                if (candidate == null) continue;
                if (candidate.Passport.Id > 0) continue;
                ApplyRow(row, candidate);
                var p = candidate.Passport;
                p.ReuseStatus = "Pending";
                SelectedCandidates.Add(candidate);
            }

            if (SelectedCandidates.Count == 0)
            {
                MessageBox.Show("請至少勾選一扇尚未建檔的門。已建檔項目請至審核管理查看。", "尚未選取新項目");
                DialogResult = DialogResult.None;
                return;
            }
            if (_savePassport == null) { DialogResult = DialogResult.OK; return; }

            var errors = new List<string>();
            int created = SaveSelected(errors);
            string result = $"{created} 筆儲存成功，已更新為『已建檔／待審核』。";
            if (errors.Count > 0) result += "\n\n以下項目尚未確認建檔，保留勾選以便重試：\n" + string.Join("\n", errors);
            MessageBox.Show(this, result, "建檔結果");
        }

        private int SaveSelected(List<string> errors)
        {
            int created = 0;
            Enabled = false;
            UseWaitCursor = true;
            try
            {
                foreach (var candidate in SelectedCandidates)
                {
                    try
                    {
                        var saved = _savePassport(candidate.Passport);
                        if (saved == null || saved.Id <= 0 || saved.SourceUniqueId != candidate.Passport.SourceUniqueId || saved.SourceModel != candidate.Passport.SourceModel)
                            throw new InvalidOperationException("後端未回傳有效的建檔結果，請重新載入確認。");
                        candidate.Passport = saved;
                        var row = _grid.Rows.Cast<DataGridViewRow>().First(r => ReferenceEquals(r.Tag, candidate));
                        SetRecordState(row, saved);
                        created++;
                    }
                    catch (Exception ex) { errors.Add(candidate.Passport.TypeName + "：" + ErrorText.RealMessage(ex)); }
                }
            }
            finally
            {
                Enabled = true;
                UseWaitCursor = false;
                UpdateSummary();
            }
            return created;
        }

        private static void ApplyRow(DataGridViewRow row, DoorCandidate c)
        {
            var p = c.Passport;
            p.WidthMm = SetDouble(c, "width", p.WidthMm, Double(row, "WidthMm"), "人工確認門寬");
            p.HeightMm = SetDouble(c, "height", p.HeightMm, Double(row, "HeightMm"), "人工確認門高");
            p.DimensionDefinition = SetText(c, "dimensionDefinition", p.DimensionDefinition, CellText(row, "DimensionDefinition"), "nominal／leaf／frame_outer");
            p.LeafThicknessMm = SetDouble(c, "leafThickness", p.LeafThicknessMm, Double(row, "LeafThicknessMm"), "人工確認門扇厚度");
            p.OperationType = SetText(c, "operationType", p.OperationType, CellText(row, "OperationType"));
            p.SwingHanding = SetText(c, "swingHanding", p.SwingHanding, CellText(row, "SwingHanding"));
            p.UseType = SetText(c, "useType", p.UseType, CellText(row, "UseType"));
            p.Material = SetText(c, "material", p.Material, CellText(row, "Material"));
            p.ClearWidthMm = SetDouble(c, "clearWidth", p.ClearWidthMm, Double(row, "ClearWidthMm"), "有效通行淨寬，非名義門寬");
            p.LeafCount = SetInt(c, "leafCount", p.LeafCount, Int(row, "LeafCount"), "人工確認門扇數");
            p.Condition = CellText(row, "Condition");
            p.Quantity = Math.Max(1, Int(row, "Quantity"));
            p.PriceNtd = Math.Max(0, Double(row, "PriceNtd"));
            p.PlacementRequirements = CellText(row, "PlacementRequirements");
        }

        private static string SetText(DoorCandidate c, string key, string oldValue, string value, string definition = "")
        {
            value = value?.Trim() ?? "";
            if (!string.Equals(oldValue ?? "", value, StringComparison.Ordinal)) c.MarkManual(key, definition);
            return value;
        }

        private static double SetDouble(DoorCandidate c, string key, double oldValue, double value, string definition = "")
        {
            if (Math.Abs(oldValue - value) > 0.01) c.MarkManual(key, definition);
            return value;
        }

        private static int SetInt(DoorCandidate c, string key, int oldValue, int value, string definition = "")
        {
            if (oldValue != value) c.MarkManual(key, definition);
            return value;
        }

        private static string CellText(DataGridViewRow row, string name) => row.Cells[name].Value?.ToString()?.Trim() ?? "";
        private static bool Bool(DataGridViewRow row, string name) => row.Cells[name].Value is bool value && value;
        private static int Int(DataGridViewRow row, string name) => int.TryParse(CellText(row, name), out int value) ? value : 0;
        private static double Double(DataGridViewRow row, string name) => double.TryParse(CellText(row, name), NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ? value : 0;
    }
}
