using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TemporalBim
{
    public sealed class DemandSuggestion
    {
        public string Field { get; set; }
        public string Label { get; set; }
        public string Value { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>B-only mapping. Captured declarations and name-based suggestions stay distinguishable.</summary>
    public sealed class DemandDataMapper
    {
        public List<DemandSuggestion> Suggestions { get; } = new List<DemandSuggestion>();
        public List<string> Notes { get; } = new List<string>();

        public DemandDataMapper(PassportInfo model, string hostFunction = "")
        {
            if (model.RawParameters == null) model.RawParameters = new List<RawParameterInfo>();
            if (model.FieldSources == null) model.FieldSources = new Dictionary<string, FieldSourceInfo>();
            if (model.MappingWarnings == null) model.MappingWarnings = new List<string>();
            model.WidthMm = Length(model, "width", model.WidthMm,
                new[] { "DOOR_WIDTH", "FAMILY_WIDTH_PARAM", "GENERIC_WIDTH" },
                new[] { "GP_門寬_mm", "Width", "門寬", "寬度", "COBie.Type.NominalWidth" });
            model.HeightMm = Length(model, "height", model.HeightMm,
                new[] { "DOOR_HEIGHT", "FAMILY_HEIGHT_PARAM", "GENERIC_HEIGHT" },
                new[] { "GP_門高_mm", "Height", "門高", "高度", "COBie.Type.NominalHeight" });
            model.LeafThicknessMm = Length(model, "leafThickness", model.LeafThicknessMm,
                new[] { "DOOR_THICKNESS", "WINDOW_THICKNESS" },
                new[] { "GP_門扇厚度_mm", "Leaf Thickness", "Door Thickness", "Thickness", "厚度", "門扇厚度", "門扇厚", "COBie.Type.NominalLength" });
            model.ClearWidthMm = Length(model, "clearWidth", model.ClearWidthMm, Array.Empty<string>(),
                new[] { "GP_有效淨寬_mm", "Clear Width", "Clear Opening Width", "Effective Clear Width", "Net Width", "有效通行淨寬", "有效淨寬", "通行淨寬", "淨寬", "COBie.Type.AccessibilityPerformance" });
            model.OperationType = Text(model, "operationType", model.OperationType,
                new[] { "GP_開啟方式", "Operation Type", "Operation_Type", "Door Operation", "開啟方式" });
            model.SwingHanding = Text(model, "swingHanding", model.SwingHanding,
                new[] { "GP_開門方向", "Swing Handing", "Swing_Handing", "Handing", "開門方向", "左右開" });
            model.UseType = CanonicalUse(Text(model, "useType", model.UseType,
                new[] { "GP_室內外用途", "Use Type", "Use_Type", "Interior Exterior", "室內外用途", "用途", "Function", "功能" }));
            model.Material = Text(model, "material", model.Material,
                new[] { "GP_門扇材料", "Door Material", "Panel Material", "Material", "材質", "門材料", "門扇材料", "COBie.Type.Material" });
            string description = model.TypeName + " " + string.Join(" ", model.RawParameters
                .Where(p => p.Builtin == "ALL_MODEL_TYPE_COMMENTS" || p.Builtin == "ALL_MODEL_DESCRIPTION")
                .Select(Value));
            bool doubleDoor = Regex.IsMatch(description, "雙扇平開|雙扇|雙開|double.*(swing|flush|door)", RegexOptions.IgnoreCase);
            bool sliding = Regex.IsMatch(description, "橫拉|推拉|滑門|sliding", RegexOptions.IgnoreCase);
            if (string.IsNullOrWhiteSpace(model.OperationType))
            {
                string operation = sliding ? "橫拉" : doubleDoor ? "雙扇平開" : Regex.IsMatch(model.Family ?? "", "單個嵌平|single.*(flush|swing)", RegexOptions.IgnoreCase) ? "單扇平開" : "";
                Suggest("operationType", "開啟方式", operation, "依類型名稱／族群名稱推得，請確認實際開啟方式。");
                if (doubleDoor && Regex.IsMatch(model.Family ?? "", "單個|single", RegexOptions.IgnoreCase))
                    Notes.Add("類型名稱為雙開，但族群名稱為單扇；名稱與族群可能不一致，請核對模型幾何。");
            }
            if (doubleDoor && model.LeafCount != 2) Suggest("leafCount", "門扇數", "2", "類型名稱含雙開／雙扇，請核對實際門扇數。");
            bool exteriorName = Regex.IsMatch(description, "外部|對外|室外|Exterior", RegexOptions.IgnoreCase);
            if (exteriorName && model.UseType != "Exterior")
            {
                Suggest("useType", "室內外用途", "Exterior", "類型名稱為對外門，與目前功能欄位不一致，請設計者確認。");
                Notes.Add("類型名稱為對外門，目前功能欄位為「" + model.UseType + "」。");
            }
            if (!string.IsNullOrEmpty(hostFunction) && !string.IsNullOrEmpty(model.UseType) && hostFunction != model.UseType)
                Notes.Add("宿主牆功能為「" + hostFunction + "」，門功能為「" + model.UseType + "」，請確認用途。");
            if (string.IsNullOrWhiteSpace(model.SwingHanding)) Notes.Add("模型未明確記錄開門方向；翻轉旗標無法單獨確定左／右鉸及內／外開，請依平面圖選填。");
            if (model.ClearWidthMm <= 0) Notes.Add("模型未提供有效淨寬；請輸入設計要求的最低淨寬（非名義門寬）。");
            if (Regex.IsMatch(model.Material ?? "", @"^門\s*[-－]\s*(嵌板|面板|框架)$"))
                Notes.Add("「" + model.Material + "」是通用材料名稱，尚未表明木材／鋁／鋼等材質；請確認配對要求。");
            foreach (var suggestion in Suggestions)
                Notes.Add(suggestion.Label + "建議「" + suggestion.Value + "」（尚未套用）：" + suggestion.Reason);
            foreach (var note in Notes)
                if (!model.MappingWarnings.Contains(note)) model.MappingWarnings.Add(note);
        }

        private void Suggest(string field, string label, string value, string reason)
        {
            if (!string.IsNullOrEmpty(value)) Suggestions.Add(new DemandSuggestion { Field = field, Label = label, Value = value, Reason = reason });
        }

        private string Text(PassportInfo model, string field, string fallback, string[] aliases, string[] builtins = null)
        {
            var p = Pick(model, field, aliases, builtins ?? Array.Empty<string>());
            if (p == null) return fallback ?? "";
            Source(model, field, p);
            return Value(p).Trim();
        }

        private double Length(PassportInfo model, string field, double fallback, string[] builtins, string[] aliases)
        {
            var p = Pick(model, field, aliases, builtins, true);
            if (p == null) return fallback;
            Source(model, field, p);
            return Math.Round(Millimeters(p).Value, 2);
        }

        private RawParameterInfo Pick(PassportInfo model, string field, string[] aliases, string[] builtins, bool length = false)
        {
            string[] demandAliases = DemandAliases(field);
            var names = new HashSet<string>(aliases.Concat(demandAliases).Select(Normalize));
            var explicitNames = new HashSet<string>(demandAliases.Select(Normalize));
            var matches = model.RawParameters.Where(p => names.Contains(Normalize(p.Name)) || builtins.Contains(p.Builtin ?? ""))
                .Where(p => length ? Millimeters(p).HasValue && Millimeters(p).Value > 0 : !string.IsNullOrWhiteSpace(Value(p)))
                .Select(p => new { Parameter = p, Rank = (explicitNames.Contains(Normalize(p.Name)) ? 500 : (p.Name ?? "").StartsWith("GP_") ? 400 : builtins.Contains(p.Builtin ?? "") ? 300 : (p.Name ?? "").StartsWith("COBie.") ? 100 : 200) + (p.Scope == "Instance" ? 10 : 0) })
                .OrderByDescending(x => x.Rank).ToList();
            if (matches.Count == 0) return null;
            var top = matches.Where(x => x.Rank == matches[0].Rank).Select(x => x.Parameter).ToList();
            bool conflict = length ? top.Any(p => Math.Abs(Millimeters(p).Value - Millimeters(top[0]).Value) > 0.1) : top.Any(p => !string.Equals(Value(p).Trim(), Value(top[0]).Trim(), StringComparison.OrdinalIgnoreCase));
            if (conflict)
            {
                Notes.Add(field + "：相同優先序的參數互相衝突，請查看完整參數並確認。");
                return null;
            }
            return top[0];
        }

        private static string[] DemandAliases(string field)
        {
            var fields = new Dictionary<string, string[]>
            {
                ["width"] = new[] { "門寬_mm", "門寬", "Width" }, ["height"] = new[] { "門高_mm", "門高", "Height" },
                ["leafThickness"] = new[] { "門扇厚度_mm", "門扇厚度", "Leaf Thickness" },
                ["clearWidth"] = new[] { "最低淨寬_mm", "最低淨寬", "有效淨寬_mm", "Clear Width" },
                ["operationType"] = new[] { "開啟方式", "Operation Type" }, ["swingHanding"] = new[] { "開門方向", "Swing Handing" },
                ["useType"] = new[] { "室內外用途", "Use Type" }, ["material"] = new[] { "材質", "門扇材料", "Material" }
            };
            return fields.TryGetValue(field, out var values)
                ? values.SelectMany(v => new[] { "B_" + v, "需求_" + v, "GP_B_" + v, "Required " + v }).ToArray()
                : Array.Empty<string>();
        }

        private static double? Millimeters(RawParameterInfo p)
        {
            if (p.NumericValueMm.HasValue) return p.NumericValueMm;
            string text = Value(p).Trim();
            var match = Regex.Match(text, @"(-?\d+(?:\.\d+)?)\s*(mm|毫米|公釐|cm|公分|m|公尺)(?![A-Za-z])", RegexOptions.IgnoreCase);
            if (match.Success && TryNumber(match.Groups[1].Value, out double v))
            {
                string unit = match.Groups[2].Value.ToLowerInvariant();
                return v * (unit == "cm" || unit == "公分" ? 10 : unit == "m" || unit == "公尺" ? 1000 : 1);
            }
            // Numeric storage without converted millimeters may still be Revit internal feet.
            if (p.Storage == "Double" && !string.Equals(p.Unit, "mm", StringComparison.OrdinalIgnoreCase)) return null;
            if (Regex.IsMatch(p.Name ?? "", "_mm|[（(]mm[）)]", RegexOptions.IgnoreCase) || string.Equals(p.Unit, "mm", StringComparison.OrdinalIgnoreCase))
                return TryNumber(text, out double value) ? value : (double?)null;
            return null;
        }

        private static bool TryNumber(string value, out double number) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        private static string Value(RawParameterInfo p) => string.IsNullOrWhiteSpace(p.DisplayValue) ? p.RawValue ?? "" : p.DisplayValue;
        private static string Normalize(string value) => Regex.Replace((value ?? "").ToLowerInvariant(), @"[\s_\-:：/\\()（）]", "");
        private static string CanonicalUse(string value) => Regex.IsMatch(value ?? "", "外部|對外|室外|Exterior", RegexOptions.IgnoreCase) ? "Exterior" : Regex.IsMatch(value ?? "", "室內|Interior", RegexOptions.IgnoreCase) ? "Interior" : value ?? "";
        private static void Source(PassportInfo model, string field, RawParameterInfo p)
        {
            model.FieldSources[field] = new FieldSourceInfo { Kind = (p.Name ?? "").StartsWith("COBie.") ? "cobie" : "bim", Scope = p.Scope, ParameterName = p.Name, ParameterId = p.ParameterId, Unit = p.Unit, Definition = "B 模型需求帶入", Verification = "模型宣告值，請設計者確認配對需求" };
        }
    }
}
