using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TemporalBim
{
    /// <summary>
    /// 歷史追溯面板是「彈出視窗（modeless，Form.Show()）」，不是跳出來擋住 Revit 的 ShowDialog()。
    /// 這代表面板按鈕的 Click 事件，是在 Execute() 已經結束之後才觸發的——
    /// 這時候直接呼叫 Revit API（GetElement / Selection / ShowElements）是不被允許的，
    /// 一定要透過 ExternalEvent 把呼叫重新排進 Revit 自己的 API 執行時機裡。
    /// 這個 handler 就是做這件事：面板把要查的 UniqueId 放進 TargetUniqueId，
    /// 呼叫 ExternalEvent.Raise()，Revit 準備好的時候就會跑這裡的 Execute()。
    /// </summary>
    public class LocateRequestHandler : IExternalEventHandler
    {
        public string TargetUniqueId;

        /// <summary>
        /// 定位完成後回呼給面板，參數是「這個 UniqueId 現在還存在嗎」。
        /// 故意用普通 delegate 欄位、不是 C# 的 event——
        /// 因為面板每次點擊都要「整個換掉」上一次掛的回呼（見 HistoryForm 裡的
        /// Completed = null 那行），event 不允許從外部這樣直接賦值，只能 += / -=。
        /// </summary>
        public Action<bool> Completed;

        public void Execute(UIApplication app)
        {
            bool found = false;
            try
            {
                var uiDoc = app.ActiveUIDocument;
                if (uiDoc != null && !string.IsNullOrEmpty(TargetUniqueId))
                {
                    var doc = uiDoc.Document;
                    Element el = null;
                    try { el = doc.GetElement(TargetUniqueId); } catch { /* 找不到就當作不存在 */ }

                    if (el != null)
                    {
                        found = true;
                        var id = el.Id;
                        uiDoc.Selection.SetElementIds(new List<ElementId> { id });
                        uiDoc.ShowElements(id);
                    }
                }
            }
            catch
            {
                found = false;
            }
            Completed?.Invoke(found);
        }

        public string GetName() => "TemporalBim 元件定位";
    }
}
