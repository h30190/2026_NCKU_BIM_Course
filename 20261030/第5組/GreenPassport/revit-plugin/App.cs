using System.Reflection;
using Autodesk.Revit.UI;

namespace TemporalBim
{
    /// <summary>
    /// 外掛進入點：Revit 啟動時建一個 Ribbon 面板。
    /// 目前掛的是 Green Passport 循環建材的三個指令（①②③）；
    /// Temporal BIM 那組指令（CreateSnapshotCommand/OpenHistoryPanelCommand/HistoryForm）
    /// 程式碼還在專案裡，只是沒有註冊到 ribbon——題目換了但模式可以照抄，需要時再接回來。
    /// 傳統 .addin 部署：TemporalBim.addin + TemporalBim.dll 一起丟到
    /// %APPDATA%\Autodesk\Revit\Addins\2024\，重開 Revit 即生效（跟 RFI sample 同模式）。
    /// </summary>
    public class App : IExternalApplication
    {
        /// <summary>
        /// LocateHandler/LocateEvent 要在這裡（Revit API 合法的執行時機）建立一次。
        /// Green Passport 目前三個指令都是模態視窗（ShowDialog），本身不需要 ExternalEvent，
        /// 但留著給以後要做非模態面板時用，跟 LocateRequestHandler.cs 的說明一致。
        /// </summary>
        public static LocateRequestHandler LocateHandler;
        public static ExternalEvent LocateEvent;

        public Result OnStartup(UIControlledApplication app)
        {
            LocateHandler = new LocateRequestHandler();
            LocateEvent = ExternalEvent.Create(LocateHandler);

            const string tabName = "Green Passport";
            try { app.CreateRibbonTab(tabName); } catch { /* 已存在就跳過 */ }

            var panel = app.CreateRibbonPanel(tabName, "循環建材護照");
            var asm = Assembly.GetExecutingAssembly().Location;

            var createBtn = new PushButtonData(
                "CreatePassport", "A｜擷取與\n建立護照",
                asm, typeof(CreatePassportCommand).FullName);
            createBtn.ToolTip = "在 A 模型擷取與建立待審核護照；已建檔項目顯示保存的資料。個別／批次審核請使用『護照清單與審核管理』。";
            panel.AddItem(createBtn);

            var matchBtn = new PushButtonData(
                "DemandMatch", "B｜確認需求\n配對與替換",
                asm, typeof(DemandMatchCommand).FullName);
            matchBtn.ToolTip = "在 B 模型選一扇門：由設計者確認八項需求，取得 Matched／Pending Review／Not Matched，預留後執行 B-1 替換。";
            panel.AddItem(matchBtn);

            var refreshBtn = new PushButtonData(
                "RefreshAllocatedDoor", "更新已配置門\n族群與材質",
                asm, typeof(RefreshAllocatedDoorCommand).FullName);
            refreshBtn.ToolTip = "選取已配置的門，套用 A 模型的真正來源族群與護照指定材質；需同時開啟來源 A 模型，不會再次占用護照。";
            panel.AddItem(refreshBtn);

            // ③確認建模／執行替換已經併入②的「執行替換」按鈕，不用再重新選門、重新挑 Reserved 護照，
            // 所以這裡不再註冊成 ribbon 按鈕。ConfirmBuildCommand.cs 程式碼還留著參考。

            var listBtn = new PushButtonData(
                "PassportList", "護照清單與\n審核管理",
                asm, typeof(PassportListCommand).FullName);
            listBtn.ToolTip = "統一進行個別審核、全部通過審核及更新資格。通過後以綠色『已通過審核』顯示。";
            panel.AddItem(listBtn);

            var dashboardBtn = new PushButtonData(
                "OpenDashboard", "開啟唯讀\n護照圖台",
                asm, typeof(OpenDashboardCommand).FullName);
            dashboardBtn.ToolTip = "開啟 http://127.0.0.1:8080/dashboard。網站只讀，不提供審核、配對、預留或模型操作。";
            panel.AddItem(dashboardBtn);

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
    }
}
