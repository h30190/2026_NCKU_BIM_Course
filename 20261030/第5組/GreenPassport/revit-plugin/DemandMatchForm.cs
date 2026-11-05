using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TemporalBim
{
    /// <summary>B 模型需求確認、三態配對、預留與 B-1 替換。</summary>
    public class DemandMatchForm : Form
    {
        private const double MatchingToleranceMm = 5.0;
        private readonly Autodesk.Revit.UI.UIDocument _uiDoc;
        private readonly Autodesk.Revit.DB.FamilyInstance _door;
        private readonly PassportInfo _model;
        private readonly double _hostWallThicknessMm;
        private readonly string _actor;
        private readonly DemandDataMapper _mapping;
        private readonly ToolTip _sources = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300, ReshowDelay = 100 };
        private int _demandRevision;
        private bool _matching;

        private readonly TextBox _width = Box();
        private readonly TextBox _height = Box();
        private readonly ComboBox _dimension = Combo("nominal", "leaf", "frame_outer");
        private readonly TextBox _tolerance = new TextBox { Text = "5", ReadOnly = true, TextAlign = HorizontalAlignment.Center };
        private readonly TextBox _thickness = Box();
        private readonly ComboBox _operation = Combo("", "單扇平開", "雙扇平開", "橫拉");
        private readonly ComboBox _swing = Combo("", "左鉸・內開", "右鉸・內開", "左鉸・外開", "右鉸・外開");
        private readonly ComboBox _use = Combo("", "Interior", "Exterior");
        private readonly ComboBox _material = Combo("");
        private readonly TextBox _clearWidth = Box();
        private readonly TextBox _leafCount = Box("1");
        private readonly CheckBox _installationReviewed = new CheckBox { Text = "我已檢查門框、門洞、宿主牆與方向", AutoSize = true };
        private readonly TextBox _basis = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical };
        private readonly Label _status = new Label { ForeColor = Color.DimGray, AutoEllipsis = true };
        private readonly DataGridView _grid = new DataGridView();
        private readonly TextBox _preview = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
        private readonly PictureBox _doorPreview = new PictureBox
        {
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Cursor = Cursors.Hand,
            SizeMode = PictureBoxSizeMode.Zoom,
        };

        public DemandMatchForm(
            Autodesk.Revit.UI.UIDocument uiDoc,
            Autodesk.Revit.DB.FamilyInstance door,
            PassportInfo model,
            double hostWallThicknessMm,
            string roomContext,
            string actor)
            : this(uiDoc, door, model, hostWallThicknessMm, roomContext, actor, "") { }

        public DemandMatchForm(
            Autodesk.Revit.UI.UIDocument uiDoc,
            Autodesk.Revit.DB.FamilyInstance door,
            PassportInfo model,
            double hostWallThicknessMm,
            string roomContext,
            string actor,
            string hostFunction)
        {
            _uiDoc = uiDoc;
            _door = door;
            _model = model;
            _hostWallThicknessMm = hostWallThicknessMm;
            _actor = actor ?? "";
            _mapping = new DemandDataMapper(model, hostFunction);
            BuildLayout(roomContext);
            FillFromModel();
            SetupRequiredHighlighting();
            SetupFieldSources();
            Shown += async (s, e) => await LoadSupplyChoicesAsync();
        }

        private void BuildLayout(string roomContext)
        {
            Text = "B 模型｜需求確認、配對與 B-1 替換";
            Size = new Size(1180, 820);
            MinimumSize = new Size(960, 700);
            StartPosition = FormStartPosition.CenterParent;

            var context = new Label
            {
                Text = $"選取門：{_model.TypeName}｜宿主牆厚：{Display(_hostWallThicknessMm)} mm｜{roomContext}\n門寬高 ±5 mm；淨寬採 A ≥ B。紅色＊為配對項目；淡黃＝已填、淡紅＝缺值，其餘僅供記錄。",
                Left = 14, Top = 10, Width = 1135, Height = 42,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };

            var input = new TableLayoutPanel
            {
                Left = 14, Top = 58, Width = 1135, Height = 206,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                ColumnCount = 6, RowCount = 5, CellBorderStyle = TableLayoutPanelCellBorderStyle.Single,
            };
            input.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            input.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            input.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            input.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            input.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            input.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            for (int i = 0; i < 5; i++) input.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 4 ? 72 : 31));

            Add(input, 0, "＊門寬 mm", _width); Add(input, 2, "＊門高 mm", _height); Add(input, 4, "尺寸定義（參考）", _dimension);
            Add(input, 6, "尺寸容差（固定）", _tolerance); Add(input, 8, "＊門扇厚 mm", _thickness); Add(input, 10, "門扇數（參考）", _leafCount);
            Add(input, 12, "＊開啟方式", _operation); Add(input, 14, "＊開門方向", _swing); Add(input, 16, "＊室內外用途", _use);
            Add(input, 18, "＊材質", _material); Add(input, 20, "＊最低淨寬 mm", _clearWidth); Add(input, 22, "安裝檢查（記錄）", _installationReviewed);
            Add(input, 24, "需求依據（記錄）", _basis);
            input.SetColumnSpan(_basis, 5);

            var sourceDetails = new Button { Text = "查看 B 參數與來源", Left = 14, Top = 268, Width = 165, Height = 32 };
            sourceDetails.Click += (s, e) => { using (var form = new ParameterDetailsForm(_model)) form.ShowDialog(this); };
            var suggestions = new Button { Text = "確認名稱建議…", Left = 187, Top = 268, Width = 150, Height = 32, Enabled = _mapping.Suggestions.Count > 0 };
            suggestions.Click += (s, e) => ApplySuggestions();
            var submit = new Button { Text = "執行三態配對", Left = 999, Top = 268, Width = 150, Height = 32, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            submit.Click += async (s, e) => await SubmitAsync();
            _status.SetBounds(346, 272, 640, 24);
            _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            ConfigureGrid();
            _grid.SetBounds(14, 310, 1135, 296);
            _grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _grid.SelectionChanged += (s, e) => UpdatePreview();
            _grid.CellDoubleClick += (s, e) => ShowSelectedDoorPreview();

            _doorPreview.SetBounds(14, 616, 250, 145);
            _doorPreview.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _doorPreview.Click += (s, e) => ShowSelectedDoorPreview();

            _preview.SetBounds(274, 616, 575, 145);
            _preview.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            var enlarge = new Button { Text = "放大所選門示意圖", Left = 869, Top = 616, Width = 280, Height = 32, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            enlarge.Click += (s, e) => ShowSelectedDoorPreview();
            var reserve = new Button { Text = "預覽並預留（Reserved）", Left = 869, Top = 656, Width = 280, Height = 42, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            reserve.Click += async (s, e) => await ReserveAsync();
            var swap = new Button { Text = "執行 B-1 替換", Left = 869, Top = 710, Width = 280, Height = 42, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            swap.Click += async (s, e) => await ExecuteSwapAsync();

            Controls.AddRange(new Control[] { context, input, _status, sourceDetails, suggestions, submit, _grid, _doorPreview, _preview, enlarge, reserve, swap });
        }

        private static void Add(TableLayoutPanel panel, int cell, string label, Control control)
        {
            int row = cell / 6;
            int col = cell % 6;
            var caption = new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };
            if (label.StartsWith("＊"))
            {
                caption.BackColor = Color.MistyRose;
                caption.ForeColor = Color.DarkRed;
                caption.Font = new Font(caption.Font, FontStyle.Bold);
            }
            panel.Controls.Add(caption, col, row);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(3);
            panel.Controls.Add(control, col + 1, row);
        }

        private void ConfigureGrid()
        {
            _grid.AutoGenerateColumns = false;
            _grid.AllowUserToAddRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            AddColumn("Result", "結果", 105); AddColumn("PassportId", "護照", 90);
            AddColumn("TypeName", "來源類型", 150); AddColumn("Dimensions", "寬×高", 105);
            AddColumn("Operation", "開啟／方向", 130); AddColumn("Use", "用途", 75);
            AddColumn("Material", "材質", 100);
            AddColumn("Condition", "狀況（參考）", 105); AddColumn("Reason", "有色欄位待確認／不符合原因", 300);
        }

        private void AddColumn(string name, string header, int width) =>
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, ReadOnly = true });

        private void FillFromModel()
        {
            _width.Text = InputNumber(_model.WidthMm); _height.Text = InputNumber(_model.HeightMm);
            _dimension.Text = string.IsNullOrWhiteSpace(_model.DimensionDefinition) ? "nominal" : _model.DimensionDefinition;
            _thickness.Text = InputNumber(_model.LeafThicknessMm); _operation.Text = _model.OperationType;
            _swing.Text = _model.SwingHanding; _use.Text = _model.UseType; _material.Text = _model.Material;
            _clearWidth.Text = InputNumber(_model.ClearWidthMm); _leafCount.Text = _model.LeafCount > 0 ? _model.LeafCount.ToString() : "1";
            foreach (var control in new Control[] { _dimension, _leafCount })
                control.BackColor = Color.AliceBlue;
            _tolerance.BackColor = Color.Honeydew;
            _tolerance.ForeColor = Color.DarkGreen;
            _installationReviewed.BackColor = Color.AliceBlue;
            _basis.BackColor = Color.AliceBlue;
        }

        private void SetupRequiredHighlighting()
        {
            foreach (var box in new Control[] { _width, _height, _thickness, _operation, _swing, _material, _clearWidth })
                box.TextChanged += (s, e) => UpdateRequiredColors();
            _use.TextChanged += (s, e) => UpdateRequiredColors();
            UpdateRequiredColors();
        }

        private System.Collections.Generic.Dictionary<string, Control> DemandFields() => new System.Collections.Generic.Dictionary<string, Control>
        {
            ["width"] = _width, ["height"] = _height, ["leafThickness"] = _thickness,
            ["operationType"] = _operation, ["swingHanding"] = _swing, ["useType"] = _use,
            ["material"] = _material, ["clearWidth"] = _clearWidth,
            ["leafCount"] = _leafCount,
        };

        private void SetupFieldSources()
        {
            foreach (var pair in DemandFields())
            {
                var control = pair.Value;
                string source = _model.FieldSources.TryGetValue(pair.Key, out var value) && value.Kind != "unknown"
                    ? $"模型帶入：{value.Scope} / {value.ParameterName}\n{value.Verification}"
                    : "模型未提供明確資料，請填入 B 模型的設計需求。";
                _sources.SetToolTip(control, source);
                control.TextChanged += (s, e) =>
                {
                    _demandRevision++;
                    _sources.SetToolTip(control, "目前為使用者編輯的需求值。");
                    _grid.Rows.Clear();
                    UpdatePreview();
                    ShowInputStatus();
                };
            }
            _sources.SetToolTip(_status, string.Join("\n", _mapping.Notes));
            ShowInputStatus();
        }

        private void ShowInputStatus()
        {
            var missing = RequiredMissing();
            _status.ForeColor = missing.Count > 0 ? Color.DarkRed : Color.DimGray;
            _status.Text = (missing.Count > 0 ? "尚需確認：" + string.Join("、", missing) : "需求欄位已填，可執行配對。")
                + (_mapping.Notes.Count > 0 ? "｜另有模型提示，請查看來源。" : "");
        }

        private void ApplySuggestions()
        {
            string summary = string.Join("\n\n", _mapping.Suggestions.Select(s => s.Label + " → " + s.Value + "\n" + s.Reason));
            if (MessageBox.Show(this, summary + "\n\n確認將以上建議帶入需求欄位？", "B 模型名稱建議", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            foreach (var suggestion in _mapping.Suggestions)
            {
                // Preserve later manual changes; users can always edit suggestions afterwards.
                var control = DemandFields()[suggestion.Field];
                if (suggestion.Field == "operationType" && !string.IsNullOrWhiteSpace(control.Text)) continue;
                if (suggestion.Field == "useType" && control.Text != _model.UseType) continue;
                if (suggestion.Field == "leafCount" && control.Text != _model.LeafCount.ToString()) continue;
                control.Text = suggestion.Value;
                _sources.SetToolTip(control, "已由使用者確認的名稱建議：" + suggestion.Reason);
            }
        }

        private async Task LoadSupplyChoicesAsync()
        {
            try
            {
                var supply = await BackendClient.ListPassportsAsync();
                if (IsDisposed) return;
                var available = supply.Where(p => p.ReuseStatus == "Approved" && p.Status == "Available" && p.Quantity > 0).ToList();
                AddChoices(_material, available.Select(p => p.Material));
                AddChoices(_operation, available.Select(p => p.OperationType));
                AddChoices(_swing, available.Select(p => p.SwingHanding));
                _sources.SetToolTip(_material, _sources.GetToolTip(_material) + "\n下拉選單另列目前核准供應的材質文字，可依設計需求選用或自行輸入。");
            }
            catch (Exception ex) { if (!IsDisposed) _sources.SetToolTip(_status, string.Join("\n", _mapping.Notes) + "\n供應選項載入失敗：" + ErrorText.RealMessage(ex)); }
        }

        private static void AddChoices(ComboBox combo, System.Collections.Generic.IEnumerable<string> values)
        {
            foreach (string value in values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct())
                if (!combo.Items.Contains(value)) combo.Items.Add(value);
        }

        private void UpdateRequiredColors()
        {
            SetRequiredColor(_width, Value(_width) > 0);
            SetRequiredColor(_height, Value(_height) > 0);
            SetRequiredColor(_thickness, Value(_thickness) > 0);
            SetRequiredColor(_operation, !string.IsNullOrWhiteSpace(_operation.Text));
            SetRequiredColor(_swing, !string.IsNullOrWhiteSpace(_swing.Text));
            SetRequiredColor(_use, !string.IsNullOrWhiteSpace(_use.Text));
            SetRequiredColor(_material, !string.IsNullOrWhiteSpace(_material.Text));
            SetRequiredColor(_clearWidth, Value(_clearWidth) > 0);
        }

        private static void SetRequiredColor(Control control, bool complete)
        {
            control.BackColor = complete ? Color.LemonChiffon : Color.MistyRose;
            control.ForeColor = complete ? Color.Black : Color.DarkRed;
        }

        private System.Collections.Generic.List<string> RequiredMissing()
        {
            var missing = new System.Collections.Generic.List<string>();
            if (Value(_width) <= 0) missing.Add("門寬");
            if (Value(_height) <= 0) missing.Add("門高");
            if (Value(_thickness) <= 0) missing.Add("門扇厚度");
            if (string.IsNullOrWhiteSpace(_operation.Text)) missing.Add("開啟方式");
            if (string.IsNullOrWhiteSpace(_swing.Text)) missing.Add("開門方向");
            if (string.IsNullOrWhiteSpace(_use.Text)) missing.Add("室內外用途");
            if (string.IsNullOrWhiteSpace(_material.Text)) missing.Add("材質");
            if (Value(_clearWidth) <= 0) missing.Add("有效通行淨寬");
            return missing;
        }

        private MatchRequestInfo BuildRequest()
        {
            return new MatchRequestInfo
            {
                WidthMm = Value(_width), HeightMm = Value(_height), DimensionDefinition = _dimension.Text.Trim(),
                ToleranceMm = MatchingToleranceMm, LeafThicknessMm = Value(_thickness), OperationType = _operation.Text.Trim(),
                SwingHanding = _swing.Text.Trim(), UseType = _use.Text.Trim(), MaterialPreference = _material.Text.Trim(),
                ClearWidthMinMm = Value(_clearWidth), LeafCount = (int)Value(_leafCount),
                DoorOpeningWidthMm = _model.WidthMm, HostWallThicknessMm = _hostWallThicknessMm,
                InstallationReviewed = _installationReviewed.Checked, Basis = _basis.Text.Trim(),
                TargetModel = _model.SourceModel, TargetUniqueId = _model.SourceUniqueId,
            };
        }

        private async Task SubmitAsync()
        {
            if (_matching) return;
            try
            {
                var missing = RequiredMissing();
                if (missing.Count > 0)
                {
                    _status.ForeColor = Color.DarkRed;
                    _status.Text = "請先完成必填項目：" + string.Join("、", missing);
                    return;
                }
                _status.ForeColor = Color.DimGray;
                _matching = true;
                int revision = _demandRevision;
                var request = BuildRequest();
                var results = await BackendClient.MatchPassportsAsync(request);
                if (IsDisposed) return;
                if (revision != _demandRevision)
                {
                    _status.Text = "需求已修改，請重新執行配對。";
                    return;
                }
                _grid.Rows.Clear();
                foreach (var result in results)
                {
                    var p = result.Passport;
                    string reasons = string.Join("；", (result.Checks ?? new System.Collections.Generic.List<MatchCheckInfo>())
                        .Where(x => x.State != "pass").Select(x => x.Key + "：" + x.Reason));
                    int i = _grid.Rows.Add(result.Result, p.PassportId, p.TypeName,
                        $"{Display(p.WidthMm)}×{Display(p.HeightMm)}", p.OperationType + "／" + p.SwingHanding,
                        p.UseType, p.Material,
                        p.ConditionGrade + " " + p.Condition, reasons);
                    _grid.Rows[i].Tag = result;
                    if (result.Result == "Matched") _grid.Rows[i].DefaultCellStyle.BackColor = Color.Honeydew;
                    else if (result.Result == "Pending Review") _grid.Rows[i].DefaultCellStyle.BackColor = Color.LemonChiffon;
                    else _grid.Rows[i].DefaultCellStyle.ForeColor = Color.Gray;
                }
                _status.Text = $"僅按紅色＊欄位配對｜共 {results.Count} 筆核准供應：Matched {results.Count(x => x.Result == "Matched")}、Pending Review {results.Count(x => x.Result == "Pending Review")}、Not Matched {results.Count(x => x.Result == "Not Matched")}。";
                if (_grid.Rows.Count > 0) _grid.Rows[0].Selected = true;
            }
            catch (Exception ex) { _status.Text = "配對失敗：" + ErrorText.RealMessage(ex); }
            finally { _matching = false; }
        }

        private void UpdatePreview()
        {
            if (!(_grid.CurrentRow?.Tag is MatchResultInfo result))
            {
                _preview.Clear();
                ReplaceDoorPreview(null);
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"{result.Passport.PassportId}｜{result.Result}｜目前供應狀態 {result.Passport.Status}");
            sb.AppendLine($"A 模型門：{result.Passport.TypeName}｜{Display(result.Passport.WidthMm)} × {Display(result.Passport.HeightMm)} mm");
            sb.AppendLine("左側為門示意圖；點圖、按放大按鈕或雙擊結果列可開啟大圖。");
            foreach (var check in result.Checks ?? new System.Collections.Generic.List<MatchCheckInfo>())
                sb.AppendLine($"{(check.State == "pass" ? "✓" : check.State == "fail" ? "✕" : "?")} {check.Key}：{check.Reason}");
            _preview.Text = sb.ToString();
            ReplaceDoorPreview(DoorPreviewRenderer.CreatePreview(result.Passport, new Size(700, 480)));
        }

        private void ShowSelectedDoorPreview()
        {
            if (!(_grid.CurrentRow?.Tag is MatchResultInfo result))
            {
                _status.Text = "請先選取一筆配對結果。";
                return;
            }
            DoorPreviewRenderer.ShowPreviewDialog(this, result.Passport);
        }

        private void ReplaceDoorPreview(Image next)
        {
            var previous = _doorPreview.Image;
            _doorPreview.Image = next;
            previous?.Dispose();
        }

        private async Task ReserveAsync()
        {
            var result = _grid.CurrentRow?.Tag as MatchResultInfo;
            if (result == null) { _status.Text = "請先選取一筆配對結果。"; return; }
            if (result.Result != "Matched") { _status.Text = "只有所有必要條件都通過的 Matched 護照可以預留；待確認資料不可略過。"; return; }
            try
            {
                try { _uiDoc.Selection.SetElementIds(new[] { _door.Id }); _uiDoc.ShowElements(_door.Id); } catch { }
                var updated = await BackendClient.PatchPassportAsync(result.Passport.Id, status: "Reserved", reason: BuildRequest().Basis, actor: _actor);
                result.Passport = updated;
                _status.Text = $"{updated.PassportId} 已預留。確認預覽內容後，可執行 B-1 替換。";
                UpdatePreview();
            }
            catch (Exception ex) { _status.Text = "預留失敗：" + ErrorText.RealMessage(ex); }
        }

        private async Task ExecuteSwapAsync()
        {
            var result = _grid.CurrentRow?.Tag as MatchResultInfo;
            if (result == null || result.Result != "Matched") { _status.Text = "只能替換 Matched 護照。"; return; }
            if (result.Passport.Status != "Reserved") { _status.Text = "請先按「預覽並預留」，避免同一護照被重複配置。"; return; }
            if (MessageBox.Show($"確定以 {result.Passport.PassportId} 建立 B-1 替換？\n將載入來源門族群與材質；請確認 A 模型已在同一個 Revit 開啟。", "確認 B-1 替換", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            DoorSwapAudit audit;
            try
            {
                audit = DoorSwapHelper.Swap(_door.Document, _door, result.Passport);
            }
            catch (Exception ex)
            {
                _status.Text = "Revit 替換失敗，Transaction 已回復：" + ErrorText.RealMessage(ex);
                return;
            }

            try
            {
                var updated = await BackendClient.PatchPassportAsync(
                    result.Passport.Id, "Allocated", _door.Document.Title, _door.Id.Value.ToString(CultureInfo.InvariantCulture),
                    audit.TargetUniqueId, audit.OriginalUniqueId, BuildRequest().Basis, _actor,
                    audit.OriginalTypeId, audit.NewTypeId);
                result.Passport = updated;
                _status.Text = $"完成：{updated.PassportId} 已套用來源族群與材質；請到 3D 著色／擬真視圖檢查外觀並儲存模型。";
                UpdatePreview();
            }
            catch (Exception ex)
            {
                _status.Text = "Revit 已完成替換，但後端稽核更新失敗；護照仍為 Reserved，請勿重複替換並重新啟動後端後補登：" + ErrorText.RealMessage(ex);
            }
        }

        private static TextBox Box(string value = "") => new TextBox { Text = value };
        private static ComboBox Combo(params string[] values)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
            combo.Items.AddRange(values.Cast<object>().ToArray());
            if (values.Length > 0) combo.Text = values[0];
            return combo;
        }
        private static double Value(Control box) => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ? value : 0;
        private static string InputNumber(double value) => value > 0 ? value.ToString("0.##", CultureInfo.CurrentCulture) : "";
        private static string Display(double value) => value > 0 ? value.ToString("0.##", CultureInfo.CurrentCulture) : "待確認";

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ReplaceDoorPreview(null);
            _sources.Dispose();
            base.OnFormClosed(e);
        }
    }
}
