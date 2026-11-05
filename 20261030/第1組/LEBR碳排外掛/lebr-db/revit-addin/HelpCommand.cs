using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LebrCarbon
{
    /// <summary>「使用說明」：列出操作步驟，可開啟 GitHub 上的完整快速開始。不修改模型。</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class HelpCommand : IExternalCommand
    {
        public const string QuickStartUrl =
            "https://github.com/Jac285/2026_NCKU_BIM_Course_Group-1/blob/main/20261030/%E7%AC%AC1%E7%B5%84/LEBR%E7%A2%B3%E6%8E%92%E5%A4%96%E6%8E%9B/%E5%85%88%E7%9C%8B%E9%80%99%E5%80%8B_%E5%BF%AB%E9%80%9F%E9%96%8B%E5%A7%8B.md";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var dialog = new TaskDialog("LEBR 使用說明")
            {
                MainInstruction = "三個步驟算出 LEBR 等級",
                MainContent =
                    "1. 專案設定：填建築類別、構造與結構參數，按「儲存」。第一次使用會自動建立 LEBR 參數。\n" +
                    "   主結構會在「開始計算」時自動用模型的混凝土、鋼筋量估算並記錄；要換材料才需要「開啟估算」。\n\n" +
                    "2. 填構件代號：牆、窗、帷幕嵌板、樓板的「類型性質」填 LEBR_Code（外牆本體另填 LEBR_Code_Body）。\n" +
                    "   別人已經填好的模型，可用「代號 JSON → 套用代號」一次填好。\n\n" +
                    "3. 開始計算：自動估算主結構 → 計算前檢查 → 結果視窗；按「在圖台開啟」用瀏覽器看圖表，或「匯出結果 JSON」。\n\n" +
                    "模型改過要再按一次「開始計算」，算完記得存檔，估算記錄才會留在模型裡。",
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "開啟完整說明（GitHub）", "附截圖的快速開始、常用代號表與常見問題");
            if (dialog.Show() == TaskDialogResult.CommandLink1)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(QuickStartUrl) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("LEBR", "無法開啟瀏覽器：" + ex.Message + "\n\n" + QuickStartUrl);
                }
            }
            return Result.Succeeded;
        }
    }
}
