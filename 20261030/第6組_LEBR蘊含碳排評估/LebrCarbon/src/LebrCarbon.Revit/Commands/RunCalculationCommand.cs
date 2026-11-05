using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LebrCarbon.Core.Engine;

namespace LebrCarbon.Revit.Commands;

/// <summary>
/// 數量檢核：以材料數量 × 附錄一 P-LCC 係數估算主結構碳排（手冊 4-1 節「BIM 模型估算」替代方法），
/// 用來與迴歸公式的 CFs 交叉比對。只含 A1-A4 製造運輸，不含地下、非結構與其他階段。
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class RunCalculationCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try
        {
            var doc = data.Application.ActiveUIDocument.Document;
            var r = CalculationService.Run(doc);
            var q = CalculationService.LastQuantities!;

            var concrete = q.Where(x => x.IsConcrete && x.Unit == "m3").Sum(x => x.Quantity);
            var steel = q.Where(x => x.Unit == "kg").Sum(x => x.Quantity);
            var rebar = r.Lines.Where(l => l.MaterialName == RebarEstimator.RebarMaterialName).Sum(l => l.Quantity);

            var sb = new StringBuilder();
            sb.AppendLine($"手冊版本：{r.ManualVersion}");
            sb.AppendLine($"擷取元件數：{q.Select(x => x.ElementId).Distinct().Count()}");
            sb.AppendLine();
            sb.AppendLine($"混凝土體積：{concrete:N2} m³");
            sb.AppendLine($"鋼構重量：{steel:N0} kg");
            sb.AppendLine($"估算鋼筋：{rebar:N0} kg");
            sb.AppendLine();
            sb.AppendLine($"蘊含碳排合計：{r.Total:N0} kgCO2e");
            foreach (var (cat, v) in r.ByCategory.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"  · {cat}：{v:N0} kgCO2e");
            sb.AppendLine();
            sb.AppendLine($"未對應／需檢查項目：{r.Unmapped.Count} 筆（請按「模型檢查」查看）");

            if (r.Lines.Count > 0 && r.Lines.All(l => l.Ef == 0))
                sb.AppendLine("\n⚠ 係數庫的 ef 目前都是 0（佔位值），碳排為 0 是正常的。請先依手冊填入 factors.LEBR-2025.json。");

            new TaskDialog("LEBR 數量檢核")
            {
                MainInstruction = $"主結構材料碳排 {r.Total:N0} kgCO2e",
                MainContent = sb.ToString(),
                FooterText = "材料數量法（A1-A4），與「LEBR 評估」的主結構 CFs 比對用",
            }.Show();

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("LEBR 試算失敗", ex.ToString());
            return Result.Failed;
        }
    }
}
