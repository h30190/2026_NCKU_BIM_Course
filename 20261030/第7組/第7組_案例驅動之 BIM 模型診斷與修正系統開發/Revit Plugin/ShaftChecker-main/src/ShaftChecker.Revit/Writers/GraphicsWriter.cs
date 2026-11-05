using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using ShaftChecker.Core.Models;
using ShaftChecker.Revit.Utils;

namespace ShaftChecker.Revit.Writers
{
    /// <summary>
    /// 在目前視圖中，依結果把相關元件上色：違規紅、資料不足黃、警告橘。
    /// 記錄上過色的元件，「清除上色」時只還原這些元件，不影響使用者原本的覆寫。
    /// 必須在 Transaction 內呼叫。
    /// </summary>
    public static class GraphicsWriter
    {
        // 視圖 Id → 本次工作階段上過色的元件 Id
        private static readonly Dictionary<long, HashSet<long>> Colored = new Dictionary<long, HashSet<long>>();

        public static bool CanColor(View view)
        {
            return view != null && !view.IsTemplate && view.AreGraphicsOverridesAllowed();
        }

        public static int Apply(Document doc, View view, CheckResult result)
        {
            if (!CanColor(view)) return 0;

            // 同一元件可能被多條規則標記，取最嚴重的等級
            var worst = new Dictionary<long, Severity>();
            var roomIds = new HashSet<long>(result.Shafts.Select(s => s.Shaft.RoomId));

            foreach (var issue in result.Shafts.SelectMany(s => s.Issues))
            {
                if (issue.Severity == Severity.Pass) continue;
                foreach (long id in issue.ElementIds)
                {
                    if (roomIds.Contains(id)) continue; // Room 本身在平面上沒有可上色的面
                    Severity current;
                    if (!worst.TryGetValue(id, out current) || issue.Severity > current)
                        worst[id] = issue.Severity;
                }
            }

            ElementId solidFill = FindSolidFill(doc);
            long viewKey = view.Id.GetIdValue();
            HashSet<long> record;
            if (!Colored.TryGetValue(viewKey, out record))
            {
                record = new HashSet<long>();
                Colored[viewKey] = record;
            }

            int count = 0;
            foreach (var kv in worst)
            {
                Color c = ColorOf(kv.Value);
                var ogs = new OverrideGraphicSettings();
                ogs.SetProjectionLineColor(c);
                ogs.SetCutLineColor(c);
                if (solidFill != ElementId.InvalidElementId)
                {
                    ogs.SetSurfaceForegroundPatternId(solidFill);
                    ogs.SetSurfaceForegroundPatternColor(c);
                    ogs.SetCutForegroundPatternId(solidFill);
                    ogs.SetCutForegroundPatternColor(c);
                }

                try
                {
                    view.SetElementOverrides(ElementIdUtil.FromLong(kv.Key), ogs);
                    record.Add(kv.Key);
                    count++;
                }
                catch (Exception)
                {
                    // 元件不在此視圖或不支援覆寫時略過
                }
            }
            return count;
        }

        public static int Clear(View view)
        {
            if (!CanColor(view)) return 0;

            HashSet<long> record;
            if (!Colored.TryGetValue(view.Id.GetIdValue(), out record)) return 0;

            int count = 0;
            foreach (long id in record)
            {
                try
                {
                    view.SetElementOverrides(ElementIdUtil.FromLong(id), new OverrideGraphicSettings());
                    count++;
                }
                catch (Exception)
                {
                    // 元件已刪除時略過
                }
            }
            record.Clear();
            return count;
        }

        private static Color ColorOf(Severity s)
        {
            switch (s)
            {
                case Severity.Violation: return new Color(220, 40, 40);
                case Severity.DataMissing: return new Color(235, 180, 0);
                case Severity.Warning: return new Color(255, 130, 0);
                default: return new Color(0, 160, 80);
            }
        }

        private static ElementId FindSolidFill(Document doc)
        {
            var solid = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
            return solid != null ? solid.Id : ElementId.InvalidElementId;
        }
    }
}
