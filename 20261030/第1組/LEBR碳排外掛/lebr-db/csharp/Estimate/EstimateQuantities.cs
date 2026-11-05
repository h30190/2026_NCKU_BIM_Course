using System;
using System.Collections.Generic;
using System.Linq;

namespace LebrCarbon
{
    /// <summary>
    /// Revit 擷取的一列數量（依來源品類與樓層彙總）。Revit 端只負責讀取，不決定材料。
    /// </summary>
    public class EstimateQuantityRow
    {
        /// <summary>來源品類：StructuralColumns、StructuralFraming、Floors、Walls、Rebar。</summary>
        public string Source { get; set; }
        public string Level { get; set; }
        public double ElevationM { get; set; }
        public double Quantity { get; set; }
        /// <summary>混凝土為 m3，鋼筋為 kg。</summary>
        public string Unit { get; set; }
        public bool IsRebar { get; set; }
    }

    public static class EstimateQuantities
    {
        /// <summary>
        /// 依使用者在估算視窗選擇的材料（來源品類 → 附錄一材料編號）組成估算輸入。
        /// 沒有選擇材料的來源品類報錯，不以預設值代替。
        /// </summary>
        public static List<EstimateItem> ToItems(IEnumerable<EstimateQuantityRow> rows, IDictionary<string, string> materialBySource)
        {
            if (rows == null) throw new ArgumentNullException("rows");
            if (materialBySource == null) throw new ArgumentNullException("materialBySource");

            var items = new List<EstimateItem>();
            foreach (var row in rows)
            {
                string materialId;
                if (!materialBySource.TryGetValue(row.Source, out materialId) || string.IsNullOrWhiteSpace(materialId))
                    throw new InvalidOperationException("「" + row.Source + "」尚未選擇對應材料");

                items.Add(new EstimateItem
                {
                    Source = row.Source,
                    Level = row.Level,
                    ElevationM = row.ElevationM,
                    Quantity = row.Quantity,
                    Unit = row.Unit,
                    MaterialId = materialId,
                    IsRebar = row.IsRebar,
                });
            }
            return items;
        }

        /// <summary>估算用的來源品類清單（順序即下拉選單顯示順序）。</summary>
        public static readonly string[] Sources =
        {
            "StructuralColumns", "StructuralFraming", "Floors", "Walls", "Rebar",
        };

        /// <summary>預設材料：混凝土 PLCC-042、鋼筋 PLCC-003。估算視窗的初始選項，也是「開始計算」自動估算時沒選過材料的品類所用。</summary>
        public static readonly Dictionary<string, string> DefaultMaterials = new Dictionary<string, string>
        {
            { "StructuralColumns", "PLCC-042" },
            { "StructuralFraming", "PLCC-042" },
            { "Floors", "PLCC-042" },
            { "Walls", "PLCC-042" },
            { "Rebar", "PLCC-003" },
        };
    }
}
