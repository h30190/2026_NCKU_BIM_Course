using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ShaftChecker.Revit.Writers;

namespace ShaftChecker.Revit.Commands
{
    /// <summary>還原「一鍵檢討」在目前視圖上色的元件。</summary>
    [Transaction(TransactionMode.Manual)]
    public class ClearColorsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            Document doc = data.Application.ActiveUIDocument.Document;
            View view = doc.ActiveView;

            if (!GraphicsWriter.CanColor(view))
            {
                TaskDialog.Show("清除上色", "目前視圖不支援圖形覆寫。");
                return Result.Succeeded;
            }

            int count;
            using (var t = new Transaction(doc, "管道間檢討：清除上色"))
            {
                t.Start();
                count = GraphicsWriter.Clear(view);
                t.Commit();
            }

            TaskDialog.Show("清除上色", count > 0
                ? "已還原 " + count + " 個元件。"
                : "目前視圖沒有本次工作階段上色的元件（重新開啟 Revit 後紀錄會清空）。");
            return Result.Succeeded;
        }
    }
}
