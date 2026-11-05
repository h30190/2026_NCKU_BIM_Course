using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Revit.UI;
// Autodesk.Revit.UI 自己也有 ComboBox / TextBox 類別（Ribbon 用的），
// 跟 System.Windows.Forms 同名會打架，這裡明確指定用 WinForms 的版本。
using ComboBox = System.Windows.Forms.ComboBox;
using TextBox = System.Windows.Forms.TextBox;

namespace TemporalBim
{
    /// <summary>
    /// 歷史追溯面板：整合版本總覽、版本比較、Diff 清單、點擊定位，全部在同一個彈出視窗裡。
    /// 用 Form.Show()（modeless）開啟，不是 ShowDialog()——
    /// 所以這個視窗裡任何要呼叫 Revit API 的地方，都要透過 App.LocateEvent 這個
    /// ExternalEvent，不能直接呼叫（細節看 LocateRequestHandler.cs）。
    /// </summary>
    public class HistoryForm : Form
    {
        private readonly UIDocument _uiDoc;

        private ListBox _lstVersions;
        private Button _btnRefresh;
        private ComboBox _cmbFrom;
        private ComboBox _cmbTo;
        private Button _btnCompare;
        private ListView _lstDiff;
        private TextBox _txtDetail;
        private Label _lblStatus;

        private VersionInfo _currentFrom;
        private VersionInfo _currentTo;

        public HistoryForm(UIDocument uiDoc)
        {
            _uiDoc = uiDoc;
            BuildLayout();
            Load += async (s, e) => await RefreshVersionsAsync();
        }

        private void BuildLayout()
        {
            Text = "Temporal BIM 歷史追溯面板";
            Size = new Size(780, 760);
            MinimumSize = new Size(640, 520);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(80, 80);
            FormBorderStyle = FormBorderStyle.Sizable;
            SizeGripStyle = SizeGripStyle.Show;

            const int left = 16;
            int formWidth = ClientSize.Width;
            int y = 12;

            var lblVersions = new Label { Text = "版本總覽", Top = y, Left = left, Width = 200, Height = 20, Font = new Font(Font, FontStyle.Bold) };
            lblVersions.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            y += 24;

            _lstVersions = new ListBox { Top = y, Left = left, Width = formWidth - 32, Height = 90 };
            _lstVersions.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _btnRefresh = new Button { Text = "重新整理版本清單", Top = y + 94, Left = formWidth - 32 - 150, Width = 150, Height = 26 };
            _btnRefresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnRefresh.Click += async (s, e) => await RefreshVersionsAsync();
            y += 128;

            var lblCompare = new Label { Text = "版本比較", Top = y, Left = left, Width = 200, Height = 20, Font = new Font(Font, FontStyle.Bold) };
            lblCompare.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            y += 24;

            var lblFrom = new Label { Text = "版本 A", Top = y + 4, Left = left, Width = 50, Height = 20 };
            lblFrom.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _cmbFrom = new ComboBox { Top = y, Left = left + 54, Width = 280, Height = 26, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbFrom.Anchor = AnchorStyles.Top | AnchorStyles.Left;

            var lblTo = new Label { Text = "版本 B", Top = y + 4, Left = left + 350, Width = 50, Height = 20 };
            lblTo.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _cmbTo = new ComboBox { Top = y, Left = left + 404, Width = 280, Height = 26, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbTo.Anchor = AnchorStyles.Top | AnchorStyles.Left;

            _btnCompare = new Button { Text = "比較", Top = y, Left = formWidth - 32 - 68, Width = 68, Height = 26 };
            _btnCompare.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnCompare.Click += async (s, e) => await CompareAsync();
            y += 40;

            _lblStatus = new Label { Text = "", Top = y, Left = left, Width = formWidth - 32, Height = 20, ForeColor = Color.DimGray };
            _lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            y += 26;

            _lstDiff = new ListView
            {
                Top = y,
                Left = left,
                Width = formWidth - 32,
                Height = 240,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                MultiSelect = false,
            };
            _lstDiff.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _lstDiff.Columns.Add("分類", 70);
            _lstDiff.Columns.Add("元件類型", 160);
            _lstDiff.Columns.Add("材質", 140);
            _lstDiff.Columns.Add("異動摘要", 280);
            _lstDiff.SelectedIndexChanged += LstDiff_SelectedIndexChanged;
            int diffBottom = y + 240;
            y = diffBottom + 12;

            var lblDetail = new Label { Text = "詳細資訊（點清單裡任一列）", Top = y, Left = left, Width = 300, Height = 20, Font = new Font(Font, FontStyle.Bold) };
            lblDetail.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            y += 24;

            _txtDetail = new TextBox
            {
                Top = y,
                Left = left,
                Width = formWidth - 32,
                Height = Math.Max(120, ClientSize.Height - y - 16),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White,
            };
            _txtDetail.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            Controls.AddRange(new Control[]
            {
                lblVersions, _lstVersions, _btnRefresh,
                lblCompare, lblFrom, _cmbFrom, lblTo, _cmbTo, _btnCompare,
                _lblStatus, _lstDiff, lblDetail, _txtDetail,
            });
        }

        private async System.Threading.Tasks.Task RefreshVersionsAsync()
        {
            try
            {
                var versions = await BackendClient.ListVersionsAsync();
                _lstVersions.Items.Clear();
                _lstVersions.Items.AddRange(versions.Cast<object>().ToArray());

                object prevFrom = _cmbFrom.SelectedItem;
                object prevTo = _cmbTo.SelectedItem;
                _cmbFrom.Items.Clear();
                _cmbTo.Items.Clear();
                _cmbFrom.Items.AddRange(versions.Cast<object>().ToArray());
                _cmbTo.Items.AddRange(versions.Cast<object>().ToArray());
                if (versions.Count > 0)
                {
                    _cmbFrom.SelectedIndex = Math.Max(0, versions.Count - 2 >= 0 ? versions.Count - 2 : 0);
                    _cmbTo.SelectedIndex = versions.Count - 1;
                }
                _lblStatus.Text = $"目前共有 {versions.Count} 個版本。";
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "抓不到版本清單（後端有跑嗎？go run ./backend）：" + ex.Message;
            }
        }

        private async System.Threading.Tasks.Task CompareAsync()
        {
            var from = _cmbFrom.SelectedItem as VersionInfo;
            var to = _cmbTo.SelectedItem as VersionInfo;
            if (from == null || to == null)
            {
                _lblStatus.Text = "請先選版本 A 跟版本 B。";
                return;
            }
            if (from.Id == to.Id)
            {
                _lblStatus.Text = "版本 A 跟版本 B 不能選同一個。";
                return;
            }

            try
            {
                var diff = await BackendClient.GetDiffAsync(from.Id, to.Id);
                _currentFrom = from;
                _currentTo = to;
                PopulateDiffList(diff);
                _lblStatus.Text = $"{from.Label} → {to.Label}：共 {diff.Changes.Count} 項異動。";
                _txtDetail.Text = "";
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "比較失敗：" + ex.Message;
            }
        }

        private void PopulateDiffList(DiffResult diff)
        {
            _lstDiff.Items.Clear();
            foreach (var change in diff.Changes)
            {
                var item = new ListViewItem(change.ChangeTypeLabel);
                item.SubItems.Add(change.LastSnapshot?.TypeName ?? "");
                item.SubItems.Add(change.LastSnapshot?.Material ?? "");
                item.SubItems.Add(SummarizeChange(change));
                item.Tag = change;
                _lstDiff.Items.Add(item);
            }
        }

        private string SummarizeChange(ChangeEventInfo change)
        {
            if (change.ChangeType == "modified" && change.ChangedFields != null && change.ChangedFields.Count > 0)
                return string.Join("；", change.ChangedFields.Select(f => $"{f.Field}: {f.OldValue} → {f.NewValue}"));
            if (change.ChangeType == "added") return "此版本新增";
            if (change.ChangeType == "removed") return "此版本移除";
            return "";
        }

        /// <summary>
        /// 第 6 步的統一規則：點清單任一列，先查這個 UniqueId 在「目前真正開啟的模型」
        /// 裡存不存在，不要用 diff 分類（新增/移除/修改）去猜。
        /// </summary>
        private void LstDiff_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_lstDiff.SelectedItems.Count == 0) return;
            var change = _lstDiff.SelectedItems[0].Tag as ChangeEventInfo;
            if (change == null) return;

            _txtDetail.Text = "正在查詢模型……";
            App.LocateHandler.TargetUniqueId = change.UniqueId;
            App.LocateHandler.Completed = null; // 先清掉上一次掛的事件，不然會疊加重複觸發
            App.LocateHandler.Completed += found => OnLocateCompleted(found, change);
            App.LocateEvent.Raise();
        }

