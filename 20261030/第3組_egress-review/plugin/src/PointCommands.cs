using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Windows.Forms;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using Form = System.Windows.Forms.Form;
using ComboBox = System.Windows.Forms.ComboBox;

namespace Group3.EgressExporter
{
    internal sealed class ElementKindFilter : ISelectionFilter
    {
        readonly bool stairs;
        internal ElementKindFilter(bool stairs) { this.stairs = stairs; }
        public bool AllowElement(Element e) => e.DesignOption == null && (stairs ? StairKind.IsStair(e) :
            e is FamilyInstance && e.Category?.Id.Value == (long)BuiltInCategory.OST_Doors);
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
    // 樓梯品類的元件：元件式樓梯（Stairs），或非元件式的舊式樓梯、樓梯族群
    internal static class StairKind
    {
        internal static bool IsStair(Element e) => e is Stairs || e.Category?.Id.Value == (long)BuiltInCategory.OST_Stairs;
        // 非元件式樓梯沒有底／頂高程屬性，用包圍框高度判斷；0.2 m 是幾何容差，非法規值
        internal static bool CrossesLevel(Element e, Level level)
        {
            double tol = UnitUtils.ConvertToInternalUnits(0.2, UnitTypeId.Meters), z = level.ProjectElevation;
            if (e is Stairs s) return z >= s.BaseElevation - 0.01 && z <= s.TopElevation + 0.01;
            var b = e.get_BoundingBox(null);
            return b != null && z >= b.Min.Z - tol && z <= b.Max.Z + tol;
        }
    }
    internal static class PointInteraction
    {
        internal static void Scope(UIDocument uiDoc, out Level level, out Phase phase)
        {
            if (uiDoc == null || uiDoc.Document.IsFamilyDocument) throw new ArgumentException("請先開啟專案的樓層平面圖。");
            var view = uiDoc.ActiveView;
            level = view.GenLevel;
            phase = uiDoc.Document.GetElement(view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ?? ElementId.InvalidElementId) as Phase;
            if (!(view is ViewPlan) || view.ViewType != ViewType.FloorPlan || level == null || phase == null)
                throw new ArgumentException("請先切到要設定的樓板平面圖；定位會使用該視圖的樓層與階段。");
        }
        internal static Element Select(UIDocument uiDoc, bool stairs, bool useSelection = true)
        {
            var filter = new ElementKindFilter(stairs);
            var selected = uiDoc.Selection.GetElementIds().Select(uiDoc.Document.GetElement).Where(filter.AllowElement).ToList();
            if (useSelection && selected.Count == 1) return selected[0];
            var reference = uiDoc.Selection.PickObject(ObjectType.Element, filter, stairs ?
                "請選取整座樓梯（可按 Tab 切換選取；Esc 取消）" : "請選取要設定位置的門（Esc 取消）");
            return uiDoc.Document.GetElement(reference.ElementId);
        }
        internal static void CheckElement(Element element, Level level, Phase phase)
        {
            if (!RevitPositions.Exists(element, phase)) throw new ArgumentException("元件不在主模型或本視圖階段。");
            if (StairKind.IsStair(element))
            {
                if (!StairKind.CrossesLevel(element, level))
                    throw new ArgumentException("樓梯沒有跨越本樓層，請切換正確樓層。");
            }
            else if (element is FamilyInstance door && door.LevelId != level.Id &&
                door.get_FromRoom(phase)?.LevelId != level.Id && door.get_ToRoom(phase)?.LevelId != level.Id)
                throw new ArgumentException("門不屬於本樓層，也沒有本樓層的相鄰房間。");
        }
        internal static XYZ Pick(UIDocument uiDoc, Level level, string prompt)
        {
            // A horizontal temporary work plane makes XY independent of any old, tilted work plane.
            // Roll back the entire group on success, cancellation and failure; never leave model changes.
            using (var group = new TransactionGroup(uiDoc.Document, "暫時定位工作平面"))
            {
                if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("無法開始定位工作平面。");
                try
                {
                    using (var transaction = new Transaction(uiDoc.Document, "暫時定位工作平面"))
                    {
                        transaction.Start();
                        var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, level.ProjectElevation));
                        uiDoc.ActiveView.SketchPlane = SketchPlane.Create(uiDoc.Document, plane);
                        if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("無法設定定位工作平面。");
                    }
                    var point = uiDoc.Selection.PickPoint(ObjectSnapTypes.Endpoints | ObjectSnapTypes.Midpoints |
                        ObjectSnapTypes.Intersections | ObjectSnapTypes.Nearest, prompt + "（Esc 取消）");
                    return new XYZ(point.X, point.Y, level.ProjectElevation);
                }
                finally
                {
                    if (group.GetStatus() == TransactionStatus.Started && group.RollBack() != TransactionStatus.RolledBack)
                        throw new InvalidOperationException("定位工作平面未成功還原，未儲存定位設定。");
                }
            }
        }
        internal static PickedPointSetting Capture(Element element, XYZ point, string source) => new PickedPointSetting {
            point_m = RevitPositions.Metres(point), geometry_fingerprint = RevitPositions.Fingerprint(element), source = source };
        internal static string Coordinates(XYZ point)
        {
            var p = RevitPositions.Metres(point);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "X {0:F3} m，Y {1:F3} m，Z {2:F3} m", p[0], p[1], p[2]);
        }
        internal static bool Confirm(string title, string detail)
        {
            var dialog = new TaskDialog(title) { MainInstruction = "儲存這個定位設定？", MainContent = detail,
                CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel, DefaultButton = TaskDialogResult.Cancel };
            return dialog.Show() == TaskDialogResult.Ok;
        }
    }
    [Transaction(TransactionMode.Manual)]
    public sealed class SetDoorPositionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData command, ref string message, ElementSet elements)
        {
            try
            {
                var uiDoc = command.Application.ActiveUIDocument;
                PointInteraction.Scope(uiDoc, out var level, out var phase);
                var door = (FamilyInstance)PointInteraction.Select(uiDoc, false);
                PointInteraction.CheckElement(door, level, phase);
                var settings = LocalSettings.Load(uiDoc.Document, level, phase);
                XYZ candidate = null; string candidateSource = null;
                try { candidate = RevitPositions.DoorCandidate(door, level, out candidateSource); } catch (ArgumentException) { }
                if (candidate != null) candidate = new XYZ(candidate.X, candidate.Y, level.ProjectElevation);
                var dialog = new TaskDialog("設定門位置") { MainInstruction = level.Name + "／" + phase.Name + "：門 " + door.Id.Value,
                    MainContent = "請核對實際可通行門口的位置。包圍框中心只作候選，可能偏離門口。\n" +
                        (candidate == null ? "無可用候選位置。" : "候選：" + PointInteraction.Coordinates(candidate)),
                    CommonButtons = TaskDialogCommonButtons.Cancel, DefaultButton = TaskDialogResult.Cancel };
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "在平面圖點選門口位置");
                if (candidate != null) dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "已核對，採用候選位置");
                if (settings.doors.Any(d => d.unique_id == door.UniqueId))
                    dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "清除此門的本機定位設定");
                var choice = dialog.Show();
                if (choice != TaskDialogResult.CommandLink1 && choice != TaskDialogResult.CommandLink2 &&
                    choice != TaskDialogResult.CommandLink3) return Result.Cancelled;
                if (choice == TaskDialogResult.CommandLink3)
                {
                    settings.doors.RemoveAll(d => d.unique_id == door.UniqueId);
                    LocalSettings.Save(uiDoc.Document, level, phase, settings);
                    TaskDialog.Show("設定門位置", "已清除設定；下次匯出會重新自動擷取或列為待確認。");
                    return Result.Succeeded;
                }
                var point = choice == TaskDialogResult.CommandLink2 ? candidate :
                    PointInteraction.Pick(uiDoc, level, "請點選此門實際可通行門口的位置");
                var saved = PointInteraction.Capture(door, point, choice == TaskDialogResult.CommandLink2 ?
                    "user_confirmed:" + candidateSource : "user_picked_plan_point");
                RevitPositions.Validate(door, level, saved, false);
                if (!PointInteraction.Confirm("設定門位置", level.Name + "／" + phase.Name + "\n門 " + door.Id.Value +
                    "\n" + PointInteraction.Coordinates(point) + "\n此操作只確認位置，不會設定淨寬或房門關聯。")) return Result.Cancelled;
                PointRules.SetDoor(settings, new DoorSetting { unique_id = door.UniqueId, picked_point = saved });
                var path = LocalSettings.Save(uiDoc.Document, level, phase, settings);
                TaskDialog.Show("設定門位置", "已儲存。下次匯出相同樓層／階段時，設定欄留空即可自動套用。\n" + path);
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex) { message = ex.Message; TaskDialog.Show("門定位未完成", ex.Message); return Result.Failed; }
        }
    }
    [Transaction(TransactionMode.Manual)]
    public sealed class SetStairEndpointCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData command, ref string message, ElementSet elements)
        {
            try
            {
                var uiDoc = command.Application.ActiveUIDocument;
                PointInteraction.Scope(uiDoc, out var level, out var phase);
                Element stair = PointInteraction.Select(uiDoc, true);
                PointInteraction.CheckElement(stair, level, phase);
                var settings = LocalSettings.Load(uiDoc.Document, level, phase);
                string endpointType; bool? direct;
                // 逐層建模的樓梯在同一樓梯間有上、下兩個元件，平面上外框重疊，點到哪個不一定，所以讓使用者直接選
                var choices = stair is Stairs component ? OverlappingStairs(uiDoc.Document, component, level, phase).Cast<Element>().ToList() : new System.Collections.Generic.List<Element> { stair };
                bool IsSet(Element s) => settings.stairs.Any(x => x != null && x.unique_id == s.UniqueId && x.picked_point != null);
                // 第一階存的位置離這座樓梯自己的梯段路徑超過 0.5 m，多半是點成重疊的另一座（操作提醒，非法規值）
                bool LooksOff(Element s)
                {
                    var saved = settings.stairs.FirstOrDefault(x => x != null && x.unique_id == s.UniqueId && x.picked_point != null && x.endpoint_type == "first_step");
                    var path = saved == null ? null : FirstStepHint(uiDoc.Document, s, level);
                    return path != null && Geometry.Distance(saved.picked_point.point_m, RevitPositions.Metres(path)) > 0.5;
                }
                // 預設停在還沒處理好的那座；都好了才停在點到的那座
                int start = choices.FindIndex(s => !IsSet(s) || LooksOff(s));
                if (start < 0) start = choices.FindIndex(s => s.Id == stair.Id);
                var labels = choices.Select(s => Describe(s, level) + (!IsSet(s) ? "｜尚未設定" : LooksOff(s) ? "｜位置待確認" : "｜已設定")).ToArray();
                using (var form = new StairPointForm(level.Name, phase.Name, labels, start))
                {
                    if (form.ShowDialog(new WindowHandle(command.Application.MainWindowHandle)) != DialogResult.OK) return Result.Cancelled;
                    endpointType = form.EndpointType; direct = form.IsDirect; stair = choices[form.StairIndex];
                }
                FamilyInstance door = null;
                if (endpointType == "fire_door")
                {
                    door = (FamilyInstance)PointInteraction.Select(uiDoc, false, false);
                    PointInteraction.CheckElement(door, level, phase);
                }
                var hint = FirstStepHint(uiDoc.Document, stair, level);
                var point = PointInteraction.Pick(uiDoc, level, endpointType == "fire_door" ?
                    "請點選剛才指定防火門的通行位置" : Describe(stair, level) + "：請點選本樓層的第一階位置" +
                    (hint == null ? "" : "（梯段路徑推得 " + PointInteraction.Coordinates(hint) + "，請核對）"));
                var saved = PointInteraction.Capture(stair, point, "user_picked_plan_point");
                RevitPositions.Validate(stair, level, saved, true);
                if (door != null)
                {
                    var doorPoint = PointInteraction.Capture(door, point, "user_picked_plan_point");
                    RevitPositions.Validate(door, level, doorPoint, false);
                    saved.reference_geometry_fingerprint = doorPoint.geometry_fingerprint;
                }
                // 點的位置離這座樓梯自己的第一階太遠時提醒：重疊的上下樓梯很容易點成另一座的（0.5 m 是操作提醒，非法規值）
                string offsetNote = "";
                if (endpointType == "first_step" && hint != null)
                {
                    double[] a = RevitPositions.Metres(point), b = RevitPositions.Metres(hint);
                    double off = Geometry.Distance(a, b);
                    if (off > 0.5) offsetNote = "\n\n注意：這個位置和這座樓梯梯段路徑的第一階相距 " +
                        off.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " m，可能點成另一座了，請核對。";
                }
                if (!PointInteraction.Confirm("設定樓梯終點", level.Name + "／" + phase.Name + offsetNote + "\n\n" + Describe(stair, level) + "\n" +
                    (endpointType == "fire_door" ? "防火門 " + door.Id.Value : "第一階") + "\n" + PointInteraction.Coordinates(point) +
                    "\n直通樓梯資格：" + (direct.HasValue ? (direct.Value ? "使用者確認是" : "使用者確認否") : "待確認") +
                    "\n終點類型與資格需依 C 組員的規則確認；外掛不自行判定。")) return Result.Cancelled;
                PointRules.SetStair(settings, new StairSetting { unique_id = stair.UniqueId, is_direct_stair = direct,
                    endpoint_type = endpointType, endpoint_unique_id = door?.UniqueId, picked_point = saved });
                var path = LocalSettings.Save(uiDoc.Document, level, phase, settings);
                var remaining = choices.Where(s => s.Id != stair.Id && !IsSet(s)).Select(s => Describe(s, level)).ToList();
                TaskDialog.Show("設定樓梯終點", "已儲存 " + Describe(stair, level) + "。下次匯出相同樓層／階段時，設定欄留空即可自動套用。\n" +
                    (remaining.Any() ? "同一樓梯間還沒設定：" + string.Join("、", remaining) + "。請再按一次這個按鈕。\n" : "") + path);
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex) { message = ex.Message; TaskDialog.Show("樓梯終點未完成", ex.Message); return Result.Failed; }
        }
        // 跨越本層、平面上和所選樓梯重疊的所有樓梯（包含所選的這座）
        internal static System.Collections.Generic.List<Stairs> OverlappingStairs(Document doc, Stairs picked, Level level, Phase phase)
        {
            var a = picked.get_BoundingBox(null);
            var list = new FilteredElementCollector(doc).OfClass(typeof(Stairs)).Cast<Stairs>().Where(s => {
                if (s.Id == picked.Id) return true;
                var b = s.get_BoundingBox(null);
                return a != null && b != null && s.DesignOption == null && RevitPositions.Exists(s, phase) &&
                    level.ProjectElevation >= s.BaseElevation - 0.01 && level.ProjectElevation <= s.TopElevation + 0.01 &&
                    a.Min.X < b.Max.X && b.Min.X < a.Max.X && a.Min.Y < b.Max.Y && b.Min.Y < a.Max.Y;
            }).OrderBy(s => s.Id.Value).ToList();
            return list;
        }
        // 給使用者核對用的位置，和匯出時自動推算第一階的方式相同；實際終點仍以使用者點選的為準
        static XYZ FirstStepHint(Document doc, Element stair, Level level) => stair is Stairs s ? StairEndpoint.Detect(doc, s, level, out _, out _) : null;
        internal static string Describe(Element element, Level level)
        {
            if (!(element is Stairs stair)) return "樓梯 " + element.Id.Value + "：非元件式，請手動點選第一階";
            double tol = UnitUtils.ConvertToInternalUnits(0.2, UnitTypeId.Meters); // 判斷樓梯底／頂是否在本層，幾何容差，非法規值
            string direction = Math.Abs(stair.TopElevation - level.ProjectElevation) <= tol ? "往下（從下層到本層）" :
                Math.Abs(stair.BaseElevation - level.ProjectElevation) <= tol ? "往上（從本層到上層）" : "穿過本層";
            return "樓梯 " + stair.Id.Value + "：" + direction;
        }
    }
    // 主要流程：點一下樓梯，外掛列出這個位置的樓梯和偵測到的第一階，使用者只要勾要用哪幾座
    [Transaction(TransactionMode.Manual)]
    public sealed class SelectDirectStairsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData command, ref string message, ElementSet elements)
        {
            try
            {
                var uiDoc = command.Application.ActiveUIDocument;
                PointInteraction.Scope(uiDoc, out var level, out var phase);
                var element = PointInteraction.Select(uiDoc, true);
                PointInteraction.CheckElement(element, level, phase);
                var doc = uiDoc.Document;
                var settings = LocalSettings.Load(doc, level, phase);
                // 元件式樓梯列出同一位置重疊的上下兩座；非元件式樓梯（舊式草圖樓梯、樓梯族群）只列自己
                var choices = element is Stairs picked
                    ? SetStairEndpointCommand.OverlappingStairs(doc, picked, level, phase).Cast<Element>().ToList()
                    : new System.Collections.Generic.List<Element> { element };
                var detected = choices.Select(s => s is Stairs st ? StairEndpoint.Detect(doc, st, level, out _, out _) : null).ToList();
                var rows = choices.Select((s, i) => {
                    var saved = settings.stairs.FirstOrDefault(x => x != null && x.unique_id == s.UniqueId);
                    var p = detected[i];
                    return new SelectStairsForm.Row { Label = SetStairEndpointCommand.Describe(s, level) + "｜" +
                        (p == null ? "偵測不到第一階，確定後請在平面圖點選" : "第一階 " + PointInteraction.Coordinates(new XYZ(p.X, p.Y, level.ProjectElevation))),
                        Checked = saved?.is_direct_stair != false && (p != null || saved?.is_direct_stair == true || choices.Count == 1) };
                }).ToList();
                bool fireDoor;
                var use = new bool[choices.Count];
                using (var form = new SelectStairsForm(level.Name, phase.Name, rows))
                {
                    if (form.ShowDialog(new WindowHandle(command.Application.MainWindowHandle)) != DialogResult.OK) return Result.Cancelled;
                    fireDoor = form.UseFireDoor;
                    for (int i = 0; i < choices.Count; i++) use[i] = form.IsChecked(i);
                }
                // 法規第 93 條第 2 項：安全梯得計算至進入樓梯間的防火門。選這個時只要點那扇門，終點用門的插入點
                FamilyInstance door = null;
                if (fireDoor && use.Any(x => x))
                {
                    door = (FamilyInstance)PointInteraction.Select(uiDoc, false, false);
                    PointInteraction.CheckElement(door, level, phase);
                    if (!(door.Location is LocationPoint)) throw new ArgumentException("這扇門沒有插入點，不能當終點；請選安全梯的防火門。");
                }
                for (int i = 0; i < choices.Count; i++)
                {
                    var s = choices[i];
                    if (!use[i]) { PointRules.SetStair(settings, new StairSetting { unique_id = s.UniqueId, is_direct_stair = false }); continue; }
                    if (door != null)
                    {
                        PointRules.SetStair(settings, new StairSetting { unique_id = s.UniqueId, is_direct_stair = true, endpoint_type = "fire_door", endpoint_unique_id = door.UniqueId });
                        continue;
                    }
                    if (detected[i] != null)
                    {
                        // 位置不存，每次擷取時從梯段路徑重新偵測
                        PointRules.SetStair(settings, new StairSetting { unique_id = s.UniqueId, is_direct_stair = true, endpoint_type = "first_step" });
                        continue;
                    }
                    var point = PointInteraction.Pick(uiDoc, level, SetStairEndpointCommand.Describe(s, level) + "：外掛偵測不到第一階，請在平面圖點選第一階位置（Esc 取消）");
                    var saved = PointInteraction.Capture(s, point, "user_picked_plan_point");
                    RevitPositions.Validate(s, level, saved, true);
                    if (!PointInteraction.Confirm("選擇直通梯", level.Name + "／" + phase.Name + "\n樓梯 " + s.Id.Value + "\n第一階 " +
                        PointInteraction.Coordinates(point) + "\n儲存後這座樓梯會當直通樓梯計算。")) return Result.Cancelled;
                    PointRules.SetStair(settings, new StairSetting { unique_id = s.UniqueId, is_direct_stair = true, endpoint_type = "first_step", picked_point = saved });
                }
                LocalSettings.Save(doc, level, phase, settings);
                int used = settings.stairs.Count(x => x != null && x.is_direct_stair == true);
                TaskDialog.Show("選擇直通梯", "已儲存。這一層會用 " + used + " 座樓梯計算，沒選的不列入。" +
                    (door != null ? "\n終點：防火門 " + door.Id.Value : "") + "\n其他位置還有直通梯要用的話，請再按一次這個按鈕。");
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex) { message = ex.Message; TaskDialog.Show("選擇直通梯未完成", ex.Message); return Result.Failed; }
        }
    }
    internal sealed class SelectStairsForm : Form
    {
        internal sealed class Row { internal string Label; internal bool Checked; }
        readonly CheckedListBox list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
        readonly ComboBox endpoint = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        internal bool IsChecked(int index) => list.GetItemChecked(index);
        internal bool UseFireDoor => endpoint.SelectedIndex == 1;
        internal SelectStairsForm(string level, string phase, System.Collections.Generic.List<Row> rows)
        {
            Text = "選擇直通梯"; Width = 760; Height = 360; StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);
            layout.Controls.Add(new Label { AutoSize = true, Padding = new Padding(0, 0, 0, 10), Text = level + "／" + phase + "\n勾選要當直通樓梯使用的樓梯。第一階由外掛從梯段路徑偵測；偵測不到的，確定後請你點選。" }, 0, 0);
            foreach (var row in rows) list.Items.Add(row.Label, row.Checked);
            layout.Controls.Add(list, 0, 1);
            endpoint.Items.AddRange(new object[] { "終點：直通樓梯的第一階", "終點：安全梯，計算到進入樓梯間的防火門（確定後點選那扇門）" });
            endpoint.SelectedIndex = 0;
            layout.Controls.Add(endpoint, 0, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var ok = new Button { Text = "確定", DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(ok); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 3);
            AcceptButton = ok; CancelButton = cancel;
        }
    }
    internal sealed class StairPointForm : Form
    {
        readonly ComboBox which = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        internal int StairIndex => which.SelectedIndex;
        readonly ComboBox type = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        readonly ComboBox direct = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        internal string EndpointType => type.SelectedIndex == 2 ? "fire_door" : "first_step";
        internal bool? IsDirect => direct.SelectedIndex == 0 ? (bool?)null : direct.SelectedIndex == 1;
        internal StairPointForm(string level, string phase, string[] stairs, int selected)
        {
            Text = "設定樓梯終點"; Width = 600; Height = 340; StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 6 };
            Controls.Add(layout);
            layout.Controls.Add(new Label { AutoSize = true, Text = level + "／" + phase + "\n先選要設定哪座樓梯，再選終點。不知道的資格保留待確認。" });
            which.Items.AddRange(stairs); which.SelectedIndex = selected;
            layout.Controls.Add(which);
            type.Items.AddRange(new object[] { "請選擇終點類型", "第一階：在平面圖點選位置", "防火門：選門，再點選通行位置" }); type.SelectedIndex = 1;
            direct.Items.AddRange(new object[] { "直通樓梯資格：待確認", "已確認是直通樓梯", "已確認不是直通樓梯" }); direct.SelectedIndex = 0;
            layout.Controls.Add(type); layout.Controls.Add(direct);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var next = new Button { Text = "下一步", AutoSize = true };
            next.Click += (s, e) => { if (type.SelectedIndex == 0) { MessageBox.Show(this, "請先選擇終點類型。"); return; } DialogResult = DialogResult.OK; };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(next); buttons.Controls.Add(cancel); layout.Controls.Add(buttons); CancelButton = cancel;
        }
    }
}
