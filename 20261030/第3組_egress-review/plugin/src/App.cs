using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Form = System.Windows.Forms.Form;
using ComboBox = System.Windows.Forms.ComboBox;
using TextBox = System.Windows.Forms.TextBox;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace Group3.EgressExporter
{
    public sealed class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication app)
        {
            var panel = app.CreateRibbonPanel("第三組｜逃生資料");
            var button = (PushButton)panel.AddItem(new PushButtonData("Group3Export", "擷取模型\n與缺漏清單",
                Assembly.GetExecutingAssembly().Location, typeof(ExportCommand).FullName));
            button.ToolTip = "唯讀匯出房間、門、樓梯、走廊與 warnings；不執行法規判定。";
            panel.AddItem(new PushButtonData("Group3SelectStairs", "選擇\n直通梯",
                Assembly.GetExecutingAssembly().Location, typeof(SelectDirectStairsCommand).FullName));
            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
    }
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class InspectSelectionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData command, ref string message, ElementSet elements)
        {
            var uiDoc = command.Application.ActiveUIDocument;
            if (uiDoc == null) return Result.Cancelled;
            var lines = uiDoc.Selection.GetElementIds().Select(id => uiDoc.Document.GetElement(id))
                .Where(e => e != null).Select(e => e.Id.Value + " | " + e.Name + Environment.NewLine + e.UniqueId);
            using (var form = new Form { Text = "所選元件識別碼（可選取文字複製）", Width = 750, Height = 450, StartPosition = FormStartPosition.CenterParent })
            {
                form.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
                    ScrollBars = ScrollBars.Both, Text = string.Join(Environment.NewLine + Environment.NewLine, lines) });
                form.ShowDialog(new WindowHandle(command.Application.MainWindowHandle));
            }
            return Result.Succeeded;
        }
    }
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ExportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData command, ref string message, ElementSet elements)
        {
            var uiDoc = command.Application.ActiveUIDocument;
            if (uiDoc == null || uiDoc.Document.IsFamilyDocument) {
                TaskDialog.Show("逃生資料擷取", "請先開啟 Revit 專案模型。"); return Result.Cancelled;
            }
            try
            {
                var doc = uiDoc.Document;
                using (var form = new ExportForm(doc))
                {
                    if (form.ShowDialog(new WindowHandle(command.Application.MainWindowHandle)) != DialogResult.OK) return Result.Cancelled;
                    var settings = string.IsNullOrWhiteSpace(form.SettingsPath) ? LocalSettings.Load(doc, form.SelectedLevel, form.SelectedPhase) : JsonFile.Read<ExportSettings>(form.SettingsPath);
                    if (string.IsNullOrWhiteSpace(form.SettingsPath))
                    {
                        settings.use_group = form.UseGroup; settings.floor_number = form.FloorNumber; settings.fire_resistant = form.FireResistant;
                        LocalSettings.Save(doc, form.SelectedLevel, form.SelectedPhase, settings);
                    }
                    var extractor = new Extractor(doc, form.SelectedLevel, form.SelectedPhase, settings);
                    var snapshot = extractor.Run();
                    var settingsCopy = form.OutputPath + ".settings.json";
                    if (File.Exists(settingsCopy)) settingsCopy = form.OutputPath + ".settings-" + Guid.NewGuid().ToString("N") + ".json";
                    JsonFile.Write(settingsCopy, settings);
                    snapshot.project.settings_file = Path.GetFileName(settingsCopy);
                    JsonFile.Write(form.OutputPath, snapshot);
                    // Keep user's settings intact; template only written when its path is new.
                    var templatePath = Path.Combine(Path.GetDirectoryName(form.OutputPath),
                        "settings-template-" + snapshot.project.level_id + "-" + snapshot.project.phase_id + ".json");
                    if (!File.Exists(templatePath)) JsonFile.Write(templatePath, extractor.Template());
                    var reportPath = WriteReport(form.OutputPath, ReportBuilder.SettingsJson(form.UseGroup, form.FloorNumber, form.FireResistant));
                    OpenReport(reportPath);
                    using (var report = new ReportForm(snapshot, form.OutputPath, reportPath))
                    {
                        report.ShowDialog(new WindowHandle(command.Application.MainWindowHandle));
                        var ids = report.SelectedIds.Select(s => long.TryParse(s, out var n) ? new ElementId(n) : ElementId.InvalidElementId)
                            .Where(id => doc.GetElement(id) != null).ToList();
                        if (ids.Any()) uiDoc.Selection.SetElementIds(ids);
                    }
                }
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; TaskDialog.Show("擷取失敗", ex.Message); return Result.Failed; }
        }
        // 檢討網頁放在 DLL 裡，每次擷取都在快照旁邊產生一份，其他電腦不用另外準備檔案
        static string WriteReport(string snapshotPath, string settingsJson)
        {
            string template;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("report.html"))
            using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8)) template = reader.ReadToEnd();
            var path = Path.Combine(Path.GetDirectoryName(snapshotPath), Path.GetFileNameWithoutExtension(snapshotPath) + "-檢討網頁.html");
            File.WriteAllText(path, ReportBuilder.Inject(template, File.ReadAllText(snapshotPath, System.Text.Encoding.UTF8), settingsJson), new System.Text.UTF8Encoding(false));
            return path;
        }
        internal static void OpenReport(string path)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { TaskDialog.Show("無法自動開啟檢討網頁", "請用瀏覽器開啟：\n" + path + "\n\n" + ex.Message); }
        }
    }
    internal sealed class WindowHandle : IWin32Window
    {
        public IntPtr Handle { get; }
        public WindowHandle(IntPtr handle) { Handle = handle; }
    }
    internal sealed class ExportForm : Form
    {
        readonly ComboBox levels = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        readonly ComboBox phases = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        readonly TextBox settings = new TextBox { Dock = DockStyle.Fill };
        public Level SelectedLevel => (Level)levels.SelectedItem;
        public Phase SelectedPhase => (Phase)phases.SelectedItem;
        public string SettingsPath => settings.Text.Trim();
        public string OutputPath { get; private set; }
        readonly Label roomInfo = new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(640, 0) };
        int RoomCount;
        readonly ComboBox useGroup = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        readonly TextBox floor = new TextBox { Dock = DockStyle.Fill };
        readonly ComboBox fire = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        public string UseGroup => useGroup.SelectedIndex > 0 ? (string)useGroup.SelectedItem : null;
        public int FloorNumber => int.Parse(floor.Text.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        public bool? FireResistant => fire.SelectedIndex == 1 ? true : fire.SelectedIndex == 2 ? false : (bool?)null;
        // 這一層之前選過就帶入；沒選過時類組和防火構造沿用畫面上的值（同棟通常一樣），樓層一定要重填
        void LoadReviewSettings(Document doc)
        {
            if (SelectedLevel == null || SelectedPhase == null) return;
            ExportSettings saved = null;
            try { saved = LocalSettings.Load(doc, SelectedLevel, SelectedPhase); } catch (ArgumentException) { }
            floor.Text = saved?.floor_number?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            if (saved?.use_group != null && useGroup.Items.Contains(saved.use_group)) useGroup.SelectedItem = saved.use_group;
            if (saved?.fire_resistant != null) fire.SelectedIndex = saved.fire_resistant.Value ? 1 : 2;
        }
        public ExportForm(Document doc)
        {
            Text = "第三組｜Revit 模型資料擷取"; Width = 720; Height = 560;
            StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 3, RowCount = 9 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            Controls.Add(layout);
            var note = new Label { Text = "選擇一個樓層與階段。只擷取主文件／主模型；不修改 Revit 元件。\n設定留空：自動使用「選擇直通梯」儲存的本機設定。選擇 JSON：改用該檔。",
                AutoSize = true, MaximumSize = new System.Drawing.Size(640, 0), Padding = new Padding(0, 0, 0, 18) };
            layout.Controls.Add(note, 0, 0); layout.SetColumnSpan(note, 3);
            layout.Controls.Add(new Label { Text = "樓層", AutoSize = true }, 0, 1); layout.Controls.Add(levels, 1, 1); layout.SetColumnSpan(levels, 2);
            layout.Controls.Add(new Label { Text = "階段", AutoSize = true }, 0, 2); layout.Controls.Add(phases, 1, 2); layout.SetColumnSpan(phases, 2);
            levels.DisplayMember = "Name"; phases.DisplayMember = "Name";
            foreach (var level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation)) levels.Items.Add(level);
            foreach (Phase phase in doc.Phases) phases.Items.Add(phase);
            if (levels.Items.Count > 0) levels.SelectedIndex = 0;
            var viewLevelId = doc.ActiveView.GenLevel?.Id;
            foreach (Level candidate in levels.Items)
                if (candidate.Id == viewLevelId) { levels.SelectedItem = candidate; break; }
            if (phases.Items.Count > 0) phases.SelectedIndex = phases.Items.Count - 1;
            var activePhase = doc.ActiveView.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
            foreach (Phase phase in phases.Items) if (phase.Id == activePhase) phases.SelectedItem = phase;
            useGroup.Items.Add("— 請選擇 —"); useGroup.Items.AddRange(ReportBuilder.UseGroups); useGroup.SelectedIndex = 0;
            fire.Items.AddRange(new object[] { "未確認", "是", "否" }); fire.SelectedIndex = 0;
            layout.Controls.Add(new Label { Text = "使用類組", AutoSize = true }, 0, 3); layout.Controls.Add(useGroup, 1, 3); layout.SetColumnSpan(useGroup, 2);
            layout.Controls.Add(new Label { Text = "第幾層", AutoSize = true }, 0, 4); layout.Controls.Add(floor, 1, 4); layout.SetColumnSpan(floor, 2);
            layout.Controls.Add(new Label { Text = "防火構造或\n不燃材料", AutoSize = true }, 0, 5); layout.Controls.Add(fire, 1, 5); layout.SetColumnSpan(fire, 2);
            LoadReviewSettings(doc);
            levels.SelectedIndexChanged += (s, e) => LoadReviewSettings(doc);
            phases.SelectedIndexChanged += (s, e) => LoadReviewSettings(doc);
            // 這一層、這個階段有幾間已放置的房間：選錯階段時房間會是 0，提早發現
            roomInfo.Text = "";
            void UpdateRoomInfo()
            {
                if (SelectedLevel == null || SelectedPhase == null) return;
                int n = new FilteredElementCollector(doc).OfClass(typeof(SpatialElement)).OfType<Autodesk.Revit.DB.Architecture.Room>()
                    .Count(r => r.LevelId == SelectedLevel.Id && r.Area > 0 && r.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() == SelectedPhase.Id);
                RoomCount = n;
                roomInfo.Text = n > 0 ? "這個樓層、階段有 " + n + " 間已放置的房間。" : "這個樓層、階段沒有已放置的房間，請確認階段是否選對。";
                roomInfo.ForeColor = n > 0 ? System.Drawing.SystemColors.ControlText : System.Drawing.Color.Firebrick;
            }
            UpdateRoomInfo();
            levels.SelectedIndexChanged += (s, e) => UpdateRoomInfo();
            phases.SelectedIndexChanged += (s, e) => UpdateRoomInfo();
            layout.Controls.Add(roomInfo, 0, 7); layout.SetColumnSpan(roomInfo, 3);
            layout.Controls.Add(new Label { Text = "設定（選填）", AutoSize = true }, 0, 6); layout.Controls.Add(settings, 1, 6);
            var browse = new Button { Text = "選擇 JSON", Dock = DockStyle.Fill };
            browse.Click += (s, e) => { using (var dialog = new OpenFileDialog { Filter = "JSON|*.json" }) if (dialog.ShowDialog(this) == DialogResult.OK) settings.Text = dialog.FileName; };
            layout.Controls.Add(browse, 2, 6);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
            var export = new Button { Text = "匯出快照…", AutoSize = true };
            export.Click += (s, e) => {
                if (SelectedLevel == null || SelectedPhase == null) { MessageBox.Show(this, "請選擇樓層與階段。"); return; }
                if (RoomCount == 0 && MessageBox.Show(this, "這個樓層、階段沒有已放置的房間，匯出後不會有任何檢討結果。\n多半是階段選錯了。仍要匯出嗎？", "沒有房間", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                if (UseGroup == null) { MessageBox.Show(this, "請選擇使用類組。外掛不從房間名稱推測。"); return; }
                if (!int.TryParse(floor.Text.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var floorNumber) || floorNumber == 0 || floorNumber < -5 || floorNumber > 120)
                { MessageBox.Show(this, "請填這是第幾層：地上填 1、2、3…，地下填 -1、-2…。"); return; }
                if (SettingsPath.Length > 0 && !File.Exists(SettingsPath)) { MessageBox.Show(this, "找不到設定檔。"); return; }
                using (var dialog = new SaveFileDialog { Filter = "模型快照 JSON|*.json", FileName = "real-模型快照.json", OverwritePrompt = true })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    if (SettingsPath.Length > 0 && string.Equals(Path.GetFullPath(SettingsPath), Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase))
                    { MessageBox.Show(this, "快照不能覆蓋設定檔。"); return; }
                    OutputPath = dialog.FileName; DialogResult = DialogResult.OK;
                }
            };
            buttons.Controls.Add(cancel); buttons.Controls.Add(export); layout.Controls.Add(buttons, 0, 8); layout.SetColumnSpan(buttons, 3);
            AcceptButton = export; CancelButton = cancel;
        }
    }
    internal sealed class ReportForm : Form
    {
        public List<string> SelectedIds = new List<string>();
        public ReportForm(Snapshot snapshot, string path, string reportPath)
        {
            Text = "擷取完成｜缺漏清單"; Width = 1000; Height = 620;
            StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            Controls.Add(layout);
            var groups = WarningSummary.Group(snapshot.warnings);
            int mustFix = groups.Count(g => g.severity == "error");
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = $"{snapshot.project.level.name}／{snapshot.project.phase}　房間 {snapshot.rooms.Count}　門 {snapshot.doors.Count}（另有待定位 {snapshot.unresolved_doors.Count}）\n樓梯 {snapshot.stairs.Count + snapshot.unresolved_stairs.Count}（未列入計算 {snapshot.unresolved_stairs.Count}）　走廊 {snapshot.corridors.Count}　{(groups.Count == 0 ? "沒有需要處理的項目" : $"要處理 {mustFix} 類、提醒 {groups.Count - mustFix} 類")}（不代表法規判定）\n{path}" }, 0, 0);
            var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            grid.Columns.Add("level", "等級"); grid.Columns.Add("code", "問題代碼"); grid.Columns.Add("count", "件數"); grid.Columns.Add("message", "說明");
            grid.Columns[0].FillWeight = 12; grid.Columns[1].FillWeight = 32; grid.Columns[2].FillWeight = 8; grid.Columns[3].FillWeight = 100;
            grid.Columns[3].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            foreach (var g in groups)
            {
                var row = grid.Rows[grid.Rows.Add(g.severity == "error" ? "要處理" : "提醒", g.code, g.count, g.message)];
                row.Tag = g.element_ids;
            }
            layout.Controls.Add(grid, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var close = new Button { Text = "關閉", DialogResult = DialogResult.OK };
            var select = new Button { Text = "在 Revit 選取所選元件", AutoSize = true };
            select.Click += (s, e) => { SelectedIds = grid.SelectedRows.Cast<DataGridViewRow>().SelectMany(r => (List<string>)r.Tag).Distinct().ToList(); Close(); };
            var open = new Button { Text = "開啟檢討網頁", AutoSize = true };
            open.Click += (s, e) => ExportCommand.OpenReport(reportPath);
            buttons.Controls.Add(close); buttons.Controls.Add(select); buttons.Controls.Add(open); layout.Controls.Add(buttons, 0, 2);
        }
    }
}
