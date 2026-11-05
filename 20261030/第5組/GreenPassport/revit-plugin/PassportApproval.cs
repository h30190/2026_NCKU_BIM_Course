using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace TemporalBim
{
    public static class PassportApproval
    {
        public static ReviewPassportInfo Request(PassportInfo passport, string inspector, string note)
        {
            var review = JsonConvert.DeserializeObject<ReviewPassportInfo>(JsonConvert.SerializeObject(passport));
            review.ReuseStatus = "Approved";
            review.InspectedBy = inspector.Trim();
            review.ReviewNote = note.Trim();
            review.InspectedOn = DateTime.Now.ToString("yyyy-MM-dd");
            return review;
        }

        public static List<string> BlockingReasons(PassportInfo passport, string inspector, string note)
        {
            var p = Request(passport, inspector, note);
            var missing = new List<string>();
            if (p.WidthMm <= 0) missing.Add("門寬");
            if (p.HeightMm <= 0) missing.Add("門高");
            if (p.LeafThicknessMm <= 0) missing.Add("門扇厚度");
            if (string.IsNullOrWhiteSpace(p.OperationType)) missing.Add("開啟方式");
            if (string.IsNullOrWhiteSpace(p.SwingHanding)) missing.Add("開門方向");
            if (string.IsNullOrWhiteSpace(p.UseType)) missing.Add("室內外用途");
            if (string.IsNullOrWhiteSpace(p.Material)) missing.Add("材質");
            if (p.ClearWidthMm <= 0) missing.Add("有效通行淨寬");
            if (string.IsNullOrWhiteSpace(p.DimensionDefinition)) missing.Add("尺寸定義");
            if (string.IsNullOrWhiteSpace(p.InspectedBy)) missing.Add("檢核人");
            if (string.IsNullOrWhiteSpace(p.ReviewNote)) missing.Add("審核說明");
            if (p.Quantity < 0) missing.Add("數量不可小於零");
            return missing;
        }

    }

    /// <summary>Reviews all pending records explicitly, showing each blocked record before submitting.</summary>
    public class PassportBatchApprovalForm : Form
    {
        private readonly List<PassportInfo> _passports;
        private readonly TextBox _inspector = new TextBox();
        private readonly TextBox _note = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical };
        private readonly DataGridView _grid = new DataGridView();
        private readonly Button _approve = new Button { AutoSize = true, Text = "全部通過審核", Height = 36 };
        public List<PassportInfo> Ready { get; } = new List<PassportInfo>();
        public string Inspector => _inspector.Text.Trim();
        public string ReviewNote => _note.Text.Trim();

        public PassportBatchApprovalForm(List<PassportInfo> passports, string inspector)
        {
            _passports = passports;
            Text = "全部通過審核｜逐筆確認結果";
            Size = new Size(900, 600);
            MinimumSize = new Size(720, 480);
            StartPosition = FormStartPosition.CenterParent;
            var fields = new TableLayoutPanel { Dock = DockStyle.Top, Height = 130, ColumnCount = 2, RowCount = 3, Padding = new Padding(10) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fields.Controls.Add(new Label { Text = "檢核人（必填）", AutoSize = true }, 0, 0);
            fields.Controls.Add(_inspector, 1, 0);
            fields.Controls.Add(new Label { Text = "審核說明（必填）", AutoSize = true }, 0, 1);
            fields.Controls.Add(_note, 1, 1);
            fields.Controls.Add(new Label { Text = "只處理待審核項目；已通過、需修復、未通過、預留或配置的項目保留原狀。", AutoSize = true }, 0, 2);
            fields.SetColumnSpan(fields.GetControlFromPosition(0, 2), 2);
            _inspector.Dock = DockStyle.Fill;
            _note.Dock = DockStyle.Fill;
            _inspector.Text = inspector ?? "";
            _note.Text = "條件符合";
            _grid.Dock = DockStyle.Fill;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.RowHeadersVisible = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.Columns.Add("Passport", "護照／門型");
            _grid.Columns.Add("Result", "批次審核預覽");
            _grid.Columns[1].FillWeight = 180;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
            buttons.Controls.Add(_approve);
            var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(cancel);
            _approve.Click += (s, e) => { if (Ready.Count > 0) DialogResult = DialogResult.OK; };
            CancelButton = cancel;
            Controls.Add(_grid);
            Controls.Add(fields);
            Controls.Add(buttons);
            _inspector.TextChanged += (s, e) => RefreshResults();
            _note.TextChanged += (s, e) => RefreshResults();
            RefreshResults();
        }

        private void RefreshResults()
        {
            Ready.Clear();
            _grid.Rows.Clear();
            foreach (var p in _passports)
            {
                bool pending = p.ReuseStatus == "Pending" && (p.Status == "Available" || p.Status == "Unavailable");
                var reasons = pending ? PassportApproval.BlockingReasons(p, Inspector, ReviewNote) : new List<string>();
                bool ready = pending && reasons.Count == 0;
                if (ready) Ready.Add(p);
                int row = _grid.Rows.Add((p.PassportId.Length > 0 ? p.PassportId : p.TypeName),
                    !pending ? p.ReuseStatus == "Approved" ? "已通過審核" : "略過：" + ReviewStatusDisplay.Label(p.ReuseStatus) : ready ? "可通過審核（尚未提交）" : "待審核：" + string.Join("、", reasons));
                _grid.Rows[row].DefaultCellStyle.BackColor = ready || p.ReuseStatus == "Approved" ? Color.Honeydew : pending ? Color.MistyRose : Color.WhiteSmoke;
            }
            _approve.Enabled = Ready.Count > 0;
            _approve.Text = $"全部通過審核（{Ready.Count} 筆可通過）";
        }
    }
}
