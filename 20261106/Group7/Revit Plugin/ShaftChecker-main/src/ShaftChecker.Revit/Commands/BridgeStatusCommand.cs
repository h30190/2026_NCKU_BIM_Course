using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ShaftChecker.Revit.Bridge;
using ShaftChecker.Revit.Utils;

namespace ShaftChecker.Revit.Commands
{
    /// <summary>顯示 AI 連線（本機 HTTP 入口）狀態；若未啟動則嘗試重新啟動。</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class BridgeStatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (!HttpBridge.IsRunning)
                HttpBridge.Start(AddinPaths.LoadConfig().BridgePort);

            string url = "http://localhost:" + HttpBridge.Port + "/status";
            TaskDialog.Show("AI 連線狀態", HttpBridge.IsRunning
                ? "AI 連線已啟動。\n\n測試網址：" + url +
                  "\n\n在 Claude 桌面版輸入「檢查全部管道間」即可開始。"
                : "AI 連線未啟動：\n" + HttpBridge.LastError +
                  "\n\n若是埠號被占用，請修改設定檔的 bridgePort 後重新開啟 Revit。");
            return Result.Succeeded;
        }
    }
}
