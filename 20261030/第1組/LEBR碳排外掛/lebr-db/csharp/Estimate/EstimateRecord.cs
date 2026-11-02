using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace LebrCarbon
{
    /// <summary>
    /// 估算紀錄（LEBR_EstimateJson，介面規格第三節）。只組成 JSON 字串與讀回摘要，不依賴 Revit API。
    /// </summary>
    public static class EstimateRecord
    {
        public const int Version = 1;

        /// <param name="items">各品類 × 樓層的數量（估算視窗的輸入）。有給時存成 rows，供網頁的數量報告與碳排報告使用。</param>
        /// <param name="table">附錄一；有給時把用到的材料係數存成 materials，網頁不必另讀資料庫。</param>
        public static string ToJson(EstimateResult result, IDictionary<string, string> materialBySource, string modelName, DateTime calculatedAt,
            IEnumerable<EstimateItem> items = null, PLccTable table = null)
        {
            if (result == null) throw new ArgumentNullException("result");

            var rows = new JArray();
            var used = new List<string>();
            foreach (var it in items ?? new EstimateItem[0])
            {
                if (it.Quantity <= 0) continue;
                rows.Add(new JObject
                {
                    ["source"] = it.Source,
                    ["level"] = it.Level,
                    ["elevation_m"] = it.ElevationM,
                    ["below_ground"] = it.ElevationM < 0,
                    ["quantity"] = it.Quantity,
                    ["unit"] = it.Unit,
                    ["material_id"] = it.MaterialId,
                    ["is_rebar"] = it.IsRebar,
                });
                if (it.MaterialId != null && !used.Contains(it.MaterialId)) used.Add(it.MaterialId);
            }

            var materials = new JArray();
            if (table != null)
            {
                foreach (var id in used)
                {
                    var m = table.Get(id);
                    materials.Add(new JObject { ["id"] = m.Id, ["name"] = m.Name, ["unit"] = m.Unit, ["total"] = m.TotalA1A4, ["manual_issue"] = m.ManualIssue });
                }
            }

            var levels = new JArray();
            foreach (var l in result.Levels)
            {
                levels.Add(new JObject
                {
                    ["name"] = l.Level,
                    ["elevation_m"] = l.ElevationM,
                    ["below_ground"] = l.IsBelowGround,
                    ["concrete_m3"] = l.ConcreteM3,
                    ["rebar_kg"] = l.RebarKg,
                    ["cfc"] = l.CFc,
                    ["cfr"] = l.CFr,
                    ["cfs_n"] = l.CFsn,
                });
            }

            var selections = new JArray();
            if (materialBySource != null)
            {
                foreach (var pair in materialBySource)
                    selections.Add(new JObject { ["source"] = pair.Key, ["material_id"] = pair.Value });
            }

            var notes = new JArray();
            foreach (var n in result.Notes) notes.Add(n);

            var root = new JObject
            {
                ["version"] = Version,
                ["calculated_at"] = calculatedAt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                ["model"] = modelName ?? "",
                ["levels"] = levels,
                ["selections"] = selections,
                ["rows"] = rows,
                ["materials"] = materials,
                ["rebar_sources"] = new JObject
                {
                    ["structure_rebar"] = "Rebar（主體為結構柱、結構構架）＋ OST_AreaRein／OST_PathRein 系統內鋼筋（樓板）",
                    ["mass_method"] = "體積 × 7850 kg/m3",
                },
                ["result"] = new JObject
                {
                    ["above"] = result.Above,
                    ["below"] = result.Below,
                    ["cfs"] = result.CFs,
                },
                ["notes"] = notes,
            };
            return root.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        /// <summary>從 LEBR_EstimateJson 讀回地上、地下與合計；格式不符時回傳 null。</summary>
        public static bool TryReadSummary(string json, out double above, out double below, out double cfs)
        {
            above = below = cfs = 0;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var result = JObject.Parse(json)["result"] as JObject;
                if (result == null) return false;
                above = result.Value<double>("above");
                below = result.Value<double>("below");
                cfs = result.Value<double>("cfs");
                return true;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return false;
            }
        }
    }
}
