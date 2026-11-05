using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TemporalBim
{
    /// <summary>
    /// Rehydrates empty canonical fields from the complete Revit parameter snapshot.
    /// Existing canonical values are never replaced; model declarations still require review.
    /// </summary>
    public static class PassportAutoFill
    {
        public static void Apply(PassportInfo passport)
        {
            if (passport == null || passport.RawParameters == null || passport.RawParameters.Count == 0) return;
            if (passport.FieldSources == null) passport.FieldSources = new Dictionary<string, FieldSourceInfo>();

            passport.WidthMm = FillLength(passport, "width", passport.WidthMm,
                new[] { "DOOR_WIDTH", "FAMILY_WIDTH_PARAM", "GENERIC_WIDTH" },
                new[] { "GP_門寬_mm", "Width", "寬度", "門寬", "COBie.Type.NominalWidth" }, "nominal");
            passport.HeightMm = FillLength(passport, "height", passport.HeightMm,
                new[] { "DOOR_HEIGHT", "FAMILY_HEIGHT_PARAM", "GENERIC_HEIGHT" },
                new[] { "GP_門高_mm", "Height", "高度", "門高", "COBie.Type.NominalHeight" }, "nominal");
            passport.LeafThicknessMm = FillLength(passport, "leafThickness", passport.LeafThicknessMm,
                new[] { "DOOR_THICKNESS", "WINDOW_THICKNESS" },
                new[] { "GP_門扇厚度_mm", "Leaf Thickness", "Door Leaf Thickness", "Door Thickness", "Thickness", "門扇厚度", "門扇厚", "扇厚", "門厚", "厚度" }, "leaf_thickness");
            passport.ClearWidthMm = FillLength(passport, "clearWidth", passport.ClearWidthMm, Array.Empty<string>(),
                new[] { "GP_有效淨寬_mm", "Clear Width", "Clear Opening Width", "Effective Clear Width", "Net Width", "通行淨寬", "有效淨寬", "COBie.Type.AccessibilityPerformance" }, "clear");

            passport.OperationType = FillText(passport, "operationType", passport.OperationType,
                new[] { "GP_開啟方式", "Operation Type", "Door Operation", "開啟方式" });
            passport.SwingHanding = FillText(passport, "swingHanding", passport.SwingHanding,
                new[] { "GP_開門方向", "Swing Handing", "Handing", "開門方向", "左右開" });
            passport.UseType = FillText(passport, "useType", passport.UseType,
                new[] { "GP_室內外用途", "Function", "Use Type", "Interior Exterior", "室內外用途", "功能" }, CanonicalUse);
            passport.Material = FillText(passport, "material", passport.Material,
                new[] { "GP_門扇材料", "Door Material", "Panel Material", "Material", "材質", "門扇材料", "COBie.Type.Material" });


            passport.FrameDimensions = FillText(passport, "frameDimensions", passport.FrameDimensions,
                new[] { "GP_門框及安裝尺寸", "Frame Dimensions", "Frame Depth", "門框尺寸", "門框及安裝尺寸" });
            passport.Condition = FillText(passport, "condition", passport.Condition,
                new[] { "GP_保存狀況", "Condition", "保存狀況" });
            passport.ConditionGrade = FillText(passport, "conditionGrade", passport.ConditionGrade,
                new[] { "GP_檢核等級", "Condition Grade", "檢核等級" });
            passport.PlacementRequirements = FillText(passport, "placementRequirements", passport.PlacementRequirements,
                new[] { "GP_安裝位置", "Installation Location", "安裝位置" });
            passport.InspectedBy = FillText(passport, "inspectedBy", passport.InspectedBy,
                new[] { "GP_檢核人員", "Inspector", "檢核人員" });
            passport.InspectedOn = FillText(passport, "inspectedOn", passport.InspectedOn,
                new[] { "GP_檢核日期", "Inspection Date", "檢核日期" });

            if (passport.PriceNtd <= 0)
            {
                var price = Pick(passport.RawParameters, Array.Empty<string>(), new[] { "GP_估價_NTD", "Replacement Cost", "估價", "COBie.Type.ReplacementCost" });
                if (price != null && TryNumber(Text(price), out double value) && value >= 0)
                {
                    passport.PriceNtd = value;
                    SetSource(passport, "priceNtd", price, "model_declared");
                }
            }
        }

        private static double FillLength(PassportInfo passport, string key, double target, string[] builtins, string[] names, string definition)
        {
            if (target > 0) return target;
            var parameter = Pick(passport.RawParameters, builtins, names, true);
            double? value = parameter == null ? null : LengthMm(parameter);
            if (value.HasValue && value.Value > 0)
            {
                SetSource(passport, key, parameter, definition);
                return Math.Round(value.Value, 2);
            }
            return target;
        }

        private static string FillText(PassportInfo passport, string key, string target, string[] names, Func<string, string> transform = null)
        {
            if (!string.IsNullOrWhiteSpace(target)) return target;
            var parameter = Pick(passport.RawParameters, Array.Empty<string>(), names);
            if (parameter == null) return target;
            string value = Text(parameter).Trim();
            if (transform != null) value = transform(value);
            if (string.IsNullOrWhiteSpace(value)) return target;
            SetSource(passport, key, parameter, "model_declared");
            return value;
        }

        private static RawParameterInfo Pick(IEnumerable<RawParameterInfo> parameters, string[] builtins, string[] names, bool requireLength = false)
        {
            var builtinSet = new HashSet<string>(builtins ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var normalizedNames = new HashSet<string>((names ?? Array.Empty<string>()).Select(Normalize));
            return parameters
                .Where(p => p != null && (builtinSet.Contains(p.Builtin ?? "") || normalizedNames.Contains(Normalize(p.Name))))
                .Where(p => requireLength ? LengthMm(p).HasValue : !string.IsNullOrWhiteSpace(Text(p)))
                .OrderByDescending(p => Priority(p, builtinSet))
                .ThenBy(p => p.Name)
                .FirstOrDefault();
        }

        private static int Priority(RawParameterInfo parameter, HashSet<string> builtins)
        {
            int value = 200;
            if ((parameter.Name ?? "").StartsWith("GP_", StringComparison.OrdinalIgnoreCase)) value = 400;
            else if (builtins.Contains(parameter.Builtin ?? "")) value = 300;
            else if ((parameter.Name ?? "").StartsWith("COBie.", StringComparison.OrdinalIgnoreCase)) value = 100;
            if (string.Equals(parameter.Scope, "Instance", StringComparison.OrdinalIgnoreCase)) value += 10;
            return value;
        }

        private static double? LengthMm(RawParameterInfo parameter)
        {
            if (parameter.NumericValueMm.HasValue) return parameter.NumericValueMm.Value;
            string text = Text(parameter).Trim();
            var explicitUnit = Regex.Match(text, @"(-?\d+(?:\.\d+)?)\s*(mm|毫米|公釐|cm|公分|m|公尺)\b", RegexOptions.IgnoreCase);
            if (explicitUnit.Success && TryNumber(explicitUnit.Groups[1].Value, out double value))
            {
                string unit = explicitUnit.Groups[2].Value.ToLowerInvariant();
                if (unit == "cm" || unit == "公分") return value * 10;
                if (unit == "m" || unit == "公尺") return value * 1000;
                return value;
            }
            string name = parameter.Name ?? "";
            if (name.IndexOf("_mm", StringComparison.OrdinalIgnoreCase) >= 0 || name.Contains("（mm）") || name.Contains("(mm)") || name.StartsWith("COBie.Type.Nominal", StringComparison.OrdinalIgnoreCase))
                return TryNumber(Text(parameter), out value) ? value : (double?)null;
            return null;
        }

        private static bool TryNumber(string value, out double number) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
            double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number);

        private static string Text(RawParameterInfo p) => string.IsNullOrWhiteSpace(p.DisplayValue) ? p.RawValue ?? "" : p.DisplayValue;
        private static string Normalize(string value) => Regex.Replace((value ?? "").Trim().ToLowerInvariant(), @"[\s_\-:：/\\()（）\[\]]", "");
        private static string CanonicalUse(string value)
        {
            if (Regex.IsMatch(value ?? "", "室外|外部|Exterior", RegexOptions.IgnoreCase)) return "Exterior";
            if (Regex.IsMatch(value ?? "", "室內|Interior", RegexOptions.IgnoreCase)) return "Interior";
            return value ?? "";
        }

        private static void SetSource(PassportInfo passport, string key, RawParameterInfo parameter, string definition)
        {
            passport.FieldSources[key] = new FieldSourceInfo
            {
                Kind = (parameter.Name ?? "").StartsWith("COBie.", StringComparison.OrdinalIgnoreCase) ? "cobie" : "bim",
                Scope = parameter.Scope,
                ParameterName = parameter.Name,
                ParameterId = parameter.ParameterId,
                Unit = parameter.Unit,
                Definition = definition,
                Verification = "由已保存的 Revit 原始參數自動回填；仍須人工檢核",
            };
        }
    }
}
