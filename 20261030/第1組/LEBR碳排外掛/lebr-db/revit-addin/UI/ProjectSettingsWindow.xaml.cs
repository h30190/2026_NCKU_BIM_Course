using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LebrCarbon;

namespace LebrCarbon.UI
{
    /// <summary>
    /// 專案設定：主結構（A）、非主結構（B）、優惠係數（C）。儲存時寫回專案資訊的 LEBR_* 參數。
    /// 規則集中在 ProjectSettingsRules，這裡只負責畫面與轉換。
    /// </summary>
    public partial class ProjectSettingsWindow : Window
    {
        private readonly LifeExtensionTable _table;
        private readonly Func<ProjectSettingsModel> _reload;
        private bool _loading;

        public ProjectSettingsModel Result { get; private set; }

        private readonly Func<EstimateOutcome> _openEstimate;

        /// <param name="openEstimate">開啟估算視窗；回傳估算結果（取消時回傳 null）。</param>
        public ProjectSettingsWindow(ProjectSettingsModel initial, LifeExtensionTable table, Func<ProjectSettingsModel> reload, string projectName, Func<EstimateOutcome> openEstimate)
        {
            _table = table;
            _reload = reload;
            _openEstimate = openEstimate;
            InitializeComponent();
            ProjectName.Text = "專案：" + (string.IsNullOrWhiteSpace(projectName) ? "（未命名）" : projectName) + "　儲存位置：專案資訊的 LEBR_* 參數";
            ReloadButton.IsEnabled = reload != null;
            FillOptions();
            Load(initial ?? new ProjectSettingsModel());
        }

        // ------------------------------------------------------------ 選項

        private void FillOptions()
        {
            WearClass.ItemsSource = new[]
            {
                new Option("H", "H 高耗損：商場、旅館、醫院"),
                new Option("M", "M 中耗損：學校、出租辦公"),
                new Option("L", "L 低耗損：住宅、自用辦公"),
            };
            I.ItemsSource = new[]
            {
                new Option(1.5, "第一、二類（I = 1.5）"),
                new Option(1.25, "第三類（I = 1.25）"),
                new Option(1.0, "第四類（I = 1.0）"),
            };
            UseType.ItemsSource = new[]
            {
                new Option("residential", "residential：住宿、旅館、醫院"),
                new Option("factory", "factory：廠房"),
                new Option("other", "other：其他"),
            };
            Structure.ItemsSource = new[]
            {
                new Option("RC", "RC"), new Option("SRC", "SRC"), new Option("S", "S"),
                new Option("light_steel", "light_steel"), new Option("wood", "wood"),
            };
            Rs.ItemsSource = new[]
            {
                new Option("0.95", "0.95 不需動力分析（預設）"),
                new Option("1.00", "1.00 其他情況"),
            };
            DurableBox.ItemsSource = LlOptions("耐久設計");
            QualityBox.ItemsSource = LlOptions("高品質施工");
        }

        private List<Option> LlOptions(string group)
        {
            var list = new List<Option> { new Option(null, "無") };
            if (_table == null) return list;
            foreach (var item in _table.Items.Where(i => i.Group == group).OrderBy(i => i.Id))
            {
                var text = string.Format(CultureInfo.InvariantCulture, "項目{0}：{1}（{2:0.00}）", item.Id, item.Label, item.LL);
                if (item.RcOnly) text += "　← 僅 RC";
                list.Add(new Option(item.Id, text));
            }
            return list;
        }

        // ------------------------------------------------------------ 讀寫模型

        private void Load(ProjectSettingsModel m)
        {
            _loading = true;
            SelectOption(WearClass, m.WearClass);
            SelectOption(UseType, m.UseType);
            SelectOption(Structure, m.Structure);
            EstimateCheck.IsChecked = m.Estimate;
            UpdateEstimateButton();
            ShowEstimate(m.EstimateCfs, m.EstimateSummary);

            SelectOption(I, m.I);
            SetText(SaD, m.SaD);
            SetText(Fu, m.Fu);
            SetText(L, m.L);
            SetText(D0, m.D0);
            SelectOption(Rs, m.Rs >= 0.999 ? "1.00" : "0.95");

            CserCheck.IsChecked = m.CserEnabled;
            SetText(CserValue, m.CserInput ?? 0);

            StructOverrideCheck.IsChecked = m.StructureOverrideEnabled;
            SetText(OverrideS, m.OverrideS);
            SetText(OverrideSp, m.OverrideSp);
            SetText(OverrideF, m.OverrideF);

            AreaOverrideCheck.IsChecked = m.AreaOverrideEnabled;
            SetText(OverrideAFu, m.OverrideAFu);

            SelectOption(DurableBox, m.DurableItemId);
            SelectOption(QualityBox, m.QualityItemId);
            ProofCheck.IsChecked = m.ProofAttached;
            _loading = false;
            Refresh();
        }

