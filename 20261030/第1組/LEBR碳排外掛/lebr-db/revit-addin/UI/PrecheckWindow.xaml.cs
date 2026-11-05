using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using LebrCarbon;

namespace LebrCarbon.UI
{
    /// <summary>
    /// 計算前檢查：選數量來源、先試算一次，列出未對應元件與缺少的參數，使用者確認後才開結果視窗。
    /// 不使用 Revit API，可在 Revit 外預覽。
    /// </summary>
    public partial class PrecheckWindow : Window
    {
        private readonly Func<QuantityInput, LebrResult> _calculate;
        private readonly string _samplePath;
        private readonly Func<QuantityInput> _readModel;

        public LebrResult Result { get; private set; }

        /// <param name="readModel">從目前模型擷取數量；在 Revit 外預覽時為 null，模型選項會停用。</param>
        public PrecheckWindow(Func<QuantityInput, LebrResult> calculate, string samplePath,
                              Func<QuantityInput> readModel = null, string modelName = null)
        {
            _calculate = calculate;
            _samplePath = samplePath;
            _readModel = readModel;
            InitializeComponent();
            if (readModel != null)
            {
                ModelSource.IsEnabled = true;
                ModelTitle.Text = "從目前的 Revit 模型擷取：" + modelName;
                ModelHint.Text = "依類型上的 LEBR_Code 加總面積，並由樓層、格線與樓板外框計算主結構幾何";
                ModelSource.IsChecked = true;
            }
            Loaded += (s, e) => Refresh();
        }

        private void OnSourceChanged(object sender, RoutedEventArgs e)
        {
            if (IsLoaded) Refresh();
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "數量檔 (*.json)|*.json", Title = "選擇數量檔" };
            if (dialog.ShowDialog(this) != true) return;
            FilePath.Text = dialog.FileName;
            FileSource.IsChecked = true;
            Refresh();
        }

        private void Refresh()
        {
            Result = null;
            var fromModel = ModelSource.IsChecked == true && _readModel != null;
            var path = SampleSource.IsChecked == true ? _samplePath : FilePath.Text;
            if (!fromModel && string.IsNullOrWhiteSpace(path))
            {
                Show("請先選擇數量檔。", new List<LebrIssue>());
                return;
            }
            try
            {
                var input = fromModel ? _readModel() : QuantityInput.Load(path);
                Result = _calculate(input);
                var errors = Result.Issues.Count(i => i.Level == IssueLevel.Error);
                var summary = string.Format("{0}：{1} 項構件、未對應 {2} 個、缺少 {3} 項。",
                    string.IsNullOrWhiteSpace(Result.Source) ? "數量檔" : Result.Source,
                    Result.Items.Count, Result.Unmapped.Count, errors);
                summary += errors > 0 ? "可以先取消去補齊，或直接計算（等級僅供參考）。" : "可以開始計算。";
                Show(summary, Result.Issues);
            }
            catch (Exception ex)
            {
                Show((fromModel ? "從模型擷取數量時發生錯誤：" : "無法讀取數量檔：") + ex.Message, new List<LebrIssue>());
            }
        }

        private void Show(string summary, IList<LebrIssue> issues)
        {
            Summary.Text = summary;
            IssueList.ItemsSource = issues.OrderByDescending(i => i.Level).ToList();
            StartButton.IsEnabled = Result != null && Result.AFu > 0;
        }

        private void OnStart(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
