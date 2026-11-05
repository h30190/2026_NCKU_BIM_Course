using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LebrCarbon.Revit.Commands;

/// <summary>F1 模型完整性檢查（第一版以對話框呈現，之後移到停駐面板）。</summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class ModelCheckCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try
        {
            var uidoc = data.Application.ActiveUIDocument;
            var r = CalculationService.Run(uidoc.Document);

            if (r.Unmapped.Count == 0)
            {
                TaskDialog.Show("LEBR 模型檢查", "沒有發現問題：所有主結構元件都已對應到係數。");
                return Result.Succeeded;
            }

            var sb = new StringBuilder();
            foreach (var g in r.Unmapped.GroupBy(u => u.Reason))
            {
                sb.AppendLine($"【{g.Key}】{g.Count()} 筆");
                foreach (var u in g.Take(8))
                    sb.AppendLine($"  · {u.Category}／{u.MaterialName}（ID {u.ElementId}）");
                if (g.Count() > 8) sb.AppendLine("  · …");
            }

            var td = new TaskDialog("LEBR 模型檢查")
            {
                MainInstruction = $"共 {r.Unmapped.Count} 筆需要處理",
                MainContent = sb.ToString(),
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "在模型中選取這些元件");

            if (td.Show() == TaskDialogResult.CommandLink1)
            {
                var ids = r.Unmapped
                    .Select(u => long.TryParse(u.ElementId, out var v) ? new ElementId(v) : ElementId.InvalidElementId)
                    .Where(id => id != ElementId.InvalidElementId)
                    .Distinct()
                    .ToList();
                uidoc.Selection.SetElementIds(ids);
                if (ids.Count > 0) uidoc.ShowElements(ids);
            }

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("LEBR 模型檢查失敗", ex.ToString());
            return Result.Failed;
        }
    }
}
