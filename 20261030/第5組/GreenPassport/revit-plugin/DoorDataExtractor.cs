using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace TemporalBim
{
    /// <summary>
    /// Captures every instance/type parameter and deterministically maps the eight
    /// canonical matching fields. Missing or conflicting values remain unknown.
    /// </summary>
    public static class DoorDataExtractor
    {
        private const double FeetToMm = 304.8;

        public static PassportInfo Extract(Document doc, FamilyInstance door)
        {
            var type = doc.GetElement(door.GetTypeId()) as ElementType;
            var raw = new List<RawParameterInfo>();
            raw.AddRange(CaptureParameters(door, "Instance"));
            if (type != null) raw.AddRange(CaptureParameters(type, "Type"));

            var info = new PassportInfo
            {
                Category = door.Category?.Name ?? "門",
                SourceModel = doc.Title,
                SourceUniqueId = door.UniqueId,
                SourceElementId = door.Id.Value.ToString(CultureInfo.InvariantCulture),
                Family = type is FamilySymbol symbol ? symbol.Family.Name : "",
                TypeName = type?.Name ?? door.Name,
                DimensionDefinition = "nominal",
                RawParameters = raw,
                FieldSources = new Dictionary<string, FieldSourceInfo>(),
                MappingWarnings = new List<string>(),
                ReuseStatus = "Pending",
                Status = "Unavailable",
                Quantity = 1,
                LeafCount = 1,
            };

            info.WidthMm = PickLength(info, "width", "nominal",
                new[] { "DOOR_WIDTH", "FAMILY_WIDTH_PARAM", "GENERIC_WIDTH" },
                new[] { "GP_門寬_mm", "Width", "寬度", "門寬", "COBie.Type.NominalWidth" });
            info.HeightMm = PickLength(info, "height", "nominal",
                new[] { "DOOR_HEIGHT", "FAMILY_HEIGHT_PARAM", "GENERIC_HEIGHT" },
                new[] { "GP_門高_mm", "Height", "高度", "門高", "COBie.Type.NominalHeight" });
            MapLeafThickness(info);
            info.ClearWidthMm = PickLength(info, "clearWidth", "clear",
                Array.Empty<string>(),
                new[] { "GP_有效淨寬_mm", "Clear Width", "Clear Opening Width", "Effective Clear Width", "Net Width", "通行淨寬", "有效淨寬", "COBie.Type.AccessibilityPerformance" });
            info.OperationType = PickText(info, "operationType",
                new[] { "GP_開啟方式", "Operation Type", "Door Operation", "開啟方式", "COBie.Type.Features" });
            info.SwingHanding = PickText(info, "swingHanding",
                new[] { "GP_開門方向", "Swing Handing", "Handing", "開門方向", "左右開", "COBie.Type.Features" });
            info.UseType = CanonicalUse(PickText(info, "useType",
                new[] { "GP_室內外用途", "Function", "Use Type", "Interior Exterior", "室內外用途", "功能" }));
            info.Material = PickText(info, "material",
                new[] { "GP_門扇材料", "Door Material", "Panel Material", "Material", "材質", "門扇材料", "COBie.Type.Material" });
            info.FrameDimensions = PickText(info, "frameDimensions",
                new[] { "GP_門框及安裝尺寸", "Frame Dimensions", "Frame Depth", "門框尺寸", "門框及安裝尺寸" });
            info.Condition = PickText(info, "condition",
                new[] { "GP_保存狀況", "Condition", "保存狀況" });
            info.LeafCount = PickCount(info, "leafCount",
                new[] { "GP_扇數", "Leaf Count", "Number of Leaves", "扇數", "COBie.Type.Features" }, 1);


            string levelName = "";
            try { levelName = (doc.GetElement(door.LevelId) as Level)?.Name ?? ""; } catch { }
            string hostName = door.Host?.Name ?? "";
            info.PlacementRequirements = string.Join("／", new[] { levelName, hostName }.Where(x => !string.IsNullOrWhiteSpace(x)));
            // Save a portable Family/Type thumbnail with the A-model passport so
            // the B-model matcher can show the actual candidate instead of only text.
            info.Photo = DoorPreviewRenderer.CaptureTypePreview(type);
            return info;
        }

        public static void MapLeafThickness(PassportInfo info)
        {
            // Door families may expose standard thickness as WINDOW_THICKNESS.
            info.LeafThicknessMm = PickLength(info, "leafThickness", "leaf_thickness",
                new[] { "DOOR_THICKNESS", "WINDOW_THICKNESS" },
                new[] { "GP_門扇厚度_mm", "Leaf Thickness", "Door Leaf Thickness", "Door Thickness", "Thickness", "門扇厚度", "門扇厚", "扇厚", "門厚", "厚度" });
        }

        private static IEnumerable<RawParameterInfo> CaptureParameters(Element element, string scope)
        {
            foreach (Parameter parameter in element.Parameters)
            {
                var item = new RawParameterInfo
                {
                    Scope = scope,
                    Name = parameter.Definition?.Name ?? "",
                    ParameterId = parameter.Id.Value.ToString(CultureInfo.InvariantCulture),
                    Storage = parameter.StorageType.ToString(),
                };
                try
                {
                    long id = parameter.Id.Value;
                    if (id < 0) item.Builtin = ((BuiltInParameter)id).ToString();
                }
                catch { }
                try { if (parameter.IsShared) item.Guid = parameter.GUID.ToString(); } catch { }
                try { item.DisplayValue = parameter.AsValueString() ?? ""; } catch { }
                try
                {
                    switch (parameter.StorageType)
                    {
                        case StorageType.Double:
                            double number = parameter.AsDouble();
                            item.RawValue = number.ToString("R", CultureInfo.InvariantCulture);
                            var dataType = parameter.Definition?.GetDataType();
                            if (dataType != null && dataType.Equals(SpecTypeId.Length))
                            {
                                item.NumericValueMm = UnitUtils.ConvertFromInternalUnits(number, UnitTypeId.Millimeters);
                                item.Unit = "mm";
                            }
                            else
                            {
                                try { item.Unit = parameter.GetUnitTypeId()?.TypeId ?? ""; } catch { }
                            }
                            break;
                        case StorageType.Integer:
                            item.RawValue = parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                            break;
                        case StorageType.String:
                            item.RawValue = parameter.AsString() ?? "";
                            break;
                        case StorageType.ElementId:
                            item.RawValue = parameter.AsElementId().Value.ToString(CultureInfo.InvariantCulture);
                            break;
                    }
                }
                catch { }
                if (string.IsNullOrWhiteSpace(item.DisplayValue)) item.DisplayValue = item.RawValue;
                yield return item;
            }
        }

        private static double PickLength(PassportInfo info, string field, string definition, string[] builtins, string[] names)
        {
            var candidates = Candidates(info.RawParameters, builtins, names)
                .Select(x => new { Parameter = x, Value = LengthMm(x), Priority = Priority(x, builtins, names) })
                .Where(x => x.Value.HasValue && x.Value.Value > 0)
                .OrderByDescending(x => x.Priority).ThenBy(x => x.Parameter.Name).ToList();
            if (candidates.Count == 0)
            {
                info.FieldSources[field] = Unknown("沒有明確數值或單位");
                info.MappingWarnings.Add(field + "：未找到具明確單位及語意的參數");
                return 0;
            }
            int topPriority = candidates[0].Priority;
            var top = candidates.Where(x => x.Priority == topPriority).ToList();
            if (top.Any(x => Math.Abs(x.Value.Value - top[0].Value.Value) > 0.1))
            {
                info.FieldSources[field] = Unknown("同優先序參數數值衝突");
                info.MappingWarnings.Add(field + "：同優先序參數數值衝突，未自動採用");
                return 0;
            }
            info.FieldSources[field] = Source(top[0].Parameter, definition);
            return top[0].Value.Value;
        }

        private static string PickText(PassportInfo info, string field, string[] names)
        {
            var candidates = Candidates(info.RawParameters, Array.Empty<string>(), names)
                .Where(x => !string.IsNullOrWhiteSpace(x.DisplayValue) || !string.IsNullOrWhiteSpace(x.RawValue))
                .Select(x => new { Parameter = x, Value = TextValue(x), Priority = Priority(x, Array.Empty<string>(), names) })
                .OrderByDescending(x => x.Priority).ThenBy(x => x.Parameter.Name).ToList();
            if (candidates.Count == 0)
            {
                info.FieldSources[field] = Unknown("模型未提供");
                return "";
            }
            int topPriority = candidates[0].Priority;
            var top = candidates.Where(x => x.Priority == topPriority).ToList();
            if (top.Any(x => !string.Equals(x.Value, top[0].Value, StringComparison.OrdinalIgnoreCase)))
            {
                info.FieldSources[field] = Unknown("同優先序參數文字衝突");
                info.MappingWarnings.Add(field + "：同優先序參數文字衝突，未自動採用");
                return "";
            }
            info.FieldSources[field] = Source(top[0].Parameter, "model_declared");
            return top[0].Value;
        }

        private static int PickCount(PassportInfo info, string field, string[] names, int fallback)
        {
            string text = PickText(info, field, names);
            var match = Regex.Match(text ?? "", @"\d+");
            if (match.Success && int.TryParse(match.Value, out int count) && count > 0) return count;
            return fallback;
        }

        private static IEnumerable<RawParameterInfo> Candidates(IEnumerable<RawParameterInfo> parameters, string[] builtins, string[] names)
        {
            var normalizedNames = new HashSet<string>(names.Select(Normalize));
            var builtinSet = new HashSet<string>(builtins, StringComparer.OrdinalIgnoreCase);
            return parameters.Where(p => builtinSet.Contains(p.Builtin ?? "") || normalizedNames.Contains(Normalize(p.Name)));
        }

        private static int Priority(RawParameterInfo parameter, string[] builtins, string[] names)
        {
            int value = 200;
            if ((parameter.Name ?? "").StartsWith("GP_", StringComparison.OrdinalIgnoreCase)) value = 400;
            else if (builtins.Any(x => string.Equals(x, parameter.Builtin, StringComparison.OrdinalIgnoreCase))) value = 300;
            else if ((parameter.Name ?? "").StartsWith("COBie.", StringComparison.OrdinalIgnoreCase)) value = 100;
            if (parameter.Scope == "Instance") value += 10;
            return value;
        }

        private static double? LengthMm(RawParameterInfo parameter)
        {
            if (parameter.NumericValueMm.HasValue) return parameter.NumericValueMm.Value;
            string text = TextValue(parameter).Trim();
            var explicitUnit = Regex.Match(text, @"(-?\d+(?:\.\d+)?)\s*(mm|毫米|公釐|cm|公分|m|公尺)\b", RegexOptions.IgnoreCase);
            if (explicitUnit.Success && double.TryParse(explicitUnit.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                string unit = explicitUnit.Groups[2].Value.ToLowerInvariant();
                if (unit == "cm" || unit == "公分") return number * 10;
                if (unit == "m" || unit == "公尺") return number * 1000;
                return number;
            }
            if ((parameter.Name ?? "").IndexOf("_mm", StringComparison.OrdinalIgnoreCase) >= 0 || (parameter.Name ?? "").Contains("（mm）") || (parameter.Name ?? "").Contains("(mm)"))
            {
                if (double.TryParse(parameter.RawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
                if (double.TryParse(parameter.DisplayValue, NumberStyles.Float, CultureInfo.CurrentCulture, out number)) return number;
            }
            return null;
        }

        private static string TextValue(RawParameterInfo parameter) =>
            string.IsNullOrWhiteSpace(parameter.DisplayValue) ? parameter.RawValue ?? "" : parameter.DisplayValue;

        private static string Normalize(string value) => Regex.Replace((value ?? "").Trim().ToLowerInvariant(), @"[\s_\-:：/\\()（）\[\]]", "");

        private static FieldSourceInfo Source(RawParameterInfo parameter, string definition) => new FieldSourceInfo
        {
            Kind = (parameter.Name ?? "").StartsWith("COBie.", StringComparison.OrdinalIgnoreCase) ? "cobie" : "bim",
            Scope = parameter.Scope,
            ParameterName = parameter.Name,
            ParameterId = parameter.ParameterId,
            Unit = parameter.Unit,
            Definition = definition,
            Verification = "BIM參數宣告，未經實體檢核／證明驗證",
        };

        private static FieldSourceInfo Unknown(string reason) => new FieldSourceInfo { Kind = "unknown", Verification = reason };

        private static string CanonicalUse(string value)
        {
            if (Regex.IsMatch(value ?? "", "室外|Exterior", RegexOptions.IgnoreCase)) return "Exterior";
            if (Regex.IsMatch(value ?? "", "室內|Interior", RegexOptions.IgnoreCase)) return "Interior";
            return value ?? "";
        }

        public static int ParseFireMinutes(string value)
        {
            string text = value ?? "";
            if (FirePerformanceData.IsExplicitZero(text)) return 0;
            var label = Regex.Match(text, @"f\s*\(\s*(\d+)", RegexOptions.IgnoreCase);
            if (label.Success) return int.Parse(label.Groups[1].Value, CultureInfo.InvariantCulture);
            var hours = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*(小時|hours?|hrs?|hr|h)\b", RegexOptions.IgnoreCase);
            if (hours.Success && double.TryParse(hours.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double h)) return (int)Math.Round(h * 60);
            var minutes = Regex.Match(text, @"(\d+)\s*(分鐘|分钟|minutes?|mins?|min|分)\b", RegexOptions.IgnoreCase);
            if (minutes.Success) return int.Parse(minutes.Groups[1].Value, CultureInfo.InvariantCulture);
            return 0;
        }
    }
}
