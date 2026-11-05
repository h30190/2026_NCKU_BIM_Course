using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LebrCarbon;
using Grid = System.Windows.Controls.Grid;
using Option = LebrCarbon.UI.ProjectSettingsWindow.Option;

namespace LebrCarbon.UI
{
    /// <summary>估算視窗的結果：有估算值時 Cfs 有值，供專案設定表單顯示。</summary>
    public class EstimateOutcome
    {
        public double? Cfs { get; set; }
        public string Summary { get; set; }
    }

    /// <summary>
    /// 結構碳排估算視窗。數量由外部委派讀取（Revit），儲存由外部委派寫入專案；本視窗只負責選擇材料與顯示結果。
    /// </summary>
    public partial class EstimateWindow : Window
    {
        private static readonly Dictionary<string, string> SourceNames = new Dictionary<string, string>
        {
            { "StructuralColumns", "結構柱" },
            { "StructuralFraming", "結構梁" },
            { "Floors", "樓板（全部）" },
            { "Walls", "混凝土類結構牆" },
            { "Rebar", "結構鋼筋（柱、梁、樓板）" },
        };

        private readonly Func<Tuple<List<EstimateQuantityRow>, List<string>>> _readQuantities;
        private readonly PLccTable _table;
        private readonly Action<double, string> _save;
        private readonly Dictionary<string, ComboBox> _selectors = new Dictionary<string, ComboBox>();
        private readonly Dictionary<string, TextBlock> _quantityTexts = new Dictionary<string, TextBlock>();
        private readonly string _modelName;

        private List<EstimateQuantityRow> _rows = new List<EstimateQuantityRow>();
        private List<EstimateItem> _items;
        private List<string> _quantityNotes = new List<string>();
        private EstimateResult _result;

        public EstimateOutcome Outcome { get; private set; }

        /// <param name="readQuantities">讀取模型數量與數量注意事項。</param>
        /// <param name="table">附錄一 P-LCC。</param>
        /// <param name="modelName">模型名稱（寫入估算紀錄）。</param>
        /// <param name="save">儲存委派：傳入 CFs 與估算紀錄 JSON。</param>
        public EstimateWindow(Func<Tuple<List<EstimateQuantityRow>, List<string>>> readQuantities, PLccTable table, string modelName, Action<double, string> save)
        {
            _readQuantities = readQuantities ?? throw new ArgumentNullException("readQuantities");
            _table = table ?? throw new ArgumentNullException("table");
            _save = save ?? throw new ArgumentNullException("save");
            InitializeComponent();
            _modelName = string.IsNullOrWhiteSpace(modelName) ? "（未命名）" : modelName;
            ModelText.Text = "模型：" + _modelName + "　地上以標高 ≥ 0 判定，地下為標高 < 0";
            BuildSourceRows();
            ReadQuantities();
        }

        // ------------------------------------------------------------ 數量與材料選擇

