using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace TemporalBim
{
    /// <summary>A 模型後續實體檢核，可補填核心欄位；不會改寫原始 RVT 參數。</summary>
    public class PassportReviewForm : Form
    {
        private readonly PassportInfo _passport;

        private readonly TextBox _width = Box();
        private readonly TextBox _height = Box();
        private readonly ComboBox _dimension = Combo("nominal", "leaf", "frame_outer");
        private readonly TextBox _thickness = Box();
        private readonly TextBox _operation = Box();
        private readonly TextBox _swing = Box();
        private readonly ComboBox _use = Combo("", "Interior", "Exterior");
        private readonly TextBox _material = Box();
        private readonly TextBox _clearWidth = Box();

        private readonly ComboBox _status = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox _condition = Box();
        private readonly TextBox _grade = Box();
        private readonly TextBox _note = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical };
        private readonly TextBox _inspector = Box();
        private readonly TextBox _date = Box();
        private readonly NumericUpDown _quantity = new NumericUpDown { Minimum = 0, Maximum = 100000 };
        private readonly NumericUpDown _price = new NumericUpDown { Maximum = 100000000, DecimalPlaces = 0 };

        public ReviewPassportInfo Review { get; private set; }
        private string SelectedStatus
        {
            get => (_status.SelectedItem as ReviewStatusOption)?.Code ?? "Pending";
            set
            {
                foreach (var item in _status.Items.Cast<ReviewStatusOption>())
                    if (item.Code == value) { _status.SelectedItem = item; return; }
                _status.SelectedIndex = 0;
            }
        }

        public PassportReviewForm(PassportInfo passport)
        {
            _passport = passport;
            PassportAutoFill.Apply(_passport);
            Text = "護照人工檢核與必填資料｜" + passport.PassportId;
            Size = new Size(760, 690);
            MinimumSize = new Size(680, 600);
            StartPosition = FormStartPosition.CenterParent;
            Build();
            Fill();
            SetupRequiredHighlighting();
        }

        private void Build()
        {
            var info = new Label
            {
                Text = $"{_passport.Family} / {_passport.TypeName}｜來源 {_passport.SourceModel} / Element {_passport.SourceElementId}\n淡黃色＝＊通過審核前必填；淡紅色＝尚未完成。人工修正會保留資料來源紀錄。",
                Dock = DockStyle.Top,
                Height = 48,
                Padding = new Padding(12, 8, 12, 4),
            };
            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildCoreTab());
            tabs.TabPages.Add(BuildReviewTab());

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, MinimumSize = new Size(0, 56), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var save = new Button { Text = "儲存審核結果", Width = 155, Height = 36 };
            save.Click += Save_Click;
            var approve = new Button { Text = "通過此項審核", Width = 155, Height = 36, BackColor = Color.Honeydew };
            approve.Click += (s, e) => { SelectedStatus = "Approved"; Save_Click(s, e); };
            bottom.Controls.Add(approve);
            bottom.Controls.Add(save);

            Controls.Add(tabs);
            Controls.Add(bottom);
            Controls.Add(info);
            AcceptButton = save;
        }

        private TabPage BuildCoreTab()
        {
            var page = new TabPage("核心必填資料");
            var table = Table(9);
            Add(table, 0, "＊門寬 mm", _width); Add(table, 1, "＊門高 mm", _height);
            Add(table, 2, "尺寸定義", _dimension); Add(table, 3, "＊門扇厚度 mm", _thickness);
            Add(table, 4, "＊開啟方式", _operation); Add(table, 5, "＊開門方向", _swing);
            Add(table, 6, "＊室內外用途", _use); Add(table, 7, "＊材質", _material);
            Add(table, 8, "＊有效通行淨寬 mm", _clearWidth);
            page.Controls.Add(table);
            return page;
        }

        private TabPage BuildReviewTab()
        {
            var page = new TabPage("實體檢核與供應資格");
            var table = Table(8);
            _status.DropDownStyle = ComboBoxStyle.DropDownList;
            _status.DisplayMember = "Label";
            _status.Items.AddRange(ReviewStatusDisplay.Options.Cast<object>().ToArray());
            _status.SelectedIndexChanged += (s, e) =>
            {
                string status = SelectedStatus;
                _status.BackColor = ReviewStatusDisplay.Background(status);
                _status.ForeColor = ReviewStatusDisplay.Foreground(status);
            };
            _note.Multiline = true;
            Add(table, 0, "審核結果", _status); Add(table, 1, "保存狀況", _condition);
            Add(table, 2, "檢核等級", _grade); Add(table, 3, "審核／修復／排除說明", _note);
            Add(table, 4, "檢核人", _inspector); Add(table, 5, "檢核日期", _date);
            Add(table, 6, "可供應數量", _quantity); Add(table, 7, "價格 NT$", _price);
            page.Controls.Add(table);
            return page;
        }

        private static TableLayoutPanel Table(int rows)
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = rows, CellBorderStyle = TableLayoutPanelCellBorderStyle.Single };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < rows; i++) table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
            return table;
        }

        private static void Add(TableLayoutPanel table, int row, string label, Control control)
        {
            var caption = new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(5, 0, 0, 0) };
            if (label.StartsWith("＊"))
            {
                caption.BackColor = Color.Goldenrod;
                caption.Font = new Font(caption.Font, FontStyle.Bold);
            }
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(4);
            table.Controls.Add(caption, 0, row);
            table.Controls.Add(control, 1, row);
        }

        private void Fill()
        {
            _width.Text = Show(_passport.WidthMm); _height.Text = Show(_passport.HeightMm);
            _dimension.Text = string.IsNullOrWhiteSpace(_passport.DimensionDefinition) ? "nominal" : _passport.DimensionDefinition;
            _thickness.Text = Show(_passport.LeafThicknessMm); _operation.Text = _passport.OperationType;
            _swing.Text = _passport.SwingHanding; _use.Text = _passport.UseType; _material.Text = _passport.Material;
            _clearWidth.Text = Show(_passport.ClearWidthMm);

            SelectedStatus = string.IsNullOrWhiteSpace(_passport.ReuseStatus) ? "Pending" : _passport.ReuseStatus;
            _condition.Text = _passport.Condition; _grade.Text = _passport.ConditionGrade;
            _note.Text = string.IsNullOrWhiteSpace(_passport.ReviewNote) ? "條件符合" : _passport.ReviewNote;
            _inspector.Text = _passport.InspectedBy;
            _date.Text = string.IsNullOrWhiteSpace(_passport.InspectedOn) ? DateTime.Now.ToString("yyyy-MM-dd") : _passport.InspectedOn;
            _quantity.Value = Math.Max(0, Math.Min((int)_quantity.Maximum, _passport.Quantity));
            _price.Value = Math.Max(0, Math.Min(_price.Maximum, (decimal)_passport.PriceNtd));
            _dimension.BackColor = Color.AliceBlue;
        }

        private void SetupRequiredHighlighting()
        {
            foreach (var box in new[] { _width, _height, _thickness, _operation, _swing, _material, _clearWidth })
                box.TextChanged += (s, e) => UpdateRequiredColors();
            _use.TextChanged += (s, e) => UpdateRequiredColors();
            UpdateRequiredColors();
        }

        private void UpdateRequiredColors()
        {
            SetRequired(_width, Number(_width) > 0); SetRequired(_height, Number(_height) > 0);
            SetRequired(_thickness, Number(_thickness) > 0); SetRequired(_operation, !string.IsNullOrWhiteSpace(_operation.Text));
            SetRequired(_swing, !string.IsNullOrWhiteSpace(_swing.Text)); SetRequired(_use, !string.IsNullOrWhiteSpace(_use.Text));
            SetRequired(_material, !string.IsNullOrWhiteSpace(_material.Text)); SetRequired(_clearWidth, Number(_clearWidth) > 0);
        }

        private static void SetRequired(Control control, bool complete)
        {
            control.BackColor = complete ? Color.LemonChiffon : Color.MistyRose;
            control.ForeColor = complete ? Color.Black : Color.DarkRed;
        }

        private void Save_Click(object sender, EventArgs e)
        {
            var missing = RequiredMissing();
            string status = SelectedStatus;
            if (status == "Approved" && missing.Count > 0)
            {
                MessageBox.Show("通過審核前請完成：\n• " + string.Join("\n• ", missing), "必填資料尚未完整");
                return;
            }
            if (status == "Approved" && (string.IsNullOrWhiteSpace(_note.Text) || string.IsNullOrWhiteSpace(_inspector.Text)))
            {
                MessageBox.Show("通過審核需要填寫審核說明與檢核人。");
                return;
            }
            if ((status == "Repair Required" || status == "Rejected") && string.IsNullOrWhiteSpace(_note.Text))
            {
                MessageBox.Show("修復或排除狀態必須填寫原因。");
                return;
            }

            Review = new ReviewPassportInfo
            {
                WidthMm = Number(_width),
                HeightMm = Number(_height),
                DimensionDefinition = _dimension.Text.Trim(),
                LeafThicknessMm = Number(_thickness),
                OperationType = _operation.Text.Trim(),
                SwingHanding = _swing.Text.Trim(),
                UseType = _use.Text.Trim(),
                Material = _material.Text.Trim(),
                ClearWidthMm = Number(_clearWidth),
                ReuseStatus = status,
                Condition = _condition.Text.Trim(),
                ConditionGrade = _grade.Text.Trim(),
                ReviewNote = _note.Text.Trim(),
                InspectedBy = _inspector.Text.Trim(),
                InspectedOn = _date.Text.Trim(),
                Quantity = (int)_quantity.Value,
                PriceNtd = (double)_price.Value,
                Revision = _passport.Revision,
            };
            DialogResult = DialogResult.OK;
        }

        private List<string> RequiredMissing()
        {
            var missing = new List<string>();
            if (Number(_width) <= 0) missing.Add("門寬"); if (Number(_height) <= 0) missing.Add("門高");
            if (Number(_thickness) <= 0) missing.Add("門扇厚度"); if (string.IsNullOrWhiteSpace(_operation.Text)) missing.Add("開啟方式");
            if (string.IsNullOrWhiteSpace(_swing.Text)) missing.Add("開門方向"); if (string.IsNullOrWhiteSpace(_use.Text)) missing.Add("室內外用途");
            if (string.IsNullOrWhiteSpace(_material.Text)) missing.Add("材質"); if (Number(_clearWidth) <= 0) missing.Add("有效通行淨寬");
            if (string.IsNullOrWhiteSpace(_dimension.Text)) missing.Add("尺寸定義");
            return missing;
        }

        private static TextBox Box(string value = "") => new TextBox { Text = value };
        private static ComboBox Combo(params string[] values)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
            combo.Items.AddRange(values.Cast<object>().ToArray());
            if (values.Length > 0) combo.Text = values[0];
            return combo;
        }
        private static double Number(TextBox box) => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ? value : 0;
        private static string Show(double value) => value > 0 ? value.ToString("0.##", CultureInfo.CurrentCulture) : "";
    }
}
