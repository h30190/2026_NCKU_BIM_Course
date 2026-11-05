using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ShaftChecker.Revit.Bridge;
using ShaftChecker.Revit.Utils;

namespace ShaftChecker.Revit.Commands
{
    /// <summary>用預設瀏覽器開啟檢討儀表板（由外掛的本機 HTTP 入口提供）。</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class OpenDashboardCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (!HttpBridge.IsRunning)
                HttpBridge.Start(AddinPaths.LoadConfig().BridgePort);

            if (!HttpBridge.IsRunning)
            {
                TaskDialog.Show("檢討儀表板", "AI 連線入口未啟動，無法開啟儀表板：\n" + HttpBridge.LastError);
                return Result.Succeeded;
            }

            try
            {
                Process.Start(new ProcessStartInfo(HttpBridge.DashboardUrl) { UseShellExecute = true });
            }
            catch (Exception)
            {
                TaskDialog.Show("檢討儀表板", "請在瀏覽器開啟：" + HttpBridge.DashboardUrl);
            }
            return Result.Succeeded;
        }
    }
}
