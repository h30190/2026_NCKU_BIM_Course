using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ShaftChecker.Revit.Services;

namespace ShaftChecker.Revit.Commands
{
    /// <summary>在目前樓層建立 T1–T6 測試案例（重新執行會先刪除上一次建立的測試元件）。</summary>
    [Transaction(TransactionMode.Manual)]
    public class BuildTestModelCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var confirm = new TaskDialog("建立測試模型")
            {
                MainInstruction = "在目前樓層建立 T1–T6 測試案例？",
                MainContent = "會建立走廊、六個管道間、樓梯間、柱與維修門（原點附近）。\n" +
                              "上一次建立的測試元件會先刪除；模型中其他元件不受影響。",
                CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel
            };
            if (confirm.Show() != TaskDialogResult.Ok) return Result.Cancelled;

            Dictionary<string, object> result;
            try
            {
                result = TestModelBuilder.Build(data.Application);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }

            var sb = new StringBuilder();
            sb.AppendLine("樓層：" + result["level"]);
            foreach (object o in (List<object>)result["cases"])
            {
                var row = (Dictionary<string, object>)o;
                sb.AppendLine(row["room"] + "　預期：" + row["expect"]);
            }
            foreach (string n in (List<string>)result["notes"]) sb.AppendLine("注意：" + n);
            sb.AppendLine();
            sb.AppendLine("下一步：按「一鍵檢討」或開啟儀表板執行檢討。");

            TaskDialog.Show("建立測試模型", sb.ToString());
            return Result.Succeeded;
        }
    }
}
