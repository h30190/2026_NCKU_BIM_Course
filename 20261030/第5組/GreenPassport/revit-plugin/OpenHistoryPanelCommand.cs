using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TemporalBim
{
    /// <summary>
    /// 「歷史追溯面板」指令：開啟 HistoryForm。
    /// 規則：用 Form.Show()（非模態），不要用 ShowDialog()——
    /// 這樣視窗才能跟 Revit 主視窗並排，點擊清單定位時才看得到畫面反應。
    /// 用一個 static 參照擋掉重複開窗，也避免視窗被 GC 回收。
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class OpenHistoryPanelCommand : IExternalCommand
    {
        private static HistoryForm _openForm;

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiDoc = data.Application.ActiveUIDocument;
            if (uiDoc == null) { message = "請先開啟一個專案文件"; return Result.Failed; }

            if (_openForm != null && !_openForm.IsDisposed)
            {
                _openForm.Activate();
                return Result.Succeeded;
            }

            _openForm = new HistoryForm(uiDoc);
            _openForm.FormClosed += (s, e) => { _openForm = null; };
            _openForm.Show();
            return Result.Succeeded;
        }
    }
}