        private void OnLocateCompleted(bool found, ChangeEventInfo change)
        {
            if (found)
                ShowCaseA(change);
            else
                ShowCaseB(change);
        }

        /// <summary>Case A：元件還在，已經選取＋定位完成，這裡只負責顯示異動內容。</summary>
        private void ShowCaseA(ChangeEventInfo change)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【已在 Revit 中選取並定位】");
            sb.AppendLine($"UniqueId：{change.UniqueId}");
            sb.AppendLine($"類型：{change.LastSnapshot?.TypeName}");
            sb.AppendLine($"材質：{change.LastSnapshot?.Material}");
            if (change.ChangeType == "modified" && change.ChangedFields != null)
            {
                sb.AppendLine();
                sb.AppendLine("異動欄位（舊值 → 新值）：");
                foreach (var f in change.ChangedFields)
                    sb.AppendLine($"・{f.Field}：{f.OldValue} → {f.NewValue}");
            }
            _txtDetail.Text = sb.ToString();
        }

        /// <summary>Case B：元件現在找不到了，顯示歷史卡片。</summary>
        private void ShowCaseB(ChangeEventInfo change)
        {
            var snap = change.LastSnapshot;
            var sb = new StringBuilder();
            sb.AppendLine("【這個元件目前已不存在，顯示歷史卡片】");
            sb.AppendLine($"元件類型：{snap?.TypeName}");
            sb.AppendLine($"材質：{snap?.Material}");
            sb.AppendLine($"數量：{snap?.Quantity}");

            string lastVersionLabel = change.ChangeType == "removed" ? _currentFrom?.Label : _currentTo?.Label;
            sb.AppendLine($"最後出現的版本：{lastVersionLabel}");

            if (change.ChangeType == "removed")
                sb.AppendLine($"消失區間：{_currentFrom?.Label} → {_currentTo?.Label} 之間消失");

            if (snap != null)
            {
                bool hasPosition = snap.PositionX != 0 || snap.PositionY != 0 || snap.PositionZ != 0 || !string.IsNullOrEmpty(snap.LevelName);
                sb.AppendLine(hasPosition
                    ? $"最後位置：{snap.LevelName} 樓層，座標約 ({snap.PositionX:0.#}, {snap.PositionY:0.#}, {snap.PositionZ:0.#}) mm"
                    : "最後位置：位置資訊未保存");
            }
            _txtDetail.Text = sb.ToString();
        }
    }
}
