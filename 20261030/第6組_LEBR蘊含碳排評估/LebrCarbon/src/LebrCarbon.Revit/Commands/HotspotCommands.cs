using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LebrCarbon.Core.Visualization;
using LebrCarbon.Data;
using LebrCarbon.Revit.Extraction;

namespace LebrCarbon.Revit.Commands;

/// <summary>
/// F8 3D 熱點著色：在目前 3D 視圖依構件碳排量上色（綠 → 紅，5 級）。
/// 構件碳排 = 主結構材料數量法（數量檢核，A1-A4）＋ 非主結構面積 × B-LCC 新建碳排。
/// 只改目前視圖的元件圖形覆寫，不改模型資料；用「清除著色」還原。
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class HotspotColorCommand : IExternalCommand
{
    /// <summary>綠、黃綠、黃、橘、紅。</summary>
    internal static readonly Color[] Palette =
    {
        new(0, 153, 76), new(153, 204, 0), new(255, 204, 0), new(255, 128, 0), new(204, 0, 0),
    };

    internal static readonly string[] PaletteNames = { "綠", "黃綠", "黃", "橘", "紅" };

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var uidoc = data.Application.ActiveUIDocument;
        var doc = uidoc.Document;
        var view = uidoc.ActiveView;

        if (view is not View3D v3 || v3.IsTemplate)
        {
            TaskDialog.Show("LEBR 熱點著色", "請先切換到 3D 視圖再執行。");
            return Result.Cancelled;
        }

        try
        {
            var warnings = new List<string>();
            var carbon = Collect(doc, warnings);
            if (carbon.Count == 0)
            {
                TaskDialog.Show("LEBR 熱點著色",
                    "找不到可著色的構件。\n\n請確認：\n· 非主結構類型已填 LEBR_BlccId，或\n· 主結構元件有材質、且能對應到係數（可按「模型檢查」）。");
                return Result.Cancelled;
            }

            var values = carbon.Values.Select(c => c.Total).ToList();
            var breaks = HeatmapClassifier.Breaks(values);
            var legend = HeatmapClassifier.Summarize(values, breaks);

            var solid = SolidFill(doc);
            var colored = new List<ElementId>();
            using (var t = new Transaction(doc, "LEBR 熱點著色"))
            {
                t.Start();
                foreach (var c in carbon.Values)
                {
                    if (c.Total <= 0) continue;
                    var id = new ElementId(c.ElementId);
                    if (doc.GetElement(id) is null) continue;

                    var color = Palette[HeatmapClassifier.Classify(c.Total, breaks)];
                    var ogs = new OverrideGraphicSettings()
                        .SetSurfaceForegroundPatternColor(color)
                        .SetCutForegroundPatternColor(color);
                    if (solid is not null)
                        ogs.SetSurfaceForegroundPatternId(solid.Id).SetCutForegroundPatternId(solid.Id);

                    view.SetElementOverrides(id, ogs);
                    colored.Add(id);
                }
                t.Commit();
            }
            HotspotState.Remember(doc, view.Id, colored);

            ShowLegend(doc, carbon, legend, warnings, solid is null);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("LEBR 熱點著色失敗", ex.ToString());
            return Result.Failed;
        }
    }

    /// <summary>彙整每個構件的主結構與非主結構碳排。</summary>
    private static Dictionary<long, ElementCarbon> Collect(Document doc, List<string> warnings)
    {
        var map = new Dictionary<long, ElementCarbon>();

        // 主結構：材料數量 × 附錄一係數（與「數量檢核」同一套計算）
        try
        {
            var r = CalculationService.Run(doc);
            foreach (var g in r.Lines.GroupBy(l => l.ElementId))
            {
                if (!long.TryParse(g.Key, out var id)) continue;
                map[id] = new ElementCarbon(id, g.Sum(l => l.Emission), 0);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"主結構數量計算失敗，只以非主結構著色：{ex.Message}");
        }

        // 非主結構：面積 × B-LCC 新建碳排；一個類型有多個 ID 時相加
        var blcc = BlccLibrary.Load().ById;
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in NonStructuralExtractor.ExtractElements(doc, double.NegativeInfinity, warnings))
        {
            var ns = 0.0;
            foreach (var bid in e.BlccIds)
            {
                if (blcc.TryGetValue(bid, out var item)) ns += e.AreaM2 * item.New;
                else unknown.Add(bid);
            }
            map[e.ElementId] = map.TryGetValue(e.ElementId, out var c)
                ? c with { NonStructural = c.NonStructural + ns }
                : new ElementCarbon(e.ElementId, 0, ns);
        }
        if (unknown.Count > 0)
            warnings.Add($"B-LCC 資料庫找不到這些 ID，未計入：{string.Join("、", unknown)}");

        return map;
    }

    private static FillPatternElement? SolidFill(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(FillPatternElement))
            .Cast<FillPatternElement>()
            .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);

    private static void ShowLegend(Document doc, Dictionary<long, ElementCarbon> carbon,
        List<HeatClass> legend, List<string> warnings, bool noSolidFill)
    {
        var sb = new StringBuilder();
        sb.AppendLine("圖例（每級構件數大致相同）：");
        foreach (var c in legend.OrderByDescending(c => c.Index))
            sb.AppendLine($"  {PaletteNames[c.Index]}　{c.Min:N0} ～ {c.Max:N0} kgCO2e　（{c.Count} 個）");

        sb.AppendLine();
        sb.AppendLine("碳排最高的 5 個構件：");
        foreach (var c in carbon.Values.OrderByDescending(c => c.Total).Take(5))
        {
            var e = doc.GetElement(new ElementId(c.ElementId));
            var type = e is null ? null : doc.GetElement(e.GetTypeId());
            var parts = c.Structural > 0 && c.NonStructural > 0
                ? $"（主結構 {c.Structural:N0} ＋ 非主結構 {c.NonStructural:N0}）"
                : "";
            sb.AppendLine($"  · {e?.Category?.Name}「{type?.Name}」ID {c.ElementId}：{c.Total:N0}{parts}");
        }

        var total = carbon.Values.Sum(c => c.Total);
        sb.AppendLine();
        sb.AppendLine($"著色構件 {carbon.Values.Count(c => c.Total > 0)} 個，合計 {total:N0} kgCO2e");
        sb.AppendLine($"  主結構（材料數量法）{carbon.Values.Sum(c => c.Structural):N0}");
        sb.AppendLine($"  非主結構（B-LCC 新建）{carbon.Values.Sum(c => c.NonStructural):N0}");

        if (noSolidFill) warnings.Add("專案中找不到實心填滿樣式，只能改顏色、無法填滿表面。");
        if (warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("注意：");
            foreach (var w in warnings.Distinct().Take(8)) sb.AppendLine($"  · {w}");
            if (warnings.Count > 8) sb.AppendLine($"  ……另 {warnings.Count - 8} 筆");
        }

        new TaskDialog("LEBR 熱點著色")
        {
            MainInstruction = $"已依碳排量為 {carbon.Values.Count(c => c.Total > 0)} 個構件著色",
            MainContent = sb.ToString(),
            FooterText = "著色用來找出碳排集中的構件，數字不等於 LEBR 評估的 CFs（主結構評估採迴歸公式）。看不到顏色時，請把視覺樣式切為「著色」。",
        }.Show();
    }
}