        private ProjectSettingsModel Read()
        {
            return new ProjectSettingsModel
            {
                WearClass = SelectedValue<string>(WearClass),
                UseType = SelectedValue<string>(UseType),
                Structure = SelectedValue<string>(Structure),
                Estimate = EstimateCheck.IsChecked == true,
                I = SelectedValue<double?>(I),
                SaD = Num(SaD),
                Fu = Num(Fu),
                L = Num(L),
                D0 = Num(D0),
                Rs = double.Parse(SelectedValue<string>(Rs) ?? "0.95", CultureInfo.InvariantCulture),
                CserEnabled = CserCheck.IsChecked == true,
                CserInput = Num(CserValue),
                StructureOverrideEnabled = StructOverrideCheck.IsChecked == true,
                OverrideS = Num(OverrideS),
                OverrideSp = Num(OverrideSp),
                OverrideF = Num(OverrideF),
                AreaOverrideEnabled = AreaOverrideCheck.IsChecked == true,
                OverrideAFu = Num(OverrideAFu),
                DurableItemId = SelectedValue<int?>(DurableBox),
                QualityItemId = SelectedValue<int?>(QualityBox),
                ProofAttached = ProofCheck.IsChecked == true,
            };
        }

        // ------------------------------------------------------------ 事件

        private void OnStructureChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var m = Read();
            m.ApplyStructureRules(_table);
            _loading = true;
            if (!m.CserEnabled) CserCheck.IsChecked = false;
            SelectOption(DurableBox, m.DurableItemId);
            SelectOption(QualityBox, m.QualityItemId);
            _loading = false;
            Refresh();
        }

        private void OnInputChanged(object sender, RoutedEventArgs e)
        {
            UpdateEstimateButton();
            if (!_loading) Refresh();
        }

