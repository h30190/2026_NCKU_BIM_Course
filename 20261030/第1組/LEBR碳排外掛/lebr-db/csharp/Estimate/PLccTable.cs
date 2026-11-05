using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LebrCarbon
{
    /// <summary>附錄一 P-LCC 的一筆材料，只保留估算用欄位。</summary>
    public class PLccMaterial
    {
        public string Id { get; set; }
        public string Category { get; set; }
        public string Name { get; set; }
        public string Unit { get; set; }

        /// <summary>總碳排量（A1～A4，kgCO2e／單位）。空白代表手冊沒有數值，估算時會報錯。</summary>
        public double? TotalA1A4 { get; set; }

        /// <summary>手冊數字不一致的說明（不是抽取錯誤）；有值的材料在結果中會註明。</summary>
        public string ManualIssue { get; set; }
    }

    /// <summary>
    /// 讀取「附錄一_P-LCC_2022.csv」。估算只使用 total_a1_a4 欄位，依計畫書規則 5。
    /// </summary>
    public class PLccTable
    {
        private readonly Dictionary<string, PLccMaterial> _byId;

        private PLccTable(IList<PLccMaterial> materials)
        {
            Materials = new List<PLccMaterial>(materials);
            _byId = new Dictionary<string, PLccMaterial>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in materials)
            {
                if (_byId.ContainsKey(m.Id))
                    throw new InvalidOperationException("附錄一材料編號重複：" + m.Id);
                _byId[m.Id] = m;
            }
        }

        public IReadOnlyList<PLccMaterial> Materials { get; private set; }

        public static PLccTable Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("找不到附錄一 CSV：" + path, path);

            var lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length < 2)
                throw new InvalidOperationException("附錄一 CSV 沒有資料：" + path);

            var header = SplitCsv(lines[0]);
            var iId = Column(header, "id");
            var iCategory = Column(header, "category");
            var iName = Column(header, "name");
            var iUnit = Column(header, "unit");
            var iTotal = Column(header, "total_a1_a4");
            var iIssue = Column(header, "manual_issue");

            var list = new List<PLccMaterial>();
            for (var i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var f = SplitCsv(lines[i]);
                if (f.Count != header.Count)
                    throw new InvalidOperationException("附錄一第 " + (i + 1) + " 行欄位數與表頭不符");

                double total;
                var hasTotal = double.TryParse(f[iTotal], NumberStyles.Float, CultureInfo.InvariantCulture, out total);
                list.Add(new PLccMaterial
                {
                    Id = f[iId],
                    Category = f[iCategory],
                    Name = f[iName],
                    Unit = f[iUnit],
                    TotalA1A4 = hasTotal ? total : (double?)null,
                    ManualIssue = string.IsNullOrWhiteSpace(f[iIssue]) ? null : f[iIssue],
                });
            }
            return new PLccTable(list);
        }

        /// <summary>依材料編號取得；找不到時報錯，不以預設值代替。</summary>
        public PLccMaterial Get(string id)
        {
            PLccMaterial m;
            if (id == null || !_byId.TryGetValue(id, out m))
                throw new InvalidOperationException("附錄一沒有材料編號 " + (id ?? "(空白)"));
            return m;
        }

        private static int Column(IList<string> header, string name)
        {
            for (var i = 0; i < header.Count; i++)
                if (string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
            throw new InvalidOperationException("附錄一 CSV 缺少欄位：" + name);
        }

        /// <summary>簡單 CSV 切欄，支援雙引號包住的欄位（含逗號）。</summary>
        internal static List<string> SplitCsv(string line)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else sb.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            result.Add(sb.ToString());
            return result;
        }
    }
}
