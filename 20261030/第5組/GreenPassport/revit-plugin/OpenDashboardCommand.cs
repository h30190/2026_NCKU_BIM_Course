using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TemporalBim
{
    /// <summary>
    /// 「開啟網頁版資料庫」：直接用系統預設瀏覽器打開 http://127.0.0.1:8080/dashboard。
    /// 跟③（Revit 內的清單視窗）是兩個不同的東西——這顆按鈕才是真的會跳出網頁的那個。
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class OpenDashboardCommand : IExternalCommand
    {
        private const string DashboardUrl = "http://127.0.0.1:8080/dashboard";

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = DashboardUrl, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Green Passport",
                    $"沒辦法自動開啟瀏覽器：{ex.Message}\n\n請手動在瀏覽器打開：{DashboardUrl}\n（後端要先用 go run . 跑起來）");
                return Result.Failed;
            }
            return Result.Succeeded;
        }
    }
}
