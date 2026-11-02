using System;
using System.Collections.Generic;
using System.Linq;

namespace LebrCarbon
{
    /// <summary>
    /// 估算的一筆數量。混凝土（Concrete）以 m3 計，對應附錄 L；鋼筋（Rebar）以 kg 計，對應附錄 M。
    /// 樓層以名稱與標高分組；標高小於 0 為地下層，其餘為地上層（規則 1）。
    /// </summary>
    public class EstimateItem
    {
        /// <summary>來源品類（例如 StructuralColumns），只用於結果說明。</summary>
        public string Source { get; set; }
        public string Level { get; set; }
        public double ElevationM { get; set; }
        public double Quantity { get; set; }
        public string Unit { get; set; }
        public string MaterialId { get; set; }
        public bool IsRebar { get; set; }
    }

    /// <summary>單一樓層的碳排量（kgCO2e）。</summary>
    public class LevelCarbon
    {
        public string Level { get; set; }
        public double ElevationM { get; set; }
        public bool IsBelowGround { get; set; }
        public double ConcreteM3 { get; set; }
        public double RebarKg { get; set; }
        /// <summary>混凝土部分 An × L。</summary>
        public double CFc { get; set; }
        /// <summary>鋼筋部分 Bn × M。</summary>
        public double CFr { get; set; }
        /// <summary>CFsn = An × L + Bn × M。</summary>
        public double CFsn { get { return CFc + CFr; } }
    }

    public class EstimateResult
    {
        public List<LevelCarbon> Levels { get; set; }
        public double Above { get; set; }
        public double Below { get; set; }
        /// <summary>最終 CFs：地上與地下分別加總後合計。</summary>
        public double CFs { get { return Above + Below; } }
        public List<string> Notes { get; set; }
    }

    /// <summary>
    /// 結構碳排估算（結構碳排估算公式.md）。不依賴 Revit API。
    /// 規則：CFsn = An × L + Bn × M；地上與地下各自加總後合計。
    /// 無鋼筋的樓層 Bn = 0 並註明；缺少係數或單位不符時直接報錯，不靜默以 0 代替。
    /// </summary>
    public class StructureEstimator
    {
        private readonly PLccTable _table;

        public StructureEstimator(PLccTable table)
        {
            if (table == null) throw new ArgumentNullException("table");
            _table = table;
        }

        public EstimateResult Calculate(IEnumerable<EstimateItem> items)
        {
            var notes = new List<string>();
            var levels = new Dictionary<string, LevelCarbon>(StringComparer.Ordinal);

            foreach (var item in items ?? Enumerable.Empty<EstimateItem>())
            {
                if (item.Quantity < 0)
                    throw new InvalidOperationException("數量不可為負：" + item.Source + "（" + item.Level + "）");
                if (item.Quantity == 0) continue;

                var material = _table.Get(item.MaterialId);
                if (!material.TotalA1A4.HasValue)
                    throw new InvalidOperationException("附錄一 " + material.Id + " 沒有總碳排量，無法估算");

                LevelCarbon level;
                if (!levels.TryGetValue(item.Level, out level))
                {
                    level = new LevelCarbon
                    {
                        Level = item.Level,
                        ElevationM = item.ElevationM,
                        IsBelowGround = item.ElevationM < 0,
                    };
                    levels[item.Level] = level;
                }
                else if (Math.Abs(level.ElevationM - item.ElevationM) > 1e-9)
                {
                    throw new InvalidOperationException("樓層 " + item.Level + " 的標高不一致");
                }

                if (item.IsRebar)
                {
                    if (material.Unit != "kg")
                        throw new InvalidOperationException("鋼筋係數 " + material.Id + " 單位應為 kg，實際為 " + material.Unit);
                    if (item.Unit != "kg")
                        throw new InvalidOperationException("鋼筋數量單位應為 kg：" + item.Source + "（" + item.Level + "）");
                    level.RebarKg += item.Quantity;
                    level.CFr += item.Quantity * material.TotalA1A4.Value;
                }
                else
                {
                    if (material.Unit != "m3")
                        throw new InvalidOperationException("混凝土係數 " + material.Id + " 單位應為 m3，實際為 " + material.Unit);
                    if (item.Unit != "m3")
                        throw new InvalidOperationException("混凝土數量單位應為 m3：" + item.Source + "（" + item.Level + "）");
                    level.ConcreteM3 += item.Quantity;
                    level.CFc += item.Quantity * material.TotalA1A4.Value;
                }

                if (material.ManualIssue != null)
                    notes.Add(material.Id + "（" + material.Name + "）：" + material.ManualIssue + "，請確認採用的數值");
            }

            var ordered = levels.Values.OrderBy(l => l.ElevationM).ToList();
            foreach (var level in ordered)
            {
                if (level.ConcreteM3 > 0 && level.RebarKg <= 0)
                    notes.Add("「" + level.Level + "」沒有鋼筋，Bn = 0");
            }
            if (ordered.Count == 0)
                notes.Add("沒有可計算的數量，CFs = 0");

            return new EstimateResult
            {
                Levels = ordered,
                Above = ordered.Where(l => !l.IsBelowGround).Sum(l => l.CFsn),
                Below = ordered.Where(l => l.IsBelowGround).Sum(l => l.CFsn),
                Notes = notes.Distinct().ToList(),
            };
        }
    }
}
