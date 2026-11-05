using Autodesk.Revit.UI;
using LebrCarbon.Revit.Commands;

namespace LebrCarbon.Revit;

/// <summary>外掛進入點：建立「低碳建築」功能區頁籤（計畫書 6.1 節）。</summary>
public sealed class App : IExternalApplication
{
    public const string TabName = "低碳建築";

    public Result OnStartup(UIControlledApplication app)
    {
        try
        {
            app.CreateRibbonTab(TabName);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // 頁籤已存在（例如重複載入），沿用即可
        }

        var asm = typeof(App).Assembly.Location;

        var prep = app.CreateRibbonPanel(TabName, "準備");
        AddButton<BindParametersCommand>(prep, asm, "綁定\n參數", "把 LEBR_ 共用參數加入專案（含非主結構用的 LEBR_BlccId）");

        var calc = app.CreateRibbonPanel(TabName, "試算");
        AddButton<LebrEvaluateCommand>(calc, asm, "LEBR\n評估", "依手冊四階段算法計算蘊含碳排、減碳率與等級");
        AddButton<ModelCheckCommand>(calc, asm, "模型\n檢查", "列出缺材質、無法對應係數的主結構元件");
        AddButton<RunCalculationCommand>(calc, asm, "數量\n檢核", "以材料數量 × 附錄一係數估算主結構碳排，與迴歸公式交叉比對");

        var vis = app.CreateRibbonPanel(TabName, "視覺化");
        AddButton<HotspotColorCommand>(vis, asm, "熱點\n著色", "在目前 3D 視圖依構件碳排量著色（綠 → 紅，5 級）");
        AddButton<ClearColorCommand>(vis, asm, "清除\n著色", "移除熱點著色加上的顏色");

        var output = app.CreateRibbonPanel(TabName, "輸出");
        AddButton<ExportReportCommand>(output, asm, "匯出\n報表", "Excel 明細與 PDF 摘要");
        AddButton<OpenConsoleCommand>(output, asm, "網頁\n控制台", "開啟網頁控制台；沒在運行時自動執行 console\\start.ps1 再開瀏覽器");
        AddButton<ApplyProposalCommand>(output, asm, "套用\n建議", "取回網頁控制台選定的減碳建議，確認後寫回類型參數 LEBR_BlccId 並重算");

        var settings = app.CreateRibbonPanel(TabName, "設定");
        AddButton<FactorLibraryCommand>(settings, asm, "係數庫", "檢視、匯入、切換係數版本");

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;

    private static void AddButton<T>(RibbonPanel panel, string asm, string text, string tooltip)
        where T : IExternalCommand
    {
        var data = new PushButtonData(typeof(T).Name, text, asm, typeof(T).FullName) { ToolTip = tooltip };
        panel.AddItem(data);
    }
}
