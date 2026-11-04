using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using LebrCarbon.Core.Lebr;
using LebrCarbon.Dashboard;
using LebrCarbon.Data;
using LebrCarbon.Reports;
using LebrCarbon.Revit.Extraction;

namespace LebrCarbon.Revit.UI;

/// <summary>
/// LEBR 評估主視窗（計畫書 6.2 節）：建築參數、非主結構構件、結果三個分頁。
/// 以強制回應方式在外部命令內開啟，因此按鈕可直接唯讀呼叫 Revit API。
/// </summary>
public sealed class LebrWindow : Window
{
    private readonly Autodesk.Revit.DB.Document _doc;
    private readonly BlccLibrary _lib;
    private readonly string _projectPath;
    private readonly LebrProjectFile _file;
    private BuildingGeometry _geo;

    private readonly ObservableCollection<LevelRow> _levels = new();
    private readonly ObservableCollection<NsRow> _rows = new();
    private readonly List<BlccChoice> _choices;

    private readonly ComboBox _groundCombo = new() { MinWidth = 160 };
    private readonly ComboBox _shapeLevelCombo = new() { MinWidth = 160 };
    private readonly StackPanel _inputsPanel = new();
    private readonly TextBlock _spanText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private readonly TextBlock _shapeText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBox _report = new()
    {
        IsReadOnly = true,
        FontFamily = new FontFamily("Consolas, Microsoft JhengHei UI"),
        FontSize = 13,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly TabControl _tabs = new();
    private LebrResult? _last;

    public LebrWindow(Autodesk.Revit.DB.Document doc)
    {
        _doc = doc;
        _lib = BlccLibrary.Load();
        _choices = new List<BlccChoice> { new("", "（自訂）") };
        _choices.AddRange(_lib.Items.Select(i => new BlccChoice(i.Id, $"{i.Id}  {i.Name}")));

        _projectPath = ProjectStore.PathFor(doc.PathName, doc.Title);
        var loaded = ProjectStore.TryLoad(_projectPath);
        _file = loaded ?? new LebrProjectFile { ModelTitle = doc.Title };

        Title = $"LEBR 低碳建築評估 — {doc.Title}";
        Width = 1050;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft JhengHei UI");
        FontSize = 13;

        _geo = BuildingGeometryExtractor.Extract(doc);
        LoadLevels();
        if (loaded is null) ApplyModelValues(); // 第一次開啟：以模型數值為起點
        foreach (var i in _file.DesignItems) AddRow(NsRow.From(i));

        Content = BuildLayout();
        SetStatus(loaded is null ? "已由模型讀入樓層、面積、跨距與平面形狀，請補齊結構計算書參數" : $"已載入 {_projectPath}");
    }

    // ───────────────────────── 版面 ─────────────────────────

    private UIElement BuildLayout()
    {
        _tabs.Items.Add(new TabItem { Header = "建築參數", Content = BuildInputsTab() });
        _tabs.Items.Add(new TabItem { Header = "非主結構構件", Content = BuildNsTab() });
        _tabs.Items.Add(new TabItem { Header = "結果", Content = _report });

        var bar = new DockPanel { Margin = new Thickness(8), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        buttons.Children.Add(Btn("計算", (_, _) => Calculate()));
        buttons.Children.Add(Btn("儲存", (_, _) => Save(true)));
        buttons.Children.Add(Btn("匯出 Excel 計算書", (_, _) => ExportExcel()));
        buttons.Children.Add(Btn("匯出 PDF 摘要", (_, _) => ExportPdf()));
        buttons.Children.Add(Btn("匯出 CSV", (_, _) => ExportCsv()));
        buttons.Children.Add(Btn("上傳到控制台", (_, _) => UploadToDashboard()));
        buttons.Children.Add(Btn("關閉", (_, _) => Close()));
        bar.Children.Add(buttons);
        bar.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(_tabs);
        return root;
    }

    private UIElement BuildInputsTab()
    {
        var left = new StackPanel { Margin = new Thickness(8) };
        left.Children.Add(Header("樓層與樓地板面積（由模型樓板計算）"));

        var groundRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        groundRow.Children.Add(new TextBlock { Text = "一樓（地面層）：", VerticalAlignment = VerticalAlignment.Center });
        _groundCombo.ItemsSource = _levels;
        _groundCombo.DisplayMemberPath = nameof(LevelRow.Name);
        _groundCombo.SelectedItem = _levels.FirstOrDefault(l => l.Name == _file.GroundLevelName) ?? DefaultGround();
        groundRow.Children.Add(_groundCombo);
        left.Children.Add(groundRow);

        var grid = new DataGrid
        {
            ItemsSource = _levels,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Height = 260,
        };
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "計入", Binding = new Binding(nameof(LevelRow.Include)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } });
        grid.Columns.Add(new DataGridTextColumn { Header = "樓層", Binding = new Binding(nameof(LevelRow.Name)), IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "高程 m", Binding = new Binding(nameof(LevelRow.ElevationM)) { StringFormat = "0.00" }, IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "樓板面積 m²", Binding = new Binding(nameof(LevelRow.AreaM2)) { StringFormat = "N2" }, IsReadOnly = true });
        left.Children.Add(grid);
        left.Children.Add(Note("屋頂層、屋突請取消勾選。樓板面積依樓層上的結構樓板計算，與執照面積不同時請直接修改右側 AFu、AFb。"));
        left.Children.Add(Btn("依樓層表計算 S、Sb、AFu、AFb、BH", (_, _) => { ApplyLevels(); RefreshInputs(); }));

        left.Children.Add(Header("跨距變化係數 Sp（由軸線計算）"));
        left.Children.Add(_spanText);
        left.Children.Add(Btn("依軸線重新計算 Sp", (_, _) => { _file.Inputs.Sp = Math.Round(SpanCalculator.Sp(_geo.XSpans, _geo.YSpans), 3); RefreshInputs(); }));

        left.Children.Add(Header("形狀係數 F（標準層平面）"));
        var shapeRow = new StackPanel { Orientation = Orientation.Horizontal };
        shapeRow.Children.Add(new TextBlock { Text = "標準層：", VerticalAlignment = VerticalAlignment.Center });
        _shapeLevelCombo.ItemsSource = _levels;
        _shapeLevelCombo.DisplayMemberPath = nameof(LevelRow.Name);
        _shapeLevelCombo.SelectedItem = _levels.FirstOrDefault(l => l.Name == _file.Shape.LevelName) ?? DefaultTypicalLevel();
        shapeRow.Children.Add(_shapeLevelCombo);
        shapeRow.Children.Add(Btn("計算平面形狀", (_, _) => { ComputeShape(); RefreshInputs(); }));
        left.Children.Add(shapeRow);
        left.Children.Add(_shapeText);
        UpdateSpanText();
        UpdateShapeText();

        _inputsPanel.Margin = new Thickness(8);
        RefreshInputs();

        var g = new System.Windows.Controls.Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var r = new ScrollViewer { Content = _inputsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        System.Windows.Controls.Grid.SetColumn(r, 1);
        g.Children.Add(l);
        g.Children.Add(r);
        return g;
    }

    /// <summary>右側參數表單；資料來源為 _file.Inputs，重新產生即可反映程式修改的值。</summary>
    private void RefreshInputs()
    {
        var p = _file.Inputs;
        var s = _file.Shape;
        var panel = _inputsPanel;
        panel.Children.Clear();
        panel.DataContext = _file;

        panel.Children.Add(Header("基本資料（設計案與基準案同）"));
        Field(panel, "地上層總樓地板面積 AFu (m²)", "Inputs.AFu");
        Field(panel, "地下層總樓地板面積 AFb (m²)", "Inputs.AFb");
        Field(panel, "地上樓層數 S（不計屋突）", "Inputs.S");
        Field(panel, "地下樓層數 Sb", "Inputs.Sb");
        Field(panel, "一樓底層樓高 BH (m)", "Inputs.BH");

        panel.Children.Add(Header("結構計算書參數"));
        Field(panel, "用途係數 I", "Inputs.I", "建築物耐震設計規範 2.8 節");
        Field(panel, "工址設計水平加速度反應譜係數 SaD", "Inputs.SaD", "x、y 向取大者");
        Field(panel, "結構系統地震力折減係數 Fu", "Inputs.Fu", "x、y 向取大者");
        Field(panel, "靜力分析折減係數 Rs", "Inputs.Rs", "不需動力分析 0.95，反之 1.0");
        Field(panel, "活載重 L (kgf/m²)", "Inputs.L", "技術規則構造篇第 17 條，混合用途依面積加權");
        Field(panel, "額外靜載重 D0 (kgf/m²)", "Inputs.D0", "表 4：少隔間 275；輕隔間 300；磚牆 350；RC 牆 375");

        panel.Children.Add(Header("設計案"));
        Choice(panel, "構造類別", "Inputs.Structure",
            Enum.GetValues<StructureType>().Select(t => new KeyValuePair<object, string>(t, $"{t.DisplayName()}（W={LebrTables.StructureFactorW(t)}）")));
        Field(panel, "構造係數 W（空白＝依構造類別）", "Inputs.WOverride", "混合構造請依面積加權後輸入");
        Field(panel, "跨距變化係數 Sp", "Inputs.Sp");
        Field(panel, "周長面積比係數 PAr", "Shape.PAr", "表 5");
        Field(panel, "平面長寬比 b", "Shape.Aspect", "表 6");
        Field(panel, "出挑係數 Rc", "Shape.Rc", "表 7；出挑深度 > 4m 應改用對比模型法");
        panel.Children.Add(Note($"目前 f1={s.F1:0.00}、f2={s.F2:0.00}、f3={s.F3:0.00}，F={s.F:0.000}（按「計算」後更新）"));
        Field(panel, "水泥強度效益倍數 CSER（無資料填 0）", "Inputs.CSER", "LCCR = 1 − CSER × 0.05");
        Field(panel, "舊建築再利用面積 EBF (m²)", "Inputs.EBF");

        panel.Children.Add(Header("基準案（空白＝依表 8 自動取值）"));
        Field(panel, $"基準案 W（自動 {p.BaselineW}）", "Inputs.BaselineWOverride");
        Field(panel, $"基準案 Sp（自動 {LebrTables.BaselineSp(p.S, p.AverageFloorArea)}）", "Inputs.BaselineSpOverride");
        Field(panel, $"基準案 F（自動 {LebrTables.BaselineF(p.S)}）", "Inputs.BaselineFOverride");

        panel.Children.Add(Header("非主結構與拆除廢棄"));
        Choice(panel, "建築耗損等級（表 1）", "Inputs.Wear", new[]
        {
            new KeyValuePair<object, string>(WearLevel.High, "高耗損：商場、旅館、餐廳、醫療、交通…"),
            new KeyValuePair<object, string>(WearLevel.Mid, "中耗損：出租辦公、工廠、公共廳舍、學校…"),
            new KeyValuePair<object, string>(WearLevel.Low, "低耗損：自用辦公、倉庫、住宅、住宿…"),
        });
        Choice(panel, "廢棄物量密度用途（表 9）", "Inputs.WasteUsage", new[]
        {
            new KeyValuePair<object, string>(WasteUsage.Residential, "住宿類與旅館、醫院類"),
            new KeyValuePair<object, string>(WasteUsage.Factory, "廠房類"),
            new KeyValuePair<object, string>(WasteUsage.Other, "其他非住宿類"),
        });

        panel.Children.Add(Header("減碳優惠"));
        Field(panel, "建築延壽優惠係數 LL（表 11，≤ 0.08）", "Inputs.LL");
        Field(panel, "舊建材再利用減碳量 (kgCO2e)", "Inputs.ReuseCredit");
        Field(panel, "低碳循環建材減碳量 (kgCO2e)", "Inputs.RecycledMaterialCredit", "與 LCCR 擇一，採計時 CSER 請填 0");
        Field(panel, "低碳工法減碳量 (kgCO2e)", "Inputs.LowCarbonMethodCredit");
    }

    private UIElement BuildNsTab()
    {
        var grid = new DataGrid
        {
            ItemsSource = _rows,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Extended,
        };
        grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "B-LCC 項目",
            ItemsSource = _choices,
            DisplayMemberPath = nameof(BlccChoice.Display),
            SelectedValuePath = nameof(BlccChoice.Id),
            SelectedValueBinding = new Binding(nameof(NsRow.BlccId)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = new DataGridLength(300),
        });
        grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "類別",
            ItemsSource = Enum.GetValues<NsCategory>().Select(c => new KeyValuePair<NsCategory, string>(c, c.DisplayName())).ToList(),
            DisplayMemberPath = "Value",
            SelectedValuePath = "Key",
            SelectedValueBinding = new Binding(nameof(NsRow.Category)),
            Width = new DataGridLength(150),
        });
        grid.Columns.Add(new DataGridTextColumn { Header = "名稱", Binding = new Binding(nameof(NsRow.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "面積 m²", Binding = new Binding(nameof(NsRow.Area)) { StringFormat = "0.##" } });
        grid.Columns.Add(new DataGridTextColumn { Header = "新建 /m²", Binding = new Binding(nameof(NsRow.NewEf)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "更新 /m²", Binding = new Binding(nameof(NsRow.RenewEf)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "新建碳排", Binding = new Binding(nameof(NsRow.NewEmission)) { StringFormat = "N0" }, IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "來源", Binding = new Binding(nameof(NsRow.Source)), IsReadOnly = true });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        buttons.Children.Add(Btn("從模型帶入", (_, _) => ImportFromModel()));
        buttons.Children.Add(Btn("新增一列", (_, _) => AddRow(new NsRow { Category = NsCategory.ExteriorFinish, Name = "新構件", Source = "人工輸入" })));
        buttons.Children.Add(Btn("刪除選取列", (_, _) =>
        {
            foreach (var r in grid.SelectedItems.Cast<NsRow>().ToList()) _rows.Remove(r);
        }));

        var panel = new DockPanel { Margin = new Thickness(8) };
        var top = new StackPanel();
        top.Children.Add(Note("「從模型帶入」會讀取牆、窗、門、樓板、帷幕嵌板類型上的 LEBR_BlccId 參數（先按功能區「綁定參數」）。" +
                              "一個類型可填多個 ID，用分號分隔，例如 RC 外牆貼磁磚填「CW-0;OW-1」。地下層構件不計。" +
                              "基準案依各構件的比較基準自動產生（手冊式 11 參數說明）。"));
        top.Children.Add(buttons);
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(grid);
        return panel;
    }

    // ───────────────────────── 模型數值 ─────────────────────────

    private void LoadLevels()
    {
        _levels.Clear();
        foreach (var lv in _geo.Levels)
            _levels.Add(new LevelRow { Id = lv.Id, Name = lv.Name, ElevationM = lv.ElevationM, AreaM2 = lv.FloorAreaM2 });

        var ground = _levels.FirstOrDefault(l => l.Name == _file.GroundLevelName) ?? DefaultGround();
        var groundElev = ground?.ElevationM ?? 0;
        var above = _levels.Where(l => l.ElevationM >= groundElev - 0.01 && l.AreaM2 > 0).ToList();
        var roof = above.Count > 1 ? above[^1] : null;   // 最高一層有樓板者通常是屋頂板

        foreach (var l in _levels)
        {
            var include = l.AreaM2 > 0 && l != roof && !LooksLikeRoof(l.Name);
            if (_file.ExcludedLevels.Contains(l.Name)) include = false;
            if (_file.IncludedLevels.Contains(l.Name)) include = true;
            l.Include = include;
        }
    }

    /// <summary>屋頂層、屋突：名稱含「屋」，或為 R、RF、R1、R1F、PR 等。</summary>
    private static bool LooksLikeRoof(string name) =>
        name.Contains('屋') ||
        System.Text.RegularExpressions.Regex.IsMatch(name.Trim(), @"^(P?RF?|R\d+F?|RF\d*)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private LevelRow? DefaultGround() => _levels.OrderBy(l => Math.Abs(l.ElevationM)).ThenByDescending(l => l.ElevationM).FirstOrDefault();

    private LevelRow? DefaultTypicalLevel()
    {
        var ground = _groundCombo.SelectedItem as LevelRow ?? DefaultGround();
        var above = _levels.Where(l => l.Include && l.ElevationM >= (ground?.ElevationM ?? 0) - 0.01).ToList();
        if (above.Count == 0) return ground;
        // 標準層：樓板面積重複最多的樓層（四捨五入到 1 m²）
        return above.GroupBy(l => Math.Round(l.AreaM2))
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key)
            .First().First();
    }

    private void ApplyModelValues()
    {
        ApplyLevels();
        _file.Inputs.Sp = Math.Round(SpanCalculator.Sp(_geo.XSpans, _geo.YSpans), 3);
        ComputeShape(DefaultTypicalLevel());
    }

    private void ApplyLevels()
    {
        var p = _file.Inputs;
        var ground = _groundCombo.SelectedItem as LevelRow ?? DefaultGround();
        if (ground is null) return;
        var groundElev = ground.ElevationM;
        var above = _levels.Where(l => l.Include && l.ElevationM >= groundElev - 0.01).OrderBy(l => l.ElevationM).ToList();
        var below = _levels.Where(l => l.Include && l.ElevationM < groundElev - 0.01).ToList();

        p.S = above.Count;
        p.AFu = Math.Round(above.Sum(l => l.AreaM2), 2);
        p.Sb = below.Count;
        p.AFb = Math.Round(below.Sum(l => l.AreaM2), 2);

        // BH：一樓到上一個勾選樓層的高差，跳過夾層、高程參考等未勾選的樓層；
        // 單層建築上面只剩屋頂（未勾選）時，退回取最近的上一層
        var higher = _levels.Where(l => l.ElevationM > groundElev + 0.01).OrderBy(l => l.ElevationM).ToList();
        var next = higher.FirstOrDefault(l => l.Include) ?? higher.FirstOrDefault();
        if (next is not null) p.BH = Math.Round(next.ElevationM - groundElev, 2);

        _file.GroundLevelName = ground.Name;
    }

    private void ComputeShape(LevelRow? level = null)
    {
        level ??= _shapeLevelCombo.SelectedItem as LevelRow ?? DefaultTypicalLevel();
        if (level is null) return;
        var shape = BuildingGeometryExtractor.ExtractShape(_doc, level.Id, _geo);
        if (shape is null)
        {
            SetStatus($"樓層 {level.Name} 無法計算平面形狀，請手動輸入 PAr 與 b");
            return;
        }
        _geo.Shape = shape;
        _file.Shape.LevelName = shape.LevelName;
        _file.Shape.PAr = Math.Round(LebrTables.PerimeterAreaRatio(shape.PerimeterM, shape.AreaM2), 3);
        _file.Shape.Aspect = Math.Round(shape.Aspect, 3);
        UpdateShapeText();
    }

    private void UpdateSpanText()
    {
        string Fmt(List<double> s) => s.Count == 0 ? "（無）" : string.Join("、", s.Select(x => x.ToString("0.##")));
        var ax = SpanCalculator.AxisRatio(_geo.XSpans);
        var ay = SpanCalculator.AxisRatio(_geo.YSpans);
        _spanText.Text = $"X 向跨距 (m)：{Fmt(_geo.XSpans)}　ax={ax:0.###}\nY 向跨距 (m)：{Fmt(_geo.YSpans)}　ay={ay:0.###}\n" +
                         $"模型計算 Sp = {SpanCalculator.Sp(_geo.XSpans, _geo.YSpans):0.###}" +
                         (_geo.Warnings.Count > 0 ? "\n※ " + string.Join("\n※ ", _geo.Warnings) : "");
    }

    private void UpdateShapeText()
    {
        var s = _geo.Shape;
        _shapeText.Text = s is null
            ? "尚未計算平面形狀。"
            : $"{s.LevelName}：面積 {s.AreaM2:N2} m²、外周長 {s.PerimeterM:N2} m（{s.Parts} 塊平面）\n" +
              $"PAr = 0.282 × P / √A = {LebrTables.PerimeterAreaRatio(s.PerimeterM, s.AreaM2):0.###}；最小外接矩形長寬比 b = {s.Aspect:0.###}\n" +
              "出挑係數 Rc 無法由模型判斷，請依表 7 自行輸入。若出挑深度大於總跨距 15%，周長面積比需納入出挑區域。";
    }

    private void ImportFromModel()
    {
        var ground = _groundCombo.SelectedItem as LevelRow ?? DefaultGround();
        var warnings = new List<string>();
        var qs = NonStructuralExtractor.Extract(_doc, ground?.ElevationM ?? 0, warnings);

        foreach (var r in _rows.Where(r => r.Source.StartsWith("模型")).ToList()) _rows.Remove(r);

        var unknown = 0;
        foreach (var q in qs)
        {
            var row = new NsRow { Source = $"模型：{q.CategoryName}「{q.TypeName}」× {q.Count}" };
            AddRow(row);
            if (_lib.ById.ContainsKey(q.BlccId))
            {
                row.BlccId = q.BlccId;
            }
            else
            {
                row.Name = $"未知 B-LCC ID：{q.BlccId}";
                unknown++;
            }
            row.Area = Math.Round(q.AreaM2, 2);
        }

        SetStatus(qs.Count == 0
            ? "模型中沒有填寫 LEBR_BlccId 的構件類型。請先綁定參數，並在牆、窗、樓板類型上填入 B-LCC ID。"
            : $"已帶入 {qs.Count} 筆模型構件" + (unknown > 0 ? $"，其中 {unknown} 筆 ID 不在資料庫中" : "") +
              (warnings.Count > 0 ? $"；{warnings.Count} 個元件無法取得面積" : ""));
    }

    private void AddRow(NsRow row)
    {
        row.Lookup = id => _lib.ById.TryGetValue(id ?? "", out var b) ? b : null;
        row.Wear = () => _file.Inputs.Wear;
        _rows.Add(row);
    }

    // ───────────────────────── 計算與儲存 ─────────────────────────

    private void Calculate()
    {
        try
        {
            CommitEdits();
            var p = _file.Inputs;
            p.F = Math.Round(_file.Shape.F, 4);

            foreach (var r in _rows.Where(r => !r.IsCustom)) r.ApplyBlcc(); // 依目前耗損等級更新
            var design = _rows.Select(r => r.ToItem()).Where(i => i.Area > 0).ToList();
            var baseline = BaselineBuilder.Build(design, _lib.ById, p.Wear);

            _last = LebrCalculator.Evaluate(p, design, baseline);
            var note = $"F = f1 {_file.Shape.F1:0.00} × f2 {_file.Shape.F2:0.00} × f3 {_file.Shape.F3:0.00}（PAr {_file.Shape.PAr:0.###}、b {_file.Shape.Aspect:0.###}、Rc {_file.Shape.Rc:0.###}）";
            if (design.Count == 0) note += "\n  ※ 尚未輸入非主結構構件，結果只含主結構、施工與拆除廢棄";
            _report.Text = LebrReportWriter.ToText(_last, note);
            _tabs.SelectedIndex = 2;
            RefreshInputs();
            Save(false);
            SetStatus($"計算完成：{_last.Grade}，減碳率 {_last.CFR:P2}，已自動儲存");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "無法計算", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Save(bool announce)
    {
        CommitEdits();
        _file.ModelTitle = _doc.Title;
        _file.DesignItems = _rows.Select(r => r.ToItem()).ToList();
        _file.ExcludedLevels = _levels.Where(l => !l.Include && l.AreaM2 > 0).Select(l => l.Name).ToList();
        _file.IncludedLevels = _levels.Where(l => l.Include && l.AreaM2 <= 0).Select(l => l.Name).ToList();
        if (_groundCombo.SelectedItem is LevelRow g) _file.GroundLevelName = g.Name;
        try
        {
            ProjectStore.Save(_projectPath, _file);
            if (announce) SetStatus($"已儲存：{_projectPath}");
        }
        catch (Exception ex)
        {
            SetStatus($"儲存失敗：{ex.Message}");
        }
    }

    private void ExportCsv()
    {
        if (_last is null) Calculate();
        if (_last is null) return;
        var path = Path.ChangeExtension(_projectPath, null) + $".{DateTime.Now:yyyyMMdd-HHmm}.csv";
        LebrReportWriter.WriteCsv(_last, path);
        SetStatus($"已匯出：{path}");
    }

    /// <summary>
    /// 匯出 Excel 計算書：先以目前輸入重新計算，再輸出含公式的 .xlsx（版面參照手冊附錄三）。
    /// </summary>
    private void ExportExcel()
    {
        Calculate();
        if (_last is null) return;

        var path = ReportExporter.ExcelPath(_projectPath, DateTime.Now);   // 與功能區「匯出報表」相同檔名規則
        try
        {
            LebrExcelWriter.Write(path, _last,
                new ShapeValues(_file.Shape.PAr, _file.Shape.Aspect, _file.Shape.Rc), _doc.Title);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"無法寫入檔案，若同名檔案正在 Excel 中開啟請先關閉。\n\n{ex.Message}",
                "匯出失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetStatus($"已匯出計算書：{path}");
        if (MessageBox.Show(this, $"已匯出：\n{path}\n\n要現在開啟嗎？", "匯出 Excel 計算書",
                MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatus($"已匯出，但無法自動開啟：{ex.Message}");
            }
        }
    }

    /// <summary>匯出一頁式 PDF 摘要（F10）：先以目前輸入重新計算，再經 Windows「Microsoft Print to PDF」輸出。</summary>
    private void ExportPdf()
    {
        Calculate();
        if (_last is null) return;

        string path;
        Cursor = Cursors.Wait;
        try
        {
            path = ReportExporter.WritePdf(_last, _file, _doc.Title, _projectPath, DateTime.Now);
        }
        catch (Reports.Pdf.PdfPrinterUnavailableException ex)
        {
            MessageBox.Show(this, ex.Message, "匯出 PDF 摘要", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"無法寫入 PDF，若同名檔案正在 PDF 檢視器中開啟請先關閉。\n\n{ex.Message}",
                "匯出 PDF 摘要", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            Cursor = null;
        }

        SetStatus($"已匯出 PDF 摘要：{path}");
        if (MessageBox.Show(this, $"已匯出：\n{path}\n\n要現在開啟嗎？", "匯出 PDF 摘要",
                MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatus($"已匯出，但無法自動開啟：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 上傳到網頁控制台：先以目前輸入重新計算，問快照名稱，再送出（控制台計畫書 W1）。
    /// 後端沒開時只提示，不影響其他功能。
    /// </summary>
    private void UploadToDashboard()
    {
        Calculate();
        if (_last is null) return;

        var settings = DashboardSettings.Load();

        // 先確認後端有開（最多 2 秒），沒開就不必再問快照名稱
        bool running;
        using (var probe = new DashboardClient(settings.BaseUrl))
            running = Task.Run(() => probe.IsRunningAsync()).GetAwaiter().GetResult();
        if (!running)
        {
            var msg = $"{DashboardClient.NotRunningMessage}（{settings.BaseUrl}）";
            SetStatus(msg);
            MessageBox.Show(this, msg, "上傳到控制台", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new UploadDialog($"{_doc.Title} {DateTime.Now:MM/dd HH:mm}", settings.BaseUrl) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        byte[]? excel = null;
        if (dlg.AttachExcel)
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"lebr-{Guid.NewGuid():N}.xlsx");
            try
            {
                LebrExcelWriter.Write(tmp, _last, new ShapeValues(_file.Shape.PAr, _file.Shape.Aspect, _file.Shape.Rc), _doc.Title);
                excel = File.ReadAllBytes(tmp);
            }
            catch (IOException ex)
            {
                SetStatus($"Excel 計算書產生失敗，改為不附檔上傳：{ex.Message}");
            }
            finally
            {
                try { File.Delete(tmp); } catch (IOException) { }
            }
        }

        // 案子 key 用模型檔名，與 {模型名}.lebr.json 一致；未存檔時用標題
        var key = string.IsNullOrWhiteSpace(_doc.PathName) ? _doc.Title : Path.GetFileNameWithoutExtension(_doc.PathName);
        // 分析（佔比、改善潛力、距下一級）由同一個計算核心逐項試算；_file 已在 Calculate() 自動儲存時同步明細
        var analysis = AnalysisBuilder.Build(_file, _lib, _last);
        var snapshot = SnapshotBuilder.Build(_last, key, _doc.Title, dlg.SnapshotName, _file.Shape, excel: excel, analysis: analysis);

        SetStatus("上傳中…");
        Cursor = Cursors.Wait;
        UploadResult result;
        try
        {
            using var client = new DashboardClient(settings.BaseUrl);
            // 在背景執行緒等待，避免在 UI 執行緒上同步等候 async 造成死結
            result = Task.Run(() => client.UploadAsync(snapshot)).GetAwaiter().GetResult();
        }
        finally
        {
            Cursor = null;
        }

        if (result.Ok)
        {
            SetStatus($"已上傳到控制台：{dlg.SnapshotName}（快照 {result.SnapshotId}）");
            MessageBox.Show(this, $"已上傳「{dlg.SnapshotName}」。\n\n在瀏覽器開 http://localhost:5173 查看。",
                "上傳到控制台", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            SetStatus(result.Message);
            MessageBox.Show(this, result.Message, "上傳到控制台", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>讓正在編輯中的文字方塊寫回資料。</summary>
    private void CommitEdits()
    {
        if (Keyboard.FocusedElement is TextBox tb)
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    // ───────────────────────── 小工具 ─────────────────────────

    private void SetStatus(string text) => _status.Text = text;

    private static Button Btn(string text, RoutedEventHandler onClick)
    {
        var b = new Button { Content = text, Margin = new Thickness(4, 2, 4, 2), Padding = new Thickness(10, 3, 10, 3) };
        b.Click += onClick;
        return b;
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.Bold,
        FontSize = 14,
        Margin = new Thickness(0, 12, 0, 4),
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.DimGray,
        Margin = new Thickness(0, 2, 0, 4),
    };

    private static void Field(Panel panel, string label, string path, string? tip = null)
    {
        var row = new System.Windows.Controls.Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
        var box = new TextBox { ToolTip = tip };
        box.SetBinding(TextBox.TextProperty, new Binding(path)
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
            TargetNullValue = "",
            ValidatesOnExceptions = true,
        });
        System.Windows.Controls.Grid.SetColumn(box, 1);
        row.Children.Add(lbl);
        row.Children.Add(box);
        panel.Children.Add(row);
    }

    private static void Choice(Panel panel, string label, string path, IEnumerable<KeyValuePair<object, string>> items)
    {
        var row = new System.Windows.Controls.Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var combo = new ComboBox { ItemsSource = items.ToList(), DisplayMemberPath = "Value", SelectedValuePath = "Key" };
        combo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, new Binding(path) { Mode = BindingMode.TwoWay });
        System.Windows.Controls.Grid.SetColumn(combo, 1);
        row.Children.Add(lbl);
        row.Children.Add(combo);
        panel.Children.Add(row);
    }
}