/// <summary>清除著色：移除「熱點著色」加在目前視圖的元件圖形覆寫。</summary>
[Transaction(TransactionMode.Manual)]
public sealed class ClearColorCommand : IExternalCommand
{
    private static readonly BuiltInCategory[] Categories =
    {
        BuiltInCategory.OST_Walls, BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors,
        BuiltInCategory.OST_Floors, BuiltInCategory.OST_CurtainWallPanels,
        BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming,
        BuiltInCategory.OST_StructuralFoundation,
    };

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var uidoc = data.Application.ActiveUIDocument;
        var doc = uidoc.Document;
        var view = uidoc.ActiveView;

        var ids = HotspotState.Recall(doc, view.Id);
        if (ids is null)
        {
            // Revit 重開後不會記得上次著色了哪些構件，改為清除相關類別的所有元件覆寫
            var td = new TaskDialog("LEBR 清除著色")
            {
                MainInstruction = "找不到這次開啟 Revit 後的著色紀錄",
                MainContent = "要清除目前視圖中牆、門窗、樓板、帷幕嵌板、柱、梁、基礎的所有元件圖形覆寫嗎？\n\n注意：這也會清掉你自己手動設定的元件覆寫。",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No,
            };
            if (td.Show() != TaskDialogResult.Yes) return Result.Cancelled;

            ids = new FilteredElementCollector(doc, view.Id)
                .WherePasses(new ElementMulticategoryFilter(Categories))
                .WhereElementIsNotElementType()
                .ToElementIds()
                .ToList();
        }

        try
        {
            var cleared = 0;
            using (var t = new Transaction(doc, "LEBR 清除著色"))
            {
                t.Start();
                var blank = new OverrideGraphicSettings();
                foreach (var id in ids)
                {
                    if (doc.GetElement(id) is null) continue;
                    view.SetElementOverrides(id, blank);
                    cleared++;
                }
                t.Commit();
            }
            HotspotState.Forget(doc, view.Id);
            TaskDialog.Show("LEBR 清除著色", $"已清除 {cleared} 個構件的著色。");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("LEBR 清除著色失敗", ex.ToString());
            return Result.Failed;
        }
    }
}

/// <summary>記住每個視圖著色過的構件，清除時只還原這些，不動使用者自己設定的覆寫。</summary>
internal static class HotspotState
{
    private static readonly Dictionary<(string Doc, long View), List<ElementId>> Colored = new();

    private static (string, long) Key(Document doc, ElementId viewId) => (doc.PathName + "|" + doc.Title, viewId.Value);

    public static void Remember(Document doc, ElementId viewId, List<ElementId> ids)
    {
        // 重複著色時累加，確保先前著色但這次沒算到的構件也能被清除
        var key = Key(doc, viewId);
        if (Colored.TryGetValue(key, out var old)) ids = old.Union(ids).ToList();
        Colored[key] = ids;
    }

    public static List<ElementId>? Recall(Document doc, ElementId viewId) =>
        Colored.TryGetValue(Key(doc, viewId), out var ids) ? ids : null;

    public static void Forget(Document doc, ElementId viewId) => Colored.Remove(Key(doc, viewId));
}
