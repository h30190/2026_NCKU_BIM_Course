using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LebrCarbon.Revit.UI;

namespace LebrCarbon.Revit.Commands;

/// <summary>LEBR 評估：依手冊四階段算法計算減碳率與等級（主功能）。</summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class LebrEvaluateCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try
        {
            var uiapp = data.Application;
            var doc = uiapp.ActiveUIDocument?.Document;
            if (doc is null || doc.IsFamilyDocument)
            {
                TaskDialog.Show("LEBR", "請先開啟專案模型（非族群檔）。");
                return Result.Cancelled;
            }

            var win = new LebrWindow(doc);
            new WindowInteropHelper(win) { Owner = uiapp.MainWindowHandle };
            win.ShowDialog();
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("LEBR 評估失敗", ex.ToString());
            return Result.Failed;
        }
    }
}
