using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LebrCarbon
{
    // ================================================================ 輸入：數量格式（計畫書第十一章）

    /// <summary>
    /// 計算引擎的輸入。由 Revit 數量擷取（組員 1）產生，或從數量檔載入（例如手冊附錄三範例）。
    /// </summary>
    public class QuantityInput
    {
        [JsonProperty("source")] public string Source { get; set; }
        [JsonProperty("building")] public BuildingGeometry Building { get; set; }
        [JsonProperty("project")] public ProjectSettings Project { get; set; }
        [JsonProperty("components")] public List<ComponentQuantity> Components { get; set; }
        [JsonProperty("unmapped")] public List<UnmappedElement> Unmapped { get; set; }
        /// <summary>數量擷取時的說明（例如哪些值是估算的），顯示在計算前檢查。</summary>
        [JsonProperty("notes")] public List<string> Notes { get; set; }

        public static QuantityInput Load(string path)
        {
            return Parse(File.ReadAllText(path));
        }

        public static QuantityInput Parse(string json)
        {
            var input = JsonConvert.DeserializeObject<QuantityInput>(json) ?? new QuantityInput();
            if (input.Building == null) input.Building = new BuildingGeometry();
            if (input.Project == null) input.Project = new ProjectSettings();
            if (input.Components == null) input.Components = new List<ComponentQuantity>();
            if (input.Unmapped == null) input.Unmapped = new List<UnmappedElement>();
            if (input.Notes == null) input.Notes = new List<string>();
            return input;
        }
    }

    /// <summary>整棟幾何（m2、層、m）。設計案的 Sp、F 由 Revit 計算；基準案由引擎查表 4-6。</summary>
    public class BuildingGeometry
    {
        [JsonProperty("AFu")] public double? AFu { get; set; }
        [JsonProperty("AFb")] public double? AFb { get; set; }
        /// <summary>地上樓層數（不含屋突），不同樓層混合時為面積加權值。</summary>
        [JsonProperty("S")] public double? S { get; set; }
        [JsonProperty("Sb")] public double? Sb { get; set; }
        [JsonProperty("BH")] public double? BH { get; set; }
        [JsonProperty("Sp")] public double? Sp { get; set; }
        [JsonProperty("F")] public double? F { get; set; }
    }

    /// <summary>專案參數（Revit 專案資訊）。結構參數來自結構計算書。</summary>
    public class ProjectSettings
    {
        /// <summary>H 高耗損 / M 中耗損 / L 低耗損（表 3-1）</summary>
        [JsonProperty("wear_class")] public string WearClass { get; set; }
        /// <summary>masonry / RC / SRC / S / light_steel / wood（表 4-1）</summary>
        [JsonProperty("structure")] public string Structure { get; set; }
        /// <summary>residential / factory / other（表 4-7）</summary>
        [JsonProperty("use_type")] public string UseType { get; set; }
        [JsonProperty("I")] public double? I { get; set; }
        [JsonProperty("SaD")] public double? SaD { get; set; }
        [JsonProperty("Fu")] public double? Fu { get; set; }
        [JsonProperty("L")] public double? L { get; set; }
        [JsonProperty("D0")] public double? D0 { get; set; }
        [JsonProperty("Rs")] public double? Rs { get; set; }
        [JsonProperty("CSER")] public double CSER { get; set; }
        [JsonProperty("LL")] public double LL { get; set; }
        /// <summary>舊建築再利用地上面積 EBF（m2），式 6。</summary>
        [JsonProperty("EBF")] public double EBF { get; set; }
        /// <summary>式 21 的三項減碳優惠（kgCO2e），需評定機構認可。</summary>
        [JsonProperty("reuse_reduction")] public double ReuseReduction { get; set; }
        [JsonProperty("low_carbon_material_reduction")] public double LowCarbonMaterialReduction { get; set; }
        [JsonProperty("low_carbon_method_reduction")] public double LowCarbonMethodReduction { get; set; }
        /// <summary>結構碳排估算值（LEBR_CFs）。有值（大於 0）時作為設計案 CFs，略過 Sp、F 計算；不再套用 Lccr 與 Rn。</summary>
        [JsonProperty("structure_CFs")] public double? StructureCFs { get; set; }
        /// <summary>結構碳排估算紀錄（LEBR_EstimateJson），匯出結果 JSON 時轉成 structure.bim。不參與計算。</summary>
        [JsonProperty("structure_estimate_json")] public string StructureEstimateJson { get; set; }
    }

    public class ComponentQuantity
    {
        /// <summary>Revit 類型上的 LEBR_Code。可省略建築類別（IW-03），引擎依專案類別補成 IW-M-03。</summary>
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("area_m2")] public double AreaM2 { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("element_ids")] public List<long> ElementIds { get; set; }
        /// <summary>附錄二沒有的構件，直接給係數（手冊 3-4 節第 4 點）。</summary>
        [JsonProperty("custom")] public CustomFactors Custom { get; set; }
    }

    public class CustomFactors
    {
        [JsonProperty("group")] public string Group { get; set; }
        [JsonProperty("f_new")] public double FNew { get; set; }
        [JsonProperty("f_renew")] public double FRenew { get; set; }
        [JsonProperty("baseline_f_new")] public double BaselineFNew { get; set; }
        [JsonProperty("baseline_f_renew")] public double BaselineFRenew { get; set; }
        [JsonProperty("basis")] public string Basis { get; set; }
    }

    public class UnmappedElement
    {
        [JsonProperty("element_id")] public long? ElementId { get; set; }
        [JsonProperty("category")] public string Category { get; set; }
        [JsonProperty("type")] public string TypeName { get; set; }
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; }
    }

    // ================================================================ 輸出

    public enum IssueLevel { Info, Warning, Error }

    public class LebrIssue
    {
        public IssueLevel Level { get; set; }
        public string Message { get; set; }

        public string LevelText
        {
            get { return Level == IssueLevel.Error ? "缺少" : Level == IssueLevel.Warning ? "注意" : "說明"; }
        }
    }

    /// <summary>單一情境（設計案或基準案）地上層的四階段碳排（kgCO2e）。</summary>
    public class ScenarioResult
    {
        public double CFs { get; set; }
        public double CFns { get; set; }
        public double CFum { get { return CFs + CFns; } }
        public double CFrm { get; set; }
        public double CFc { get; set; }
        public double CFdw { get; set; }
        public double Reductions { get; set; }
        public double Subtotal { get { return CFum + CFrm + CFc + CFdw; } }
    }

    public class StructureDetail
    {
        public bool Computed { get; set; }
        public double DesignC { get; set; }
        public double BaselineC { get; set; }
        public double DesignW { get; set; }
        public double BaselineW { get; set; }
        public double DesignSp { get; set; }
        public double BaselineSp { get; set; }
        public double DesignF { get; set; }
        public double BaselineF { get; set; }
        public double Lccr { get; set; }
        public double Rn { get; set; }
        /// <summary>基準案 EBF = 0，Rn = 1.0。</summary>
        public double BaselineRn { get; set; }
    }

    public class ItemResult
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public string Group { get; set; }
        public string GroupLabel { get; set; }
        public double AreaM2 { get; set; }
        public double FNew { get; set; }
        public double FRenew { get; set; }
        public double DesignNew { get; set; }
        public double DesignRenew { get; set; }
        public double Design { get { return DesignNew + DesignRenew; } }
        public string BaselineCode { get; set; }
        public string BaselineName { get; set; }
        public double BaselineNew { get; set; }
        public double BaselineRenew { get; set; }
        public double Baseline { get { return BaselineNew + BaselineRenew; } }
        public double Reduction { get { return Baseline - Design; } }
        public List<long> ElementIds { get; set; }
    }

    public class StageRow
    {
        public string Stage { get; set; }
        public double Design { get; set; }
        public double Baseline { get; set; }
        public double DesignShare { get; set; }
        public double Reduction { get { return Baseline - Design; } }
    }

    public class GroupRow
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public double Design { get; set; }
        public double Baseline { get; set; }
        public double DesignShare { get; set; }
        public double Reduction { get { return Baseline - Design; } }
    }

    public class GradeMark
    {
        public string Grade { get; set; }
        public double? Eci { get; set; }
    }

    public class LebrResult
    {
        public string Source { get; set; }
        public string DatabaseVersion { get; set; }
        public string DatabaseReviewStatus { get; set; }
        public DateTime CalculatedAt { get; set; }

        /// <summary>主結構等必要項目都算到了；false 時等級只能當參考。</summary>
        public bool Complete { get; set; }
        public List<LebrIssue> Issues { get; set; }

        public double AFu { get; set; }
        public double AFb { get; set; }
        public double S { get; set; }
        public double Sb { get; set; }
        public string WearClass { get; set; }
        public double LL { get; set; }

        public StructureDetail Structure { get; set; }
        public ScenarioResult Design { get; set; }
        public ScenarioResult Baseline { get; set; }

        public double EEC { get; set; }
        public double EECc { get; set; }
        public double DeltaCF { get { return EECc - EEC; } }
        public double? CFR { get; set; }
        public string Grade { get; set; }
        public double ECI { get; set; }
        public double ECIs { get; set; }

        public double UndergroundStructure { get; set; }
        public double UndergroundConstruction { get; set; }
        public double UndergroundDemolition { get; set; }
        public double TEC { get { return EEC + UndergroundStructure + UndergroundConstruction + UndergroundDemolition; } }
        public double TECc { get { return EECc + UndergroundStructure + UndergroundConstruction + UndergroundDemolition; } }

        public List<StageRow> Stages { get; set; }
        public List<GroupRow> Groups { get; set; }
        public List<ItemResult> Items { get; set; }
        public List<UnmappedElement> Unmapped { get; set; }
        public List<GradeMark> GradeScale { get; set; }
        /// <summary>計算時用的整棟幾何與專案參數（匯出時附上，網頁可做輸入參數面板）。</summary>
        public BuildingGeometry InputBuilding { get; set; }
        public ProjectSettings InputProject { get; set; }

        /// <summary>給網頁用的結果格式（計畫書第八章）。</summary>
        public string ToJson()
        {
            var o = new JObject
            {
                ["project"] = new JObject
                {
                    ["source"] = Source, ["AFu"] = AFu, ["AFb"] = AFb, ["floors"] = S, ["basement_floors"] = Sb,
                    ["wear_class"] = WearClass, ["LL"] = LL,
                },
                ["result"] = new JObject
                {
                    ["complete"] = Complete, ["TEC"] = TEC, ["TECc"] = TECc, ["EEC"] = EEC, ["EECc"] = EECc,
                    ["delta_CF"] = DeltaCF, ["CFR"] = CFR, ["grade"] = Grade, ["ECI"] = ECI, ["ECIs"] = ECIs,
                },
                ["grade_scale"] = JArray.FromObject(GradeScale.Select(g => new { grade = g.Grade, eci = g.Eci })),
                ["by_stage"] = JArray.FromObject(Stages.Select(s => new { stage = s.Stage, design = s.Design, baseline = s.Baseline })),
                ["by_component"] = JArray.FromObject(Groups.Select(g => new { key = g.Key, label = g.Label, design = g.Design, baseline = g.Baseline })),
                ["underground"] = new JObject
                {
                    ["structure"] = UndergroundStructure, ["construction"] = UndergroundConstruction, ["demolition"] = UndergroundDemolition,
                },
                ["structure"] = StructureJson(),
                ["inputs"] = new JObject
                {
                    ["building"] = InputBuilding == null ? null : JObject.FromObject(InputBuilding),
                    ["project"] = InputProjectJson(),
                },
                ["items"] = JArray.FromObject(Items.Select(i => new
                {
                    code = i.Code, name = i.Name, group = i.Group, area_m2 = i.AreaM2,
                    design_new = i.DesignNew, design_renew = i.DesignRenew,
                    baseline_code = i.BaselineCode, baseline_new = i.BaselineNew, baseline_renew = i.BaselineRenew,
                    element_ids = i.ElementIds ?? new List<long>(),
                })),
                ["unmapped"] = JArray.FromObject(Unmapped),
                ["issues"] = JArray.FromObject(Issues.Select(i => new { level = i.Level.ToString().ToLowerInvariant(), message = i.Message })),
                ["database"] = new JObject
                {
                    ["manual_version"] = DatabaseVersion, ["review_status"] = DatabaseReviewStatus,
                },
                ["calculated_at"] = CalculatedAt.ToString("yyyy-MM-ddTHH:mm:ss"),
            };
            return o.ToString(Formatting.Indented);
        }

        /// <summary>
        /// 主結構區塊，欄位依「主結構 BIM 數量法｜結果 JSON 輸出規格」v1 第 2-1 節。
        /// method：standard（式 1～7）、bim_quantity（採 LEBR_CFs 估算值）、none（設計案未計算）。
        /// standard 內是式 4、式 1 的參數與中間值，算不出來的為 null；基準案一律依表 4-6，採估算值時仍輸出。
        /// bim 是 BIM 數量法明細（StructureBim 由估算紀錄轉出），只有 method = bim_quantity 且估算紀錄含明細時才有值。
        /// </summary>
        /// <summary>輸入參數；估算紀錄本身很長，已轉成 structure.bim，這裡不重複輸出。</summary>
        private JObject InputProjectJson()
        {
            if (InputProject == null) return null;
            var o = JObject.FromObject(InputProject);
            o.Remove("structure_estimate_json");
            return o;
        }

        private JObject StructureJson()
        {
            var d = Structure ?? new StructureDetail();
            var b = InputBuilding ?? new BuildingGeometry();
            var p = InputProject ?? new ProjectSettings();
            var estimated = d.Computed && p.StructureCFs > 0;
            var standard = d.Computed && !estimated;
            var hasBaseline = d.BaselineC > 0;
            Func<bool, double, JToken> v = (ok, x) => ok ? new JValue(x) : JValue.CreateNull();
            Func<double?, JToken> n = x => x.HasValue ? new JValue(x.Value) : JValue.CreateNull();
            return new JObject
            {
                ["method"] = estimated ? "bim_quantity" : standard ? "standard" : "none",
                ["design_CFs"] = v(d.Computed, Design == null ? 0 : Design.CFs),
                ["baseline_CFsc"] = v(hasBaseline, Baseline == null ? 0 : Baseline.CFs),
                ["intensity"] = v(d.Computed && AFu > 0, AFu > 0 && Design != null ? Design.CFs / AFu : 0),
                ["AFu"] = AFu,
                ["underground_CFs"] = UndergroundStructure,
                ["standard"] = new JObject
                {
                    ["I"] = n(p.I), ["SaD"] = n(p.SaD), ["Fu"] = n(p.Fu), ["L"] = n(p.L), ["D0"] = n(p.D0), ["Rs"] = n(p.Rs),
                    ["BH"] = n(b.BH), ["Sp"] = v(standard, d.DesignSp), ["F"] = v(standard, d.DesignF),
                    ["Rn"] = v(standard, d.Rn), ["LCCR"] = v(standard, d.Lccr), ["C"] = v(standard, d.DesignC), ["W"] = v(standard, d.DesignW),
                    ["baseline_Sp"] = v(hasBaseline, d.BaselineSp), ["baseline_F"] = v(hasBaseline, d.BaselineF),
                    ["baseline_C"] = v(hasBaseline, d.BaselineC), ["baseline_W"] = v(hasBaseline, d.BaselineW),
                },
                // 只有採估算值時才輸出；沒勾「使用估算」時，專案裡留著的舊估算紀錄不輸出，以免被當成主結構的算法
                ["bim"] = estimated ? (JToken)StructureBim.FromEstimateJson(p.StructureEstimateJson) ?? JValue.CreateNull() : JValue.CreateNull(),
            };
        }
    }

    // ================================================================ 計算引擎

    /// <summary>
    /// 依《2025 低碳建築評估手冊》式 1～27 計算設計案與基準案。
    /// 不使用 Revit API，只讀數量格式、輸出 LebrResult，可直接用附錄三做自動測試。
    /// 計算順序見計畫書「公式運算邏輯」分頁第 1～8 步。
    /// </summary>
    public class LebrCalculator
    {
        private static readonly string[] GroupOrder = { "OW", "W", "CW", "IW", "F", "P" };
        private static readonly Dictionary<string, string> GroupLabels = new Dictionary<string, string>
        {
            { "OW", "一般外牆外裝" }, { "W", "外窗與透光帷幕外窗" }, { "CWW", "外窗與透光帷幕外窗" },
            { "CW", "不透光帷幕外牆及一般外牆" }, { "IW", "內隔間" }, { "F", "室內地坪" }, { "P", "戶外地坪" },
        };

        private readonly LebrDatabase _db;

        public LebrCalculator(LebrDatabase db)
        {
            _db = db;
        }

        public LebrResult Calculate(QuantityInput input)
        {
            var b = input.Building ?? new BuildingGeometry();
            var p = input.Project ?? new ProjectSettings();
            var issues = new List<LebrIssue>();
            var result = new LebrResult
            {
                Source = input.Source,
                DatabaseVersion = _db.Meta.ManualVersion,
                DatabaseReviewStatus = _db.Meta.ReviewStatus,
                CalculatedAt = DateTime.Now,
                Issues = issues,
                Unmapped = new List<UnmappedElement>(input.Unmapped ?? new List<UnmappedElement>()),
                Items = new List<ItemResult>(),
                Structure = new StructureDetail(),
                Design = new ScenarioResult(),
                Baseline = new ScenarioResult(),
                Stages = new List<StageRow>(),
                Groups = new List<GroupRow>(),
                GradeScale = new List<GradeMark>(),
                WearClass = p.WearClass,
                InputBuilding = b,
                InputProject = p,
                LL = p.LL,
            };

            if (!_db.IsReviewed)
                Add(issues, IssueLevel.Warning, "資料庫尚未完成人工核對（review_status = " + _db.Meta.ReviewStatus + "），結果僅供參考。");
            foreach (var note in input.Notes ?? new List<string>())
                Add(issues, IssueLevel.Info, note);

            if (b.AFu == null || b.AFu <= 0 || b.S == null || b.S <= 0)
            {
                Add(issues, IssueLevel.Error, "缺少地上樓地板面積 AFu 或地上樓層數 S，無法計算。");
                result.Complete = false;
                return result;
            }
            double afu = b.AFu.Value, s = b.S.Value, afb = b.AFb ?? 0, sb = b.Sb ?? 0;
            result.AFu = afu; result.S = s; result.AFb = afb; result.Sb = sb;

            var structure = string.IsNullOrWhiteSpace(p.Structure) ? null : p.Structure.Trim();
            if (structure == null)
            {
                Add(issues, IssueLevel.Warning, "未設定構造類別，暫以 RC 構造計算（表 4-1、表 4-7）。");
                structure = "RC";
            }
            var renewalMultiplier = RenewalMultiplier(structure);

            // 第 2 步：非結構構件
            foreach (var q in input.Components ?? new List<ComponentQuantity>())
            {
                var item = ResolveItem(q, p.WearClass, renewalMultiplier, issues, result.Unmapped);
                if (item == null) continue;
                result.Items.Add(item);
                result.Design.CFns += item.DesignNew;
                result.Design.CFrm += item.DesignRenew;
                result.Baseline.CFns += item.BaselineNew;
                result.Baseline.CFrm += item.BaselineRenew;
            }
            if (result.Unmapped.Count > 0)
                Add(issues, IssueLevel.Warning, result.Unmapped.Count + " 個元件沒有對應到資料庫，未計入碳排（見「未對應」）。");

            // 第 1 步：主結構
            result.Complete = CalculateStructure(b, p, structure, result, issues);

            // 第 3～5 步：施工與拆除（以設計案 CFum、CFrm 計算一次，兩案相同）
            var k = result.Design.CFum > 0 ? 1.0 + result.Design.CFrm / result.Design.CFum : 1.0;
            var prm = _db.Parameters;
            var cfc = (prm.Constant("construction_above_CFc", "a") + prm.Constant("construction_above_CFc", "b") * s) * afu * k;
            var wd = WasteDensity(structure, p.UseType, issues);
            var cfd = (prm.Constant("demolition_above", "d_a") * s + prm.Constant("demolition_above", "d_b")) * afu;
            var cfwa = prm.Constant("demolition_above", "wa_coef") * wd * afu;
            var cfdw = (cfd + cfwa) * k;
            result.Design.CFc = result.Baseline.CFc = cfc;
            result.Design.CFdw = result.Baseline.CFdw = cfdw;

            // 第 6 步：加總（式 20、21）
            var ll = p.LL;
            var llMax = prm.LifeExtensionLL != null ? prm.LifeExtensionLL.MaxTotal : 0.08;
            if (ll > llMax)
            {
                Add(issues, IssueLevel.Warning, "延壽優惠係數 LL = " + ll + " 超過上限 " + llMax + "，以上限計算（表 5-2）。");
                ll = llMax;
            }
            result.LL = ll;
            result.Design.Reductions = p.ReuseReduction + p.LowCarbonMaterialReduction + p.LowCarbonMethodReduction;
            result.EECc = result.Baseline.Subtotal;
            result.EEC = (result.Design.Subtotal - result.Design.Reductions) / (1.0 + ll);

            // 第 7 步：比較與分級（式 24～27、表 5-1、表 6-1）
            result.ECI = result.EEC / afu;
            result.ECIs = result.EECc / afu;
            if (result.EECc > 0)
            {
                result.CFR = result.DeltaCF / result.EECc;
                result.Grade = _db.GradeFor(result.CFR.Value);
            }
            foreach (var g in prm.GradeScale.Grades)
                result.GradeScale.Add(new GradeMark { Grade = g.Grade, Eci = g.ScaleFactor.HasValue ? result.ECIs * g.ScaleFactor.Value : (double?)null });

            // 第 8 步：地下層（只揭露，不評分）
            if (afb > 0)
            {
                result.UndergroundStructure = prm.Constant("basement_structure_CFs_prime", "afb_coef") * afb
                                              + prm.Constant("basement_structure_CFs_prime", "total_coef") * (afu + afb);
                result.UndergroundConstruction = (prm.Constant("construction_below_CFc_prime", "a")
                                                  + prm.Constant("construction_below_CFc_prime", "b") * sb) * (afu + afb);
                result.UndergroundDemolition = (prm.Constant("demolition_below", "d_a") * sb + prm.Constant("demolition_below", "d_b")) * afb
                                               + prm.Constant("demolition_below", "wa_coef") * wd * afb;
            }

            BuildSummaries(result);
            return result;
        }

        // ------------------------------------------------------------ 第 1 步：主結構（式 1～7）

        private bool CalculateStructure(BuildingGeometry b, ProjectSettings p, string structure, LebrResult result, List<LebrIssue> issues)
        {
            var missing = new List<string>();
            if (p.I == null) missing.Add("用途係數 I");
            if (p.SaD == null) missing.Add("SaD");
            if (p.Fu == null) missing.Add("Fu");
            if (p.L == null) missing.Add("活載重 L");
            if (p.D0 == null) missing.Add("額外靜載重 D0");
            if (b.BH == null) missing.Add("一樓樓高 BH");
            if (missing.Count > 0)
            {
                Add(issues, IssueLevel.Error, "主結構未計算，缺少：" + string.Join("、", missing) + "。等級僅供參考。");
                return false;
            }
            var rs = p.Rs;
            if (rs == null)
            {
                Add(issues, IssueLevel.Warning, "未設定靜力分析折減係數 Rs，以 1.0 計算。");
                rs = 1.0;
            }

            double afu = b.AFu.Value, s = b.S.Value;
            var floors = (int)Math.Round(s);
            var d = result.Structure;

            // 基準案：只依樓層與 AFa 查表 4-6，不讀取設計案的 Sp、F；基準案 EBF = 0（Rn = 1.0）
            d.BaselineW = _db.BaselineW(floors);
            d.BaselineSp = _db.BaselineSp(floors, afu / s);
            d.BaselineF = _db.BaselineShapeF(floors);
            d.BaselineRn = 1.0;
            d.BaselineC = StructureC(s, p.I.Value, p.SaD.Value, p.Fu.Value, d.BaselineSp, p.D0.Value, p.L.Value, b.BH.Value, rs.Value, d.BaselineF);
            result.Baseline.CFs = afu * d.BaselineC * d.BaselineW * d.BaselineRn;

            // 設計案：結構碳排估算值有值時直接採用（介面規格第四節）
            if (p.StructureCFs.HasValue && p.StructureCFs.Value > 0)
            {
                result.Design.CFs = p.StructureCFs.Value;
                d.Computed = true;
                Add(issues, IssueLevel.Info, "設計案主結構 CFs 採用結構碳排估算值（LEBR_CFs），未以 Sp、F 計算。");
                return true;
            }

            // 設計案：Sp、F 缺少只影響設計案
            if (b.Sp == null || b.F == null)
            {
                var designMissing = new List<string>();
                if (b.Sp == null) designMissing.Add("跨距變化 Sp");
                if (b.F == null) designMissing.Add("形狀係數 F");
                Add(issues, IssueLevel.Error, "設計案主結構未計算，缺少：" + string.Join("、", designMissing) + "。基準案仍已計算。等級僅供參考。");
                return false;
            }
            d.DesignW = _db.StructureFactorW(structure);
            d.DesignSp = b.Sp.Value;
            d.DesignF = b.F.Value;
            var lowCarbonConcrete = structure == "RC" || structure == "SRC" || structure == "S";
            d.Lccr = lowCarbonConcrete ? 1.0 - p.CSER * _db.Parameters.Constant("low_carbon_concrete_LCCR", "cser_coef") : 1.0;
            d.Rn = (afu - p.EBF) / afu;
            d.DesignC = StructureC(s, p.I.Value, p.SaD.Value, p.Fu.Value, d.DesignSp, p.D0.Value, p.L.Value, b.BH.Value, rs.Value, d.DesignF);
            result.Design.CFs = afu * d.DesignC * d.DesignW * d.Lccr * d.Rn;
            d.Computed = true;
            return true;
        }

        /// <summary>式 4：地上層主結構單位樓地板面積碳排密度 C（kgCO2e/m2）。</summary>
        private double StructureC(double s, double i, double sad, double fu, double sp, double d0, double l, double bh, double rs, double f)
        {
            Func<string, double> k = name => _db.Parameters.Constant("structure_C", name);
            var c = (k("base") + k("s_coef") * (s - k("s_ref")) + k("seismic_coef") * (i * sad / fu - k("seismic_ref"))
                     + k("sp_coef") * (sp - k("sp_ref")) + k("d0_coef") * (d0 - k("d0_ref")) + k("l_coef") * (l - k("l_ref"))
                     + k("bh_coef") * (bh - k("bh_ref"))) * rs * f;
            return Math.Max(c, k("min_C"));
        }

        // ------------------------------------------------------------ 第 2 步：構件對應

        private ItemResult ResolveItem(ComponentQuantity q, string wearClass, double renewalMultiplier,
                                       List<LebrIssue> issues, List<UnmappedElement> unmapped)
        {
            if (q.AreaM2 <= 0) return null;
            if (q.Custom != null)
            {
                Add(issues, IssueLevel.Info, "「" + (q.Name ?? q.Code) + "」使用自訂係數（" + (q.Custom.Basis ?? "未註明依據") + "），送審需附計算資料。");
                return new ItemResult
                {
                    Code = q.Code, Name = q.Name, Group = q.Custom.Group, GroupLabel = LabelOf(q.Custom.Group), AreaM2 = q.AreaM2,
                    FNew = q.Custom.FNew, FRenew = q.Custom.FRenew,
                    DesignNew = q.AreaM2 * q.Custom.FNew, DesignRenew = q.AreaM2 * q.Custom.FRenew * renewalMultiplier,
                    BaselineCode = "（自訂）", BaselineName = "自訂基準",
                    BaselineNew = q.AreaM2 * q.Custom.BaselineFNew, BaselineRenew = q.AreaM2 * q.Custom.BaselineFRenew * renewalMultiplier,
                    ElementIds = q.ElementIds,
                };
            }

            var c = ResolveCode(q.Code, wearClass);
            if (c == null)
            {
                unmapped.Add(new UnmappedElement
                {
                    Code = q.Code, TypeName = q.Name, ElementId = q.ElementIds != null && q.ElementIds.Count > 0 ? q.ElementIds[0] : (long?)null,
                    Reason = string.IsNullOrWhiteSpace(q.Code) ? "類型沒有填 LEBR_Code" : "資料庫找不到代號 " + q.Code,
                });
                return null;
            }
            if (c.WearClass != null && c.WearClass != "S" && wearClass != null && c.WearClass != wearClass)
                Add(issues, IssueLevel.Warning, "代號 " + c.Code + " 的建築類別與專案設定（" + wearClass + "）不同，請確認。");

            var baseline = _db.GetBaseline(c);
            return new ItemResult
            {
                Code = c.Code, Name = string.IsNullOrWhiteSpace(q.Name) ? c.Name : q.Name, Group = c.Group, GroupLabel = LabelOf(c.Group),
                AreaM2 = q.AreaM2, FNew = c.FNew, FRenew = c.FRenewFor(renewalMultiplier),
                DesignNew = q.AreaM2 * c.FNew, DesignRenew = q.AreaM2 * c.FRenewFor(renewalMultiplier),
                BaselineCode = baseline.Code, BaselineName = baseline.Name,
                BaselineNew = q.AreaM2 * baseline.FNew, BaselineRenew = q.AreaM2 * baseline.FRenewFor(renewalMultiplier),
                ElementIds = q.ElementIds,
            };
        }

        /// <summary>代號可含或不含建築類別：IW-03 + 中耗損 → IW-M-03；F-G01 + 低耗損 → F-L-G01。</summary>
        private BuildingComponent ResolveCode(string code, string wearClass)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            BuildingComponent c;
            if (_db.TryGetComponent(code, out c)) return c;
            var dash = code.IndexOf('-');
            if (wearClass != null && dash > 0)
            {
                var full = code.Substring(0, dash) + "-" + wearClass + code.Substring(dash);
                if (_db.TryGetComponent(full, out c)) return c;
            }
            return null;
        }

        // ------------------------------------------------------------ 查表

        private double RenewalMultiplier(string structure)
        {
            var m = _db.Parameters.LifeCycleRenewal != null ? _db.Parameters.LifeCycleRenewal.StructureMultiplier : null;
            double v;
            return m != null && m.TryGetValue(structure, out v) ? v : 1.0;
        }

        private double WasteDensity(string structure, string useType, List<LebrIssue> issues)
        {
            if (string.IsNullOrWhiteSpace(useType))
            {
                Add(issues, IssueLevel.Warning, "未設定建築用途類別，廢棄物量 Wd 以「其他非住宿類」計算（表 4-7）。");
                useType = "other";
            }
            try
            {
                return _db.WasteDensityWd(structure, useType);
            }
            catch (KeyNotFoundException)
            {
                Add(issues, IssueLevel.Warning, "表 4-7 沒有 " + structure + " / " + useType + "，Wd 以 RC 其他類計算。");
                return _db.WasteDensityWd("RC", "other");
            }
        }

        private static string LabelOf(string group)
        {
            string label;
            return group != null && GroupLabels.TryGetValue(group, out label) ? label : group;
        }

        private static void Add(List<LebrIssue> issues, IssueLevel level, string message)
        {
            issues.Add(new LebrIssue { Level = level, Message = message });
        }

        // ------------------------------------------------------------ 表 6-2 的兩張組成表

        private static void BuildSummaries(LebrResult r)
        {
            var designTotal = r.Design.Subtotal;
            Func<double, double> share = v => designTotal > 0 ? v / designTotal : 0;
            r.Stages.Add(new StageRow { Stage = "資材製造運輸", Design = r.Design.CFum, Baseline = r.Baseline.CFum, DesignShare = share(r.Design.CFum) });
            r.Stages.Add(new StageRow { Stage = "施工", Design = r.Design.CFc, Baseline = r.Baseline.CFc, DesignShare = share(r.Design.CFc) });
            r.Stages.Add(new StageRow { Stage = "更新修繕", Design = r.Design.CFrm, Baseline = r.Baseline.CFrm, DesignShare = share(r.Design.CFrm) });
            r.Stages.Add(new StageRow { Stage = "拆除廢棄", Design = r.Design.CFdw, Baseline = r.Baseline.CFdw, DesignShare = share(r.Design.CFdw) });
            if (r.Design.Reductions > 0)
                r.Stages.Add(new StageRow { Stage = "減碳優惠（扣除）", Design = -r.Design.Reductions, Baseline = 0, DesignShare = share(-r.Design.Reductions) });

            // 分項工程只含資材（新建 + 更新），與手冊表 6-2 下半部相同
            var componentTotal = r.Design.CFum + r.Design.CFrm;
            Func<double, double> cshare = v => componentTotal > 0 ? v / componentTotal : 0;
            r.Groups.Add(new GroupRow { Key = "structure", Label = "主結構體", Design = r.Design.CFs, Baseline = r.Baseline.CFs, DesignShare = cshare(r.Design.CFs) });
            foreach (var g in GroupOrder)
            {
                var items = r.Items.Where(i => i.Group == g || (g == "W" && i.Group == "CWW")).ToList();
                var design = items.Sum(i => i.Design);
                r.Groups.Add(new GroupRow { Key = g, Label = LabelOf(g), Design = design, Baseline = items.Sum(i => i.Baseline), DesignShare = cshare(design) });
            }
        }
    }
}
