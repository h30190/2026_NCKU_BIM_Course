using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using ShaftChecker.Core.Models;
using ShaftChecker.Revit.Utils;

namespace ShaftChecker.Revit.Writers
{
    /// <summary>
    /// 把檢討結果寫回管道間 Room 的共用參數，讓明細表自動更新。
    /// 參數不存在時略過，並回報缺少哪些參數（請先執行「設定參數」）。
    /// 必須在 Transaction 內呼叫。
    /// </summary>
    public static class ParameterWriter
    {
        public const string PClearWidth = "PS_淨寬";
        public const string PClearDepth = "PS_淨深";
        public const string PArea = "PS_淨面積";
        public const string PResult = "PS_檢討結果";
        public const string PIssues = "PS_違規項目";
        public const string PTime = "PS_檢討時間";

        private enum Kind { Length, Area, Text }

        public static List<string> Write(Document doc, CheckResult result)
        {
            var missing = new HashSet<string>();
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            foreach (ShaftResult sr in result.Shafts)
            {
                Element room = doc.GetElement(ElementIdUtil.FromLong(sr.Shaft.RoomId));
                if (room == null) continue;

                string failed = string.Join("、", sr.Issues
                    .Where(i => i.Severity != Severity.Pass)
                    .Select(i => i.RuleId)
                    .Distinct());

                Set(room, PClearWidth, Kind.Length, sr.Shaft.ClearWidthMm, null, missing);
                Set(room, PClearDepth, Kind.Length, sr.Shaft.ClearDepthMm, null, missing);
                Set(room, PArea, Kind.Area, sr.Shaft.AreaM2, null, missing);
                Set(room, PResult, Kind.Text, null, Labels.Of(sr.Overall), missing);
                Set(room, PIssues, Kind.Text, null, failed, missing);
                Set(room, PTime, Kind.Text, null, now, missing);
            }

            return missing.ToList();
        }

        private static void Set(Element e, string name, Kind kind, double? value, string text, HashSet<string> missing)
        {
            Parameter p = e.LookupParameter(name);
            if (p == null)
            {
                missing.Add(name);
                return;
            }
            if (p.IsReadOnly) return;

            switch (p.StorageType)
            {
                case StorageType.Double:
                    if (!value.HasValue) return;
                    p.Set(kind == Kind.Area ? UnitConv.FromM2(value.Value) : UnitConv.FromMm(value.Value));
                    break;

                case StorageType.String:
                    if (text != null) p.Set(text);
                    else if (value.HasValue) p.Set(value.Value.ToString(kind == Kind.Area ? "F2" : "F0"));
                    break;
            }
        }
    }
}
