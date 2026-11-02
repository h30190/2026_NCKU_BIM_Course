using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using LebrCarbon;
using Visibility = System.Windows.Visibility; // Revit SDK 的全域 using 也有 Autodesk.Revit.DB.Visibility

namespace LebrCarbon.UI
{
    /// <summary>
    /// 結果視窗：等級、四張重點卡、等級刻度與五個分頁，可匯出結果 JSON。
    /// 選取元件透過建構子傳入的委派完成，本身不使用 Revit API，可在 Revit 外預覽。
    /// </summary>
    public partial class ResultWindow : Window
    {
        private readonly LebrResult _result;
        private readonly Func<IList<long>, bool> _selectElements;

        public ResultWindow(LebrResult result, Func<IList<long>, bool> selectElements)
        {
            _result = result;
            _selectElements = selectElements;
            InitializeComponent();
            DataContext = new ResultView(result);
        }

        private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var item = ItemGrid.SelectedItem as ItemResult;
            if (item == null) return;
            var selected = _selectElements != null && item.ElementIds != null && item.ElementIds.Count > 0
                           && _selectElements(item.ElementIds);
            Status.Text = selected
                ? "已在模型中選取「" + item.Name + "」的 " + item.ElementIds.Count + " 個元件。"
                : "「" + item.Name + "」沒有對應的 Revit 元件（例如範例資料）。";
        }

