using System;
using System.Reflection;
using Autodesk.Revit.UI;
using ShaftChecker.Revit.Commands;

namespace ShaftChecker.Revit
{
    /// <summary>Revit 啟動時建立「管道間檢討」頁籤與按鈕。</summary>
    public class App : IExternalApplication
    {
        public const string TabName = "管道間檢討";

        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                app.CreateRibbonTab(TabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // 頁籤已存在（例如其他組員的外掛也用同名頁籤）
            }

            RibbonPanel panel = app.CreateRibbonPanel(TabName, "模組一 淨空間");
            string dll = Assembly.GetExecutingAssembly().Location;

            Add(panel, dll, typeof(SetupParametersCommand), "SC_Setup", "設定\n參數",
                "建立並綁定檢討用的共用參數（PS_淨寬、PS_檢討結果、遮煙性能…）。每個專案執行一次即可。");
            Add(panel, dll, typeof(ListShaftsCommand), "SC_List", "管道間\n清單",
                "列出模型中辨識到的管道間、淨尺寸、邊界牆防火時效與維修門，用來確認資料是否齊全。");
            Add(panel, dll, typeof(RunCheckCommand), "SC_Run", "一鍵\n檢討",
                "執行 A1、A2、A4、A6、A7、A8 檢討：寫回參數、目前視圖上色、匯出 CSV 報表。");
            Add(panel, dll, typeof(ClearColorsCommand), "SC_Clear", "清除\n上色",
                "還原本次檢討在目前視圖中上色的元件。");
            Add(panel, dll, typeof(BuildTestModelCommand), "SC_TestModel", "建立\n測試模型",
                "在目前樓層建立 T1–T6 測試案例（合格、牆防火不足、門無遮煙、門開向樓梯間、淨寬不足、門前有柱）。");

            RibbonPanel aiPanel = app.CreateRibbonPanel(TabName, "AI 自然語言檢討");
            Add(aiPanel, dll, typeof(OpenDashboardCommand), "SC_Dashboard", "檢討\n儀表板",
                "在瀏覽器開啟檢討儀表板：總覽、逐一查看違規、定位到 Revit、假設分析門檻。");
            Add(aiPanel, dll, typeof(BridgeStatusCommand), "SC_AI", "AI\n連線狀態",
                "顯示 Claude（MCP）連線入口是否啟動。Revit 開啟時會自動啟動。");

            // 啟動本機 HTTP 入口，讓 MCP 伺服器（Claude）可以呼叫檢討功能
            try
            {
                Bridge.HttpBridge.Start(Utils.AddinPaths.LoadConfig().BridgePort);
            }
            catch (Exception)
            {
                // 啟動失敗不影響功能區按鈕；可從「AI 連線狀態」查看原因
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication app)
        {
            Bridge.HttpBridge.Stop();
            return Result.Succeeded;
        }

        private static void Add(RibbonPanel panel, string dll, Type command, string name, string text, string tip)
        {
            var data = new PushButtonData(name, text, dll, command.FullName) { ToolTip = tip };
            panel.AddItem(data);
        }
    }
}
