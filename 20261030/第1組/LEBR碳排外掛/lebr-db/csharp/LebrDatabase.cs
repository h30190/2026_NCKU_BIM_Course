using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace LebrCarbon
{
    /// <summary>
    /// LEBR 低碳（低蘊含碳）建築評估資料庫（lebr_2025.json）的讀取與查表。
    /// 只負責「讀資料」與「查係數」，碳排計算流程另外實作。
    /// 寫法維持 C# 7.3 相容，讓 Revit 2022～2024（.NET Framework 4.8）也能編譯。
    /// </summary>
    public class LebrDatabase
    {
        public const string SupportedSchemaMajor = "1";

        public LebrMeta Meta { get; private set; }
        public LebrParameters Parameters { get; private set; }
        public IReadOnlyList<PrimaryMaterial> PrimaryMaterials { get; private set; }
        public IReadOnlyList<BuildingComponent> Components { get; private set; }

        private Dictionary<string, BuildingComponent> _componentsByCode;
        private Dictionary<string, PrimaryMaterial> _materialsById;

        public static LebrDatabase Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("找不到 LEBR 資料庫檔案", path);
            return Parse(File.ReadAllText(path));
        }

        public static LebrDatabase Parse(string json)
        {
            var raw = JsonConvert.DeserializeObject<RawDatabase>(json);
            if (raw == null || raw.Meta == null)
                throw new InvalidDataException("LEBR 資料庫格式錯誤：缺少 meta");
            var major = (raw.Meta.SchemaVersion ?? "").Split('.')[0];
            if (major != SupportedSchemaMajor)
                throw new InvalidDataException(
                    "LEBR 資料庫 schema_version " + raw.Meta.SchemaVersion + " 與外掛支援的 " + SupportedSchemaMajor + ".x 不相容");

            var db = new LebrDatabase
            {
                Meta = raw.Meta,
                Parameters = raw.Parameters ?? new LebrParameters(),
                PrimaryMaterials = raw.PLcc ?? new List<PrimaryMaterial>(),
                Components = raw.BLcc ?? new List<BuildingComponent>(),
            };
            db._componentsByCode = db.Components.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
            db._materialsById = db.PrimaryMaterials.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
            return db;
        }

        /// <summary>資料是否已完成人工核對（meta.review_status = reviewed）。</summary>
        public bool IsReviewed
        {
            get { return string.Equals(Meta.ReviewStatus, "reviewed", StringComparison.OrdinalIgnoreCase); }
        }

        // ------------------------------------------------------------ B-LCC 構件

        public bool TryGetComponent(string code, out BuildingComponent component)
        {
            component = null;
            return !string.IsNullOrWhiteSpace(code) && _componentsByCode.TryGetValue(code.Trim(), out component);
        }

        public BuildingComponent GetComponent(string code)
        {
            BuildingComponent c;
            if (!TryGetComponent(code, out c))
                throw new KeyNotFoundException("LEBR 資料庫沒有構件編號 " + code);
            return c;
        }

        /// <summary>該構件比較用的基準構件（基準案計算時，面積相同、改用此構件）。</summary>
        public BuildingComponent GetBaseline(BuildingComponent component)
        {
            return GetComponent(component.BaselineCode);
        }

        /// <summary>
        /// 某類構件的基準構件。wearClass 用於分建築類別的附表（IW、F、P）；
        /// subgroup 用於同類有多個基準者（W/CWW 的玻璃種類、F 的一般/木作地坪、P 的基層結構）。
        /// </summary>
        public IEnumerable<BuildingComponent> GetBaselines(string group, string wearClass = null)
        {
            return Components.Where(c => c.IsBaseline
                                         && string.Equals(c.Group, group, StringComparison.OrdinalIgnoreCase)
                                         && (wearClass == null || c.WearClass == null || c.WearClass == wearClass));
        }

        public IEnumerable<BuildingComponent> GetComponents(string group, string wearClass = null)
        {
            return Components.Where(c => string.Equals(c.Group, group, StringComparison.OrdinalIgnoreCase)
                                         && (wearClass == null || c.WearClass == null || c.WearClass == wearClass));
        }

        // ------------------------------------------------------------ P-LCC 初級資材

        public bool TryGetMaterial(string id, out PrimaryMaterial material)
        {
            material = null;
            return !string.IsNullOrWhiteSpace(id) && _materialsById.TryGetValue(id.Trim(), out material);
        }

        public IEnumerable<PrimaryMaterial> FindMaterials(string keyword, string unit = null)
        {
            return PrimaryMaterials.Where(m => m.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0
                                               && (unit == null || m.Unit == unit));
        }

        // ------------------------------------------------------------ 參數查表

        /// <summary>表4-3 平面形狀不規則修正係數 f1。PAr = 0.282 × P / √A。</summary>
        public double ShapeFactorF1(double perimeterM, double areaM2)
        {
            var par = Parameters.ShapeFactorF1.PaRatioCoefficient * perimeterM / Math.Sqrt(areaM2);
            return LookupRange(Parameters.ShapeFactorF1.Ranges, par, "表4-3 f1");
        }

        /// <summary>表4-4 平面長寬比修正係數 f2，aspect = 長邊 / 短邊。</summary>
        public double AspectFactorF2(double aspect)
        {
            return LookupRange(Parameters.AspectFactorF2.Ranges, aspect, "表4-4 f2");
        }

        /// <summary>表4-5 平面出挑修正係數 f3，rc = 出挑面積 / 當層總樓地板面積。出挑深度 &gt; 4m 時不適用。</summary>
        public double CantileverFactorF3(double rc, double maxCantileverDepthM = 0)
        {
            if (maxCantileverDepthM > Parameters.CantileverFactorF3.MaxCantileverDepthM)
                throw new InvalidOperationException("出挑深度大於 4m，表4-5 不適用，應改用對比模型結構分析法");
            return LookupRange(Parameters.CantileverFactorF3.Ranges, rc, "表4-5 f3");
        }

        /// <summary>表4-1 構造係數 W。key：masonry / RC / SRC / S / light_steel / wood。</summary>
        public double StructureFactorW(string structureKey)
        {
            StructureFactor f;
            if (Parameters.StructureFactorW == null || !Parameters.StructureFactorW.Items.TryGetValue(structureKey, out f))
                throw new KeyNotFoundException("表4-1 沒有構造類別 " + structureKey);
            return f.W;
        }

        /// <summary>表4-7 新建與拆除廢棄物量密度 Wd（kg/m2）。木構造依手冊以輕鋼構造代用。</summary>
        public double WasteDensityWd(string structureKey, string useType)
        {
            var key = structureKey == "wood" ? "light_steel" : structureKey;
            if (key == "light_steel" && useType == "residential")
                useType = "other";  // 輕鋼構造只有廠房類與其他類
            var item = Parameters.WasteDensityWd.Items.FirstOrDefault(i => i.Structure == key && i.UseType == useType);
            if (item == null)
                throw new KeyNotFoundException("表4-7 沒有 " + structureKey + " / " + useType);
            return item.Wd;
        }

        /// <summary>表5-1 依碳排減碳率 CFR（小數，0.2 = 20%）判定等級。</summary>
        public string GradeFor(double cfr)
        {
            foreach (var g in Parameters.GradeScale.Grades)
            {
                var aboveMin = g.CfrMinExclusive == null || cfr > g.CfrMinExclusive.Value;
                var belowMax = g.CfrMaxInclusive == null || cfr <= g.CfrMaxInclusive.Value;
                if (aboveMin && belowMax)
                    return g.Grade;
            }
            throw new InvalidOperationException("CFR " + cfr + " 無法對應表5-1 等級");
        }

        /// <summary>表4-6 基準案構造係數 W。</summary>
        public double BaselineW(int floors)
        {
            return LookupFloors(Parameters.BaselineStructureScenario.W, floors, "表4-6 W");
        }

        /// <summary>表4-6 基準案形狀係數 F。</summary>
        public double BaselineShapeF(int floors)
        {
            return LookupFloors(Parameters.BaselineStructureScenario.F, floors, "表4-6 F");
        }

        /// <summary>表4-6 基準案跨距變化係數 Sp，averageFloorAreaM2 = 地上單層平均樓地板面積 AFa。</summary>
        public double BaselineSp(int floors, double averageFloorAreaM2)
        {
            foreach (var r in Parameters.BaselineStructureScenario.Sp)
            {
                if (!r.MatchesArea(averageFloorAreaM2)) continue;
                if (floors >= r.FloorsMin && (r.FloorsMax == null || floors <= r.FloorsMax.Value))
                    return r.Value;
            }
            throw new InvalidOperationException("表4-6 Sp 查不到 " + floors + " 層、AFa " + averageFloorAreaM2);
        }

        private static double LookupRange(IEnumerable<ValueRange> ranges, double x, string label)
        {
            foreach (var r in ranges)
            {
                if ((r.MinExclusive == null || x > r.MinExclusive.Value) && (r.MaxInclusive == null || x <= r.MaxInclusive.Value))
                    return r.Value;
            }
            throw new InvalidOperationException(label + " 查不到 " + x);
        }

        private static double LookupFloors(IEnumerable<FloorRange> ranges, int floors, string label)
        {
            foreach (var r in ranges)
            {
                if (floors >= r.FloorsMin && (r.FloorsMax == null || floors <= r.FloorsMax.Value))
                    return r.Value;
            }
            throw new InvalidOperationException(label + " 查不到 " + floors + " 層");
        }

        private class RawDatabase
        {
            [JsonProperty("meta")] public LebrMeta Meta { get; set; }
            [JsonProperty("parameters")] public LebrParameters Parameters { get; set; }
            [JsonProperty("p_lcc")] public List<PrimaryMaterial> PLcc { get; set; }
            [JsonProperty("b_lcc")] public List<BuildingComponent> BLcc { get; set; }
        }
    }

    // ================================================================ 資料模型

    public class LebrMeta
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("manual")] public string Manual { get; set; }
        [JsonProperty("manual_version")] public string ManualVersion { get; set; }
        [JsonProperty("schema_version")] public string SchemaVersion { get; set; }
        [JsonProperty("review_status")] public string ReviewStatus { get; set; }
        [JsonProperty("generated")] public string Generated { get; set; }
    }

    public class SourceRef
    {
        [JsonProperty("book_page")] public int BookPage { get; set; }
        [JsonProperty("pdf_page")] public int PdfPage { get; set; }
    }

    public class CheckResult
    {
        /// <summary>OK / CHECK（手冊本身數字不一致）/ INFO（無法驗算）</summary>
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("note")] public string Note { get; set; }
    }

    /// <summary>附錄一 P-LCC 初級資材（每單位碳排，kgCO2e）。</summary>
    public class PrimaryMaterial
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("category")] public string Category { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("unit")] public string Unit { get; set; }
        [JsonProperty("a1")] public double? A1 { get; set; }
        [JsonProperty("a2")] public double? A2 { get; set; }
        [JsonProperty("a3")] public double? A3 { get; set; }
        [JsonProperty("a4")] public double? A4 { get; set; }
        /// <summary>手冊合併儲存格，例如 "A1-A3" 表示 A1 欄的數字是 A1～A3 合計。</summary>
        [JsonProperty("merged_span")] public string MergedSpan { get; set; }
        /// <summary>總碳排量 A1～A4（kgCO2e / 單位）。計算時用這個值。</summary>
        [JsonProperty("total")] public double Total { get; set; }
        [JsonProperty("note")] public string Note { get; set; }
        [JsonProperty("source")] public SourceRef Source { get; set; }
        [JsonProperty("check")] public CheckResult Check { get; set; }
    }

    /// <summary>附錄二 B-LCC 建築構件（kgCO2e/m2）。</summary>
    public class BuildingComponent
    {
        /// <summary>構件編號，對應 Revit 類型參數 LEBR_Code。</summary>
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("table")] public string Table { get; set; }
        /// <summary>OW 外牆外裝 / W 外窗 / CWW 透光帷幕 / CW 不透光帷幕 / IW 內隔間 / F 室內地坪 / P 戶外地坪</summary>
        [JsonProperty("group")] public string Group { get; set; }
        /// <summary>H 高耗損 / M 中耗損 / L 低耗損 / S 46-6分戶樓板；不分類別的附表為 null。</summary>
        [JsonProperty("wear_class")] public string WearClass { get; set; }
        [JsonProperty("subgroup")] public string Subgroup { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("base_a")] public double? BaseA { get; set; }
        [JsonProperty("surface_b")] public double? SurfaceB { get; set; }
        [JsonProperty("a_text")] public string AText { get; set; }
        [JsonProperty("b_text")] public string BText { get; set; }
        /// <summary>新建碳排 F（kgCO2e/m2）。</summary>
        [JsonProperty("f_new")] public double FNew { get; set; }
        [JsonProperty("renew_base_c")] public double RenewBaseC { get; set; }
        [JsonProperty("renew_surface_c")] public double RenewSurfaceC { get; set; }
        /// <summary>更新碳排 F*（kgCO2e/m2，RC/SRC/S 構造之 60 年更新次數）。</summary>
        [JsonProperty("f_renew")] public double FRenew { get; set; }
        /// <summary>相對基準構件的減碳量 ΔF，負數 = 較低碳。</summary>
        [JsonProperty("delta_f")] public double DeltaF { get; set; }
        [JsonProperty("is_baseline")] public bool IsBaseline { get; set; }
        [JsonProperty("baseline_code")] public string BaselineCode { get; set; }
        [JsonProperty("source")] public SourceRef Source { get; set; }
        [JsonProperty("check")] public CheckResult Check { get; set; }

        /// <summary>
        /// 依構造調整後的更新碳排。表3-1：輕鋼構 RT × 0.8、木構造 RT × 0.5（multiplier 取自
        /// parameters.life_cycle_renewal.structure_multiplier）；RC/SRC/S 傳 1.0 直接回傳手冊值。
        /// </summary>
        public double FRenewFor(double renewalMultiplier)
        {
            if (Math.Abs(renewalMultiplier - 1.0) < 1e-9)
                return FRenew;
            return ((BaseA ?? 0) * RenewBaseC + (SurfaceB ?? 0) * RenewSurfaceC) * renewalMultiplier;
        }
    }

    public class LebrParameters
    {
        [JsonProperty("structure_factor_W")] public KeyedTable<StructureFactor> StructureFactorW { get; set; }
        [JsonProperty("shape_factor_f1")] public ShapeFactorTable ShapeFactorF1 { get; set; }
        [JsonProperty("aspect_factor_f2")] public RangeTable AspectFactorF2 { get; set; }
        [JsonProperty("cantilever_factor_f3")] public CantileverTable CantileverFactorF3 { get; set; }
        [JsonProperty("baseline_structure_scenario")] public BaselineScenario BaselineStructureScenario { get; set; }
        [JsonProperty("waste_density_Wd")] public WasteDensityTable WasteDensityWd { get; set; }
        [JsonProperty("grade_scale")] public GradeScaleTable GradeScale { get; set; }
        [JsonProperty("life_cycle_renewal")] public LifeCycleRenewalTable LifeCycleRenewal { get; set; }
        [JsonProperty("life_extension_LL")] public LifeExtensionTable LifeExtensionLL { get; set; }
        /// <summary>第四、五章公式常數，用 Constant("structure_C", "base") 取值（= 224）。</summary>
        [JsonProperty("formula_constants")] public KeyedTable<Dictionary<string, object>> FormulaConstants { get; set; }

        public double Constant(string formula, string name)
        {
            Dictionary<string, object> f;
            object v;
            if (FormulaConstants == null || !FormulaConstants.Items.TryGetValue(formula, out f) || !f.TryGetValue(name, out v))
                throw new KeyNotFoundException("formula_constants 沒有 " + formula + "." + name);
            return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public class KeyedTable<T>
    {
        [JsonProperty("items")] public Dictionary<string, T> Items { get; set; }
    }

    public class StructureFactor
    {
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("W")] public double W { get; set; }
    }

    public class ValueRange
    {
        [JsonProperty("min_exclusive")] public double? MinExclusive { get; set; }
        [JsonProperty("max_inclusive")] public double? MaxInclusive { get; set; }
        [JsonProperty("value")] public double Value { get; set; }
    }

    public class RangeTable
    {
        [JsonProperty("ranges")] public List<ValueRange> Ranges { get; set; }
    }

    public class ShapeFactorTable : RangeTable
    {
        [JsonProperty("pa_ratio_coefficient")] public double PaRatioCoefficient { get; set; }
    }

    public class CantileverTable : RangeTable
    {
        [JsonProperty("max_cantilever_depth_m")] public double MaxCantileverDepthM { get; set; }
    }

    public class FloorRange
    {
        [JsonProperty("floors_min")] public int FloorsMin { get; set; }
        [JsonProperty("floors_max")] public int? FloorsMax { get; set; }
        [JsonProperty("value")] public double Value { get; set; }
    }

    public class SpRange : FloorRange
    {
        [JsonProperty("AFa_min_exclusive")] public double? AfaMinExclusive { get; set; }
        [JsonProperty("AFa_min_inclusive")] public double? AfaMinInclusive { get; set; }
        [JsonProperty("AFa_max_exclusive")] public double? AfaMaxExclusive { get; set; }
        [JsonProperty("AFa_max_inclusive")] public double? AfaMaxInclusive { get; set; }

        public bool MatchesArea(double afa)
        {
            return (AfaMinExclusive == null || afa > AfaMinExclusive.Value)
                   && (AfaMinInclusive == null || afa >= AfaMinInclusive.Value)
                   && (AfaMaxExclusive == null || afa < AfaMaxExclusive.Value)
                   && (AfaMaxInclusive == null || afa <= AfaMaxInclusive.Value);
        }
    }

    public class BaselineScenario
    {
        [JsonProperty("W")] public List<FloorRange> W { get; set; }
        [JsonProperty("LCCR")] public double Lccr { get; set; }
        [JsonProperty("Sp")] public List<SpRange> Sp { get; set; }
        [JsonProperty("F")] public List<FloorRange> F { get; set; }
    }

    public class WasteDensityItem
    {
        [JsonProperty("structure")] public string Structure { get; set; }
        [JsonProperty("use_type")] public string UseType { get; set; }
        [JsonProperty("Wd")] public double Wd { get; set; }
    }

    public class WasteDensityTable
    {
        [JsonProperty("items")] public List<WasteDensityItem> Items { get; set; }
    }

    public class GradeRange
    {
        [JsonProperty("grade")] public string Grade { get; set; }
        [JsonProperty("cfr_min_exclusive")] public double? CfrMinExclusive { get; set; }
        [JsonProperty("cfr_max_inclusive")] public double? CfrMaxInclusive { get; set; }
        [JsonProperty("scale_factor")] public double? ScaleFactor { get; set; }
    }

    public class GradeScaleTable
    {
        [JsonProperty("grades")] public List<GradeRange> Grades { get; set; }
    }

    public class LifeCycleRenewalTable
    {
        /// <summary>light_steel = 0.8、wood = 0.5</summary>
        [JsonProperty("structure_multiplier")] public Dictionary<string, double> StructureMultiplier { get; set; }
    }

    public class LifeExtensionItem
    {
        [JsonProperty("id")] public int Id { get; set; }
        [JsonProperty("group")] public string Group { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("LL")] public double LL { get; set; }
        [JsonProperty("rc_only")] public bool RcOnly { get; set; }
        [JsonProperty("exclusive_with")] public List<int> ExclusiveWith { get; set; }
    }

    public class LifeExtensionTable
    {
        [JsonProperty("max_total")] public double MaxTotal { get; set; }
        [JsonProperty("items")] public List<LifeExtensionItem> Items { get; set; }
    }
}