        private void OnExport(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "結果 JSON (*.json)|*.json",
                FileName = "LEBR結果_" + _result.CalculatedAt.ToString("yyyyMMdd_HHmm") + ".json",
                Title = "匯出結果給網頁",
            };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllText(dialog.FileName, _result.ToJson(), new UTF8Encoding(false));
            Status.Text = "已匯出：" + dialog.FileName;
        }

        /// <summary>
        /// 「在圖台開啟」：結果 JSON 壓縮後放在圖台網址的 #d= 後面，用預設瀏覽器開啟，並把網址複製到剪貼簿。
        /// 圖台網址讀外掛資料夾的 dashboard_url.txt，沒有就用 DashboardLink.DefaultBaseUrl。
        /// </summary>
        private void OnOpenDashboard(object sender, RoutedEventArgs e)
        {
            var baseUrl = DashboardLink.ResolveBaseUrl(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
            var json = _result.ToJson();
            var url = DashboardLink.Build(baseUrl, json);
            try
            {
                if (url.Length > DashboardLink.MaxUrlLength)
                {
                    // 結果太大放不進網址：存成暫存檔，開空白圖台讓使用者按「載入 JSON」
                    var file = Path.Combine(Path.GetTempPath(), "LEBR結果_" + _result.CalculatedAt.ToString("yyyyMMdd_HHmm") + ".json");
                    File.WriteAllText(file, json, new UTF8Encoding(false));
                    Process.Start(new ProcessStartInfo(baseUrl) { UseShellExecute = true });
                    Status.Text = "結果太大，無法放進網址。已存成 " + file + "，請在圖台按「載入 JSON」選這個檔。";
                    return;
                }
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                try { Clipboard.SetText(url); } catch (System.Runtime.InteropServices.ExternalException) { }
                Status.Text = "已在瀏覽器開啟圖台（" + baseUrl + "），網址已複製到剪貼簿。";
            }
            catch (Exception ex)
            {
                Status.Text = "無法開啟瀏覽器：" + ex.Message;
            }
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    /// <summary>結果視窗的顯示文字。數字格式在這裡統一。</summary>
    public class ResultView
    {
        public ResultView(LebrResult r)
        {
            Result = r;
            GradeText = r.Grade != null ? r.Grade + " 級" : "—";
            CfrText = r.CFR.HasValue ? "減碳率 CFR " + r.CFR.Value.ToString("P2") : "減碳率無法計算";
            EciText = r.ECI.ToString("N2");
            EcisText = "基準案 ECIs " + r.ECIs.ToString("N2");
            EecText = r.EEC.ToString("N0");
            EeccText = "基準案 EECc " + r.EECc.ToString("N0");
            DeltaText = r.DeltaCF.ToString("N0");
            TecText = "全生命週期 TEC " + r.TEC.ToString("N0") + "（含地下層，只揭露）";
            SourceText = "數量來源：" + (r.Source ?? "數量檔") + "　｜　資料庫 " + r.DatabaseVersion + " 版（" + r.DatabaseReviewStatus + "）";

            var banner = new List<string>();
            if (!r.Complete) banner.Add("必要資料不足（見「提醒與未對應」），等級僅供參考。");
            if (!string.Equals(r.DatabaseReviewStatus, "reviewed", StringComparison.OrdinalIgnoreCase))
                banner.Add("資料庫尚未完成人工核對，結果僅供設計階段參考，不等於標章評定。");
            BannerText = string.Join("　", banner);
            BannerVisibility = banner.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // 表 6-1：ECI 低於該刻度即達該等級；7 級為最後一個刻度以上
            GradeChips = new List<GradeChip>();
            double? previous = null;
            foreach (var g in r.GradeScale)
            {
                GradeChips.Add(new GradeChip
                {
                    Label = g.Grade + " 級",
                    EciText = g.Eci.HasValue ? "< " + g.Eci.Value.ToString("N0") : previous.HasValue ? "≥ " + previous.Value.ToString("N0") : "—",
                    IsCurrent = g.Grade == r.Grade,
                });
                if (g.Eci.HasValue) previous = g.Eci;
            }

            var notices = r.Issues.Count(i => i.Level != IssueLevel.Info) + r.Unmapped.Count;
            NoticeHeader = notices > 0 ? "提醒與未對應（" + notices + "）" : "提醒與未對應";
            UnmappedVisibility = r.Unmapped.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoUnmappedVisibility = r.Unmapped.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

            var s = r.Structure;
            StructureRows = s.Computed
                ? new List<StructureRow>
                {
                    new StructureRow("單位面積碳排 C（kgCO2e/m²，式 4）", s.DesignC.ToString("N2"), s.BaselineC.ToString("N2"), "計算結果"),
                    new StructureRow("構造係數 W", s.DesignW.ToString("0.00"), s.BaselineW.ToString("0.00"), "設計案：表 4-1；基準案：表 4-6"),
                    new StructureRow("跨距變化係數 Sp", s.DesignSp.ToString("0.00"), s.BaselineSp.ToString("0.00"), "設計案：模型；基準案：表 4-6"),
                    new StructureRow("形狀係數 F", s.DesignF.ToString("0.00"), s.BaselineF.ToString("0.00"), "設計案：模型；基準案：表 4-6"),
                    new StructureRow("低碳混凝土 LCCR", s.Lccr.ToString("0.000"), "1.000", "式 5，CSER"),
                    new StructureRow("舊建築再利用 RN", s.Rn.ToString("0.000"), "1.000", "式 6"),
                    new StructureRow("地上主結構碳排 CFs（kgCO2e）", r.Design.CFs.ToString("N0"), r.Baseline.CFs.ToString("N0"), "式 1、3"),
                    new StructureRow("地下主結構碳排 CFs'（kgCO2e）", r.UndergroundStructure.ToString("N0"), r.UndergroundStructure.ToString("N0"), "式 2，只揭露"),
                }
                : new List<StructureRow> { new StructureRow("主結構未計算", "—", "—", "見「提醒與未對應」缺少的參數") };
        }

        public LebrResult Result { get; private set; }
        public string GradeText { get; private set; }
        public string CfrText { get; private set; }
        public string EciText { get; private set; }
        public string EcisText { get; private set; }
        public string EecText { get; private set; }
        public string EeccText { get; private set; }
        public string DeltaText { get; private set; }
        public string TecText { get; private set; }
        public string SourceText { get; private set; }
        public string BannerText { get; private set; }
        public Visibility BannerVisibility { get; private set; }
        public List<GradeChip> GradeChips { get; private set; }
        public string NoticeHeader { get; private set; }
        public Visibility UnmappedVisibility { get; private set; }
        public Visibility NoUnmappedVisibility { get; private set; }
        public List<StructureRow> StructureRows { get; private set; }
    }

    public class GradeChip
    {
        public string Label { get; set; }
        public string EciText { get; set; }
        public bool IsCurrent { get; set; }
    }

    public class StructureRow
    {
        public StructureRow(string name, string design, string baseline, string note)
        {
            Name = name;
            Design = design;
            Baseline = baseline;
            Note = note;
        }

        public string Name { get; private set; }
        public string Design { get; private set; }
        public string Baseline { get; private set; }
        public string Note { get; private set; }
    }
}
