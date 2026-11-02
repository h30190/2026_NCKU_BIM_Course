using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace LebrCarbon
{
    /// <summary>
    /// 「開始計算」時依目前模型自動估算主結構：沿用上次選的附錄一材料（沒選過的品類用預設材料），數量重新從模型讀。
    /// 結果由「開始計算」寫回專案資訊（LEBR_CFs、LEBR_EstimateJson）。不依賴 Revit API。
    /// </summary>
    public static class EstimateRefresh
    {
        /// <summary>差距小於這個值（kgCO2e）視為沒變。</summary>
        public const double Tolerance = 0.5;

        /// <summary>從估算紀錄讀回上次選的材料（品類 → 附錄一編號）；沒有紀錄或格式不符時回傳 null。</summary>
        public static Dictionary<string, string> ReadSelections(string estimateJson)
        {
            if (string.IsNullOrWhiteSpace(estimateJson)) return null;
            try
            {
                var arr = JObject.Parse(estimateJson)["selections"] as JArray;
                if (arr == null || arr.Count == 0) return null;
                var map = new Dictionary<string, string>();
                foreach (var s in arr.OfType<JObject>())
                {
                    var source = (string)s["source"];
                    var id = (string)s["material_id"];
                    if (!string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(id)) map[source] = id;
                }
                return map.Count > 0 ? map : null;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }
        }

        /// <summary>上次的材料選擇補上預設材料（selections 可為 null）；回傳新的字典與補了哪些品類。</summary>
        public static Dictionary<string, string> WithDefaults(IDictionary<string, string> selections, out List<string> defaulted)
        {
            var map = selections == null ? new Dictionary<string, string>() : new Dictionary<string, string>(selections);
            defaulted = new List<string>();
            foreach (var kv in EstimateQuantities.DefaultMaterials)
            {
                string id;
                if (map.TryGetValue(kv.Key, out id) && !string.IsNullOrWhiteSpace(id)) continue;
                map[kv.Key] = kv.Value;
                defaulted.Add(kv.Key);
            }
            return map;
        }

        /// <summary>
        /// 用目前模型數量與上次的材料重算；沒選過材料的品類（例如第一次估算，或模型新增了鋼筋）用預設材料。
        /// previousCFs 為專案資訊目前記錄的值，沒有記錄時為 null。
        /// </summary>
        public static RefreshResult Recompute(IEnumerable<EstimateQuantityRow> rows, IDictionary<string, string> selections,
            PLccTable table, string modelName, DateTime calculatedAt, double? previousCFs)
        {
            if (table == null) throw new ArgumentNullException("table");
            var rowList = rows == null ? new List<EstimateQuantityRow>() : rows.ToList();
            List<string> defaulted;
            var used = WithDefaults(selections, out defaulted);
            // 只記錄模型裡有數量的品類，估算紀錄的 selections 才不會列出沒用到的材料
            var present = new HashSet<string>(rowList.Select(r => r.Source));
            used = used.Where(kv => present.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            var items = EstimateQuantities.ToItems(rowList, used);
            var result = new StructureEstimator(table).Calculate(items);
            var json = EstimateRecord.ToJson(result, used, modelName, calculatedAt, items, table);
            var changed = !previousCFs.HasValue || Math.Abs(previousCFs.Value - result.CFs) > Tolerance;
            return new RefreshResult
            {
                CFs = result.CFs, EstimateJson = json, Changed = changed, PreviousCFs = previousCFs,
                DefaultedSources = defaulted.Where(present.Contains).ToList(),
            };
        }

        /// <summary>給計算前檢查與結果 JSON 的說明文字。</summary>
        public static string Describe(RefreshResult r)
        {
            var now = r.CFs.ToString("N0", CultureInfo.InvariantCulture);
            var hasDefaults = r.DefaultedSources != null && r.DefaultedSources.Count > 0;
            var defaults = hasDefaults
                ? "（" + string.Join("、", r.DefaultedSources) + " 用預設材料：混凝土 PLCC-042、鋼筋 PLCC-003，可到「專案設定 → 開啟估算」改）"
                : "（沿用上次選的材料）";
            if (!r.PreviousCFs.HasValue)
                return "已依模型的混凝土、鋼筋量自動估算主結構：" + now + " kgCO2e" + defaults + "，並記錄到專案資訊，記得存檔。";
            if (!r.Changed)
                return "主結構估算已依目前模型重新讀取數量，與專案資訊記錄的相同（" + now + " kgCO2e）" + (hasDefaults ? defaults : "") + "。";
            var before = r.PreviousCFs.Value.ToString("N0", CultureInfo.InvariantCulture);
            return "模型有變動，主結構估算已自動重算：" + before + " → " + now + " kgCO2e" + defaults + "，並更新專案資訊的記錄，記得存檔。";
        }
    }

    public class RefreshResult
    {
        public double CFs { get; set; }
        public string EstimateJson { get; set; }
        public bool Changed { get; set; }
        public double? PreviousCFs { get; set; }
        /// <summary>這次用預設材料的品類。</summary>
        public List<string> DefaultedSources { get; set; }
    }
}