        private void BuildSourceRows()
        {
            SourcePanel.Children.Clear();
            _selectors.Clear();
            _quantityTexts.Clear();

            foreach (var source in EstimateQuantities.Sources)
            {
                var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var name = new TextBlock { Text = SourceNames[source], VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                var quantity = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                var selector = new ComboBox { Margin = new Thickness(0, 2, 0, 2), ItemsSource = MaterialOptions(source), DisplayMemberPath = "Text", SelectedValuePath = "Value" };
                selector.SelectedValue = EstimateQuantities.DefaultMaterials[source];

                Grid.SetColumn(name, 0);
                Grid.SetColumn(quantity, 1);
                Grid.SetColumn(selector, 2);
                grid.Children.Add(name);
                grid.Children.Add(quantity);
                grid.Children.Add(selector);

                SourcePanel.Children.Add(grid);
                _selectors[source] = selector;
                _quantityTexts[source] = quantity;
            }
        }

        /// <summary>混凝土來源列出附錄一的預拌混凝土（m³）；鋼筋列出鋼筋（kg）。</summary>
        private List<Option> MaterialOptions(string source)
        {
            var isRebar = source == "Rebar";
            return _table.Materials
                .Where(m => isRebar
                    ? m.Unit == "kg" && m.Name.Contains("鋼筋")
                    : m.Unit == "m3" && m.Name.Contains("預拌") && m.TotalA1A4.HasValue)
                .Select(m => new Option(m.Id, m.Id + "　" + m.Name + "（" + m.TotalA1A4.Value.ToString("0.00", CultureInfo.InvariantCulture) + " kgCO2e／" + m.Unit + "）"))
                .ToList();
        }

        private void ReadQuantities()
        {
            var read = _readQuantities();
            _rows = read.Item1 ?? new List<EstimateQuantityRow>();
            _quantityNotes = read.Item2 ?? new List<string>();

            foreach (var source in EstimateQuantities.Sources)
            {
                var lines = _rows.Where(r => r.Source == source).ToList();
                var total = lines.Sum(r => r.Quantity);
                var unit = source == "Rebar" ? "kg" : "m³";
                var text = total.ToString("N2", CultureInfo.InvariantCulture) + " " + unit;
                if (lines.Count > 0) text += "（" + lines.Select(r => r.Level).Distinct().Count() + " 層）";
                _quantityTexts[source].Text = text;
            }

            SourceNote.Text = _quantityNotes.Count > 0 ? string.Join("\n", _quantityNotes) : "";
            _result = null;
            SaveButton.IsEnabled = false;
            LevelGrid.ItemsSource = null;
            TotalText.Text = "";
            NotesText.Text = "";
        }

        private Dictionary<string, string> SelectedMaterials()
        {
            var map = new Dictionary<string, string>();
            foreach (var pair in _selectors)
                map[pair.Key] = pair.Value.SelectedValue as string;
            return map;
        }

        // ------------------------------------------------------------ 事件

        private void OnReread(object sender, RoutedEventArgs e)
        {
            ReadQuantities();
        }

        private void OnCalculate(object sender, RoutedEventArgs e)
        {
            try
            {
                _items = EstimateQuantities.ToItems(_rows, SelectedMaterials());
                _result = new StructureEstimator(_table).Calculate(_items);
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(this, ex.Message, "LEBR 估算", MessageBoxButton.OK, MessageBoxImage.Warning);
                _result = null;
                SaveButton.IsEnabled = false;
                return;
            }

            LevelGrid.ItemsSource = _result.Levels.Select(l => new LevelLine
            {
                Level = l.Level,
                Elevation = l.ElevationM.ToString("0.###", CultureInfo.InvariantCulture),
                Zone = l.IsBelowGround ? "地下" : "地上",
                Concrete = l.ConcreteM3.ToString("N2", CultureInfo.InvariantCulture),
                Rebar = l.RebarKg.ToString("N1", CultureInfo.InvariantCulture),
                Cfs = l.CFsn.ToString("N1", CultureInfo.InvariantCulture),
            }).ToList();

            TotalText.Text = "地上 " + _result.Above.ToString("N1", CultureInfo.InvariantCulture)
                + "　地下 " + _result.Below.ToString("N1", CultureInfo.InvariantCulture)
                + "　CFs 合計 " + _result.CFs.ToString("N1", CultureInfo.InvariantCulture) + " kgCO2e";

            var notes = _quantityNotes.Concat(_result.Notes).ToList();
            NotesText.Text = notes.Count > 0 ? "注意：\n" + string.Join("\n", notes) : "";
            SaveButton.IsEnabled = true;
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (_result == null) return;
            // 連同各品類 × 樓層數量與材料係數一起存，結果 JSON 的 structure.bim 由此產生
            var json = EstimateRecord.ToJson(_result, SelectedMaterials(), _modelName, DateTime.Now, _items, _table);
            try
            {
                _save(_result.CFs, json);
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(this, "儲存估算結果時發生錯誤：\n" + ex.Message, "LEBR 估算", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Outcome = new EstimateOutcome
            {
                Cfs = _result.CFs,
                Summary = "地上 " + _result.Above.ToString("N0", CultureInfo.InvariantCulture)
                    + "　地下 " + _result.Below.ToString("N0", CultureInfo.InvariantCulture)
                    + "　合計 " + _result.CFs.ToString("N0", CultureInfo.InvariantCulture) + " kgCO2e",
            };
            DialogResult = true;
        }

        /// <summary>結果表格的一列（顯示用字串）。</summary>
        public class LevelLine
        {
            public string Level { get; set; }
            public string Elevation { get; set; }
            public string Zone { get; set; }
            public string Concrete { get; set; }
            public string Rebar { get; set; }
            public string Cfs { get; set; }
        }
    }
}
