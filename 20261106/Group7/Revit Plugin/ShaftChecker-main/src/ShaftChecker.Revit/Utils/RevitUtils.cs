using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using ShaftChecker.Core.Config;

namespace ShaftChecker.Revit.Utils
{
    /// <summary>Revit 2022 與 2024+ 的 ElementId API 差異統一在這裡處理。</summary>
    public static class ElementIdUtil
    {
        public static long GetIdValue(this ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        public static ElementId FromLong(long value)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(value);
#else
            return new ElementId((int)value);
#endif
        }
    }

    /// <summary>Revit 內部單位為英尺，這裡統一換算成 mm。</summary>
    public static class UnitConv
    {
        public static double ToMm(double feet)
        {
            return UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
        }

        public static double FromMm(double mm)
        {
            return UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        }

        public static double ToM2(double squareFeet)
        {
            return UnitUtils.ConvertFromInternalUnits(squareFeet, UnitTypeId.SquareMeters);
        }

        public static double FromM2(double m2)
        {
            return UnitUtils.ConvertToInternalUnits(m2, UnitTypeId.SquareMeters);
        }
    }

    /// <summary>外掛安裝路徑與設定檔讀取。</summary>
    public static class AddinPaths
    {
        public static string AddinFolder
        {
            get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(AddinFolder, "config", "shaft-rules.json"); }
        }

        public static RuleConfig LoadConfig()
        {
            return RuleConfig.Load(ConfigPath);
        }
    }

    /// <summary>讀取參數：先找實體、再找類型；名稱找不到時用內建參數。</summary>
    public static class ParamReader
    {
        public static string ReadText(Element inst, Element type, string name, BuiltInParameter? bip)
        {
            Parameter p = Find(inst, type, name, bip);
            if (p == null || !p.HasValue) return null;
            string s = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        public static bool? ReadYesNo(Element inst, Element type, string name)
        {
            Parameter p = Find(inst, type, name, null);
            if (p == null || !p.HasValue) return null;

            if (p.StorageType == StorageType.Integer) return p.AsInteger() == 1;

            string s = (p.StorageType == StorageType.String ? p.AsString() : p.AsValueString()) ?? "";
            s = s.Trim().ToLowerInvariant();
            if (s.Length == 0) return null;
            return s == "是" || s == "有" || s == "yes" || s == "y" || s == "true" || s == "1";
        }

        /// <summary>讀長度（回傳 mm），依序嘗試多個內建參數。</summary>
        public static double ReadLengthMm(Element inst, Element type, params BuiltInParameter[] bips)
        {
            foreach (var bip in bips)
            {
                Parameter p = inst != null ? inst.get_Parameter(bip) : null;
                if (p == null || !p.HasValue) p = type != null ? type.get_Parameter(bip) : null;
                if (p != null && p.HasValue && p.StorageType == StorageType.Double)
                    return UnitConv.ToMm(p.AsDouble());
            }
            return 0;
        }

        public static double? ReadNamedLengthMm(Element inst, Element type, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Parameter p = Find(inst, type, name, null);
            if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return null;
            return UnitConv.ToMm(p.AsDouble());
        }

        private static Parameter Find(Element inst, Element type, string name, BuiltInParameter? bip)
        {
            Parameter p = null;
            if (!string.IsNullOrEmpty(name))
            {
                if (inst != null) p = inst.LookupParameter(name);
                if ((p == null || !p.HasValue) && type != null) p = type.LookupParameter(name);
            }
            if ((p == null || !p.HasValue) && bip.HasValue)
            {
                if (type != null) p = type.get_Parameter(bip.Value);
                if ((p == null || !p.HasValue) && inst != null) p = inst.get_Parameter(bip.Value);
            }
            return p;
        }
    }
}