        /// <summary>只有勾選「使用估算」時才能開啟估算視窗。</summary>
        private void UpdateEstimateButton()
        {
            OpenEstimateButton.IsEnabled = _openEstimate != null && EstimateCheck.IsChecked == true;
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_loading) Refresh();
        }

        private void OnIChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loading) Refresh();
        }

        private void OnOpenEstimate(object sender, RoutedEventArgs e)
        {
            var outcome = _openEstimate?.Invoke();
            if (outcome != null && outcome.Cfs.HasValue)
                ShowEstimate(outcome.Cfs, outcome.Summary);
        }

        private void ShowEstimate(double? cfs, string summary)
        {
            if (cfs.HasValue)
            {
                EstimateCfsText.Text = "估算結果（CFs）：" + cfs.Value.ToString("N0", CultureInfo.InvariantCulture) + " kgCO2e（已儲存至專案）";
                EstimateSummaryText.Text = summary ?? "";
            }
            else
            {
                EstimateCfsText.Text = "估算結果（CFs）：未估算";
                EstimateSummaryText.Text = "";
            }
        }

        private void OnLlSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loading) Refresh();
        }

        private void OnStructOverrideChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (StructOverrideCheck.IsChecked != true)
            {
                SetText(OverrideS, null);
                SetText(OverrideSp, null);
                SetText(OverrideF, null);
            }
            Refresh();
        }

        private void OnAreaOverrideChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (AreaOverrideCheck.IsChecked != true) SetText(OverrideAFu, null);
            Refresh();
        }

        private void OnReload(object sender, RoutedEventArgs e)
        {
            if (_reload == null) return;
            Load(_reload());
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            var m = Read();
            if (ProjectSettingsRules.MissingRequired(m).Count > 0) return;
            m.ClearOverridesIfDisabled();
            m.ApplyStructureRules(_table);
            Result = m;
            DialogResult = true;
        }

        // ------------------------------------------------------------ 畫面狀態

        private void Refresh()
        {
            var m = Read();
            bool estimate = m.Estimate;

            ASection.IsEnabled = !estimate;
            StructOverridePanel.Visibility = StructOverrideCheck.IsChecked == true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            AreaOverridePanel.Visibility = AreaOverrideCheck.IsChecked == true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

            bool cserAllowed = ProjectSettingsRules.CanUseCser(m.Structure);
            CserCheck.IsEnabled = cserAllowed && !estimate;
            CserValue.IsEnabled = CserCheck.IsEnabled && CserCheck.IsChecked == true;
            CserHint.Text = cserAllowed
                ? "勾選後可輸入任意數值；未勾選寫入 0.00"
                : "僅構造為 RC 或 SRC 時可勾選；目前構造不可勾選，寫入 0.00";

            bool rcOnly = ProjectSettingsRules.CanUseRcOnlyLl(m.Structure);
            foreach (var box in new[] { DurableBox, QualityBox })
            {
                foreach (Option o in box.Items)
                {
                    var item = o.Value == null ? null : _table.Items.FirstOrDefault(i => i.Id == (int)o.Value);
                    o.Enabled = item == null || !item.RcOnly || rcOnly;
                }
            }

            var max = _table != null ? _table.MaxTotal : 0.08;
            var raw = ItemLl(m.DurableItemId, m.Structure) + ItemLl(m.QualityItemId, m.Structure);
            var ll = ProjectSettingsRules.ComputeLl(_table, m.Structure, m.DurableItemId, m.QualityItemId, m.ProofAttached);
            LlTotalText.Text = string.Format(CultureInfo.InvariantCulture, "合計 LL = {0:0.00}　　上限 {1:0.00}", ll, max);
            LlNote.Text = string.Format(CultureInfo.InvariantCulture, "寫入 LEBR_LL：{0:0.00}", ll)
                + (raw > max + 1e-9 ? "（合計超過上限，以 " + max.ToString("0.00", CultureInfo.InvariantCulture) + " 計算）" : "")
                + (m.ProofAttached ? "" : "（未附證明文件，寫入 0）");

            var missing = ProjectSettingsRules.MissingRequired(m);
            var names = missing.Select(n => n == "I" ? "用途係數 I" : n);
            MissingText.Text = (missing.Count > 0 ? "⚠ 檢查：缺少 " + string.Join("、", names) + "（未勾選估算時必填）\n" : "")
                + "⚠ 設計案缺少 Sp、F 時，僅設計案主結構未計算；基準案仍會計算";
            if (missing.Contains("I"))
                I.BorderBrush = (System.Windows.Media.Brush)FindResource("ErrorInk");
            else
                I.ClearValue(ComboBox.BorderBrushProperty);
            SaveButton.IsEnabled = missing.Count == 0;
        }

        private double ItemLl(int? id, string structure)
        {
            if (!id.HasValue || _table == null) return 0;
            var item = _table.Items.FirstOrDefault(i => i.Id == id.Value);
            if (item == null) return 0;
            if (item.RcOnly && !ProjectSettingsRules.CanUseRcOnlyLl(structure)) return 0;
            return item.LL;
        }

        // ------------------------------------------------------------ 小工具

        private static void SelectOption(ComboBox box, object value)
        {
            foreach (Option o in box.Items)
            {
                if (Equals(o.Value, value))
                {
                    box.SelectedItem = o;
                    return;
                }
            }
            box.SelectedIndex = -1;
        }

        private static T SelectedValue<T>(ComboBox box)
        {
            var o = box.SelectedItem as Option;
            if (o == null || o.Value == null) return default(T);
            return (T)o.Value;
        }

        private static void SetText(TextBox box, double? value)
        {
            box.Text = value.HasValue ? value.Value.ToString(CultureInfo.CurrentCulture) : "";
        }

        private static double? Num(TextBox box)
        {
            double v;
            return double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out v) ? v : (double?)null;
        }

        /// <summary>下拉選單項目；Value 為 null 代表「無」。Enabled 由規則決定，會通知畫面更新。</summary>
        public class Option : INotifyPropertyChanged
        {
            private bool _enabled = true;

            public Option(object value, string text)
            {
                Value = value;
                Text = text;
            }

            public object Value { get; }
            public string Text { get; }

            public bool Enabled
            {
                get { return _enabled; }
                set
                {
                    if (_enabled == value) return;
                    _enabled = value;
                    if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Enabled"));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            public override string ToString() { return Text; }
        }
    }
}
