using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;

namespace LebrCarbon.Reports.Pdf;

/// <summary>電腦上沒有可用的「Microsoft Print to PDF」時丟出，訊息可直接顯示給使用者。</summary>
public sealed class PdfPrinterUnavailableException(string message) : Exception(message);

/// <summary>
/// 把 LebrSummary 排成一頁 A4，經 Windows 內建「Microsoft Print to PDF」寫成 PDF。
/// 版面以公釐為單位；顏色與網頁控制台一致（達門檻綠、未達橙，其餘中性色）。
/// </summary>
public static class PdfSummaryWriter
{
    public const string PrinterName = "Microsoft Print to PDF";

    public static bool IsPrinterAvailable()
    {
        using var doc = new PrintDocument();
        doc.PrinterSettings.PrinterName = PrinterName;
        return doc.PrinterSettings.IsValid;
    }

    /// <summary>寫出 PDF，回傳完整路徑。等候列印佇列寫完檔案（最多 30 秒）。</summary>
    public static string Write(string path, LebrSummary summary)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (File.Exists(full)) File.Delete(full);   // 被其他程式開著時這裡就會丟 IOException，讓呼叫端提示關閉

        using var doc = new PrintDocument { DocumentName = summary.Title };
        doc.PrinterSettings.PrinterName = PrinterName;
        if (!doc.PrinterSettings.IsValid)
            throw new PdfPrinterUnavailableException(
                $"找不到「{PrinterName}」。請在 Windows「設定 → 印表機與掃描器」新增此印表機（Windows 10／11 內建），或改用 Excel 計算書。");

        doc.PrinterSettings.PrintToFile = true;
        doc.PrinterSettings.PrintFileName = full;
        var a4 = doc.PrinterSettings.PaperSizes.Cast<PaperSize>().FirstOrDefault(p => p.Kind == PaperKind.A4);
        if (a4 is not null) doc.DefaultPageSettings.PaperSize = a4;
        doc.DefaultPageSettings.Landscape = false;
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        doc.PrintController = new StandardPrintController();   // 不顯示「正在列印」視窗
        doc.PrintPage += (_, e) =>
        {
            Render(e.Graphics!, summary);
            e.HasMorePages = false;
        };
        doc.Print();

        WaitForFile(full, TimeSpan.FromSeconds(30));
        return full;
    }

    private static void WaitForFile(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        long last = -1;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                var len = new FileInfo(path).Length;
                if (len > 0 && len == last) return;   // 連續兩次大小相同，視為寫完
                last = len;
            }
            Thread.Sleep(250);
        }
        if (!File.Exists(path))
            throw new IOException($"PDF 印表機沒有在 {timeout.TotalSeconds:0} 秒內寫出檔案：{path}");
    }

    // ───────────────────────── 版面 ─────────────────────────

    private const float PageW = 210f, Margin = 16f, ContentW = PageW - 2 * Margin;

    private static readonly Color Ink = ColorTranslator.FromHtml("#17251E");
    private static readonly Color Muted = ColorTranslator.FromHtml("#5E6A63");
    private static readonly Color Rule = ColorTranslator.FromHtml("#D9DDD5");
    private static readonly Color Band = ColorTranslator.FromHtml("#ECEDE8");
    private static readonly Color Good = ColorTranslator.FromHtml("#2E7D4F");
    private static readonly Color GoodSoft = ColorTranslator.FromHtml("#E3EEE6");
    private static readonly Color Warn = ColorTranslator.FromHtml("#B4532A");
    private static readonly Color WarnSoft = ColorTranslator.FromHtml("#FBE9E1");
    // 與控制台相同的五段配色（主結構、非主結構新建、更新修繕、施工、拆除廢棄）
    private static readonly Color[] Series =
        new[] { "#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4" }.Select(ColorTranslator.FromHtml).ToArray();

    private static readonly string Family = PickFamily();

    private static string PickFamily()
    {
        using var installed = new System.Drawing.Text.InstalledFontCollection();
        var names = installed.Families.Select(f => f.Name).ToHashSet();
        foreach (var f in new[] { "Microsoft JhengHei UI", "Microsoft JhengHei", "PMingLiU" })
            if (names.Contains(f)) return f;
        return FontFamily.GenericSansSerif.Name;
    }

    private static Font F(float pt, FontStyle style = FontStyle.Regular) => new(Family, pt, style, GraphicsUnit.Point);

    internal static void Render(Graphics g, LebrSummary s)
    {
        g.PageUnit = GraphicsUnit.Millimeter;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var y = Margin;
        using var title = F(17, FontStyle.Bold);
        using var small = F(8);
        using var body = F(9);
        using var bodyBold = F(9, FontStyle.Bold);
        using var heading = F(10.5f, FontStyle.Bold);

        // 抬頭
        Text(g, s.Title, title, Ink, Margin, y);
        y += 9;
        Text(g, s.Subtitle, small, Muted, Margin, y, ContentW);
        y += 6;
        Line(g, Margin, y, Margin + ContentW, y, Rule, 0.3f);
        y += 5;

        // 等級、CFR、主要數字
        var good = s.MeetsPublicThreshold;
        var badge = new RectangleF(Margin, y, 34, 24);
        Fill(g, badge, good ? GoodSoft : WarnSoft, good ? Good : Warn, 0.5f);
        using (var gradeFont = F(24, FontStyle.Bold))
            TextCenter(g, s.Grade, gradeFont, good ? Good : Warn, badge);
        Text(g, "LEBR 等級", small, Muted, Margin, y + 25);

        var cx = Margin + 40;
        Text(g, "碳排減碳率 CFR", small, Muted, cx, y);
        using (var cfrFont = F(22, FontStyle.Bold)) Text(g, s.CfrText, cfrFont, Ink, cx, y + 4);
        Text(g, "（EECc − EEC）÷ EECc", small, Muted, cx, y + 15);

        // 兩欄三列：欄寬約 45 mm，數字與單位放得下同一行
        var gx = Margin + 88; var colW = (ContentW - 88) / 2f;
        for (var i = 0; i < s.Stats.Count; i++)
        {
            var st = s.Stats[i];
            var x = gx + (i % 2) * colW; var yy = y - 1 + (i / 2) * 9.6f;
            Text(g, st.Label, small, Muted, x, yy, colW - 1);
            using var valueFont = F(11, FontStyle.Bold);
            Text(g, st.Value, valueFont, Ink, x, yy + 3.8f);
            var vw = g.MeasureString(st.Value, valueFont).Width;
            Text(g, st.Unit, small, Muted, x + vw, yy + 5.3f, colW - vw - 1);
        }
        y += 32;

        // 八等級刻度
        Text(g, "八等級刻度（表 12）", heading, Ink, Margin, y);
        y += 8;
        DrawScale(g, s, new RectangleF(Margin, y + 6, ContentW, 8), small, bodyBold);
        y += 26;

        // 四階段
        Text(g, "地上層四階段碳排（kgCO2e）", heading, Ink, Margin, y);
        y += 7;
        y = DrawStages(g, s, y, small, body, bodyBold);
        y += 4;

        // 建築參數
        Text(g, "建築參數", heading, Ink, Margin, y);
        y += 7;
        var half = ContentW / 2f;
        for (var i = 0; i < s.Parameters.Count; i++)
        {
            var (label, value) = s.Parameters[i];
            var x = Margin + (i % 2) * half; var yy = y + (i / 2) * 5.2f;
            Text(g, label, small, Muted, x, yy + 0.4f);
            Text(g, value, body, Ink, x + 36, yy, half - 37);
        }
        y += (s.Parameters.Count + 1) / 2 * 5.2f + 4;

        // 非主結構明細
        Text(g, "非主結構構件（設計案）", heading, Ink, Margin, y);
        y += 7;
        y = DrawItems(g, s, y, 280 - 26 - s.Notes.Count * 4.2f, small, body);

        // 附註與頁尾
        y += 2;
        foreach (var n in s.Notes)
        {
            Text(g, "※ " + n, small, Muted, Margin, y, ContentW);
            y += 4.2f;
        }
        var fy = 297 - Margin + 2;
        Line(g, Margin, fy - 4, Margin + ContentW, fy - 4, Rule, 0.3f);
        Text(g, s.Disclaimer, small, Ink, Margin, fy - 3);
        TextRight(g, s.Footer, small, Muted, Margin + ContentW, fy - 3);
    }

    private static void DrawScale(Graphics g, LebrSummary s, RectangleF bar, Font small, Font bold)
    {
        if (s.ScaleTicks.Count == 0 || s.Ecis <= 0) return;
        const float lo = 0.7f, hi = 1.3f;
        float X(double ratio) => bar.Left + (float)((Math.Clamp(ratio, lo, hi) - lo) / (hi - lo)) * bar.Width;
        var ratios = s.ScaleTicks.Select(t => t.Eci / s.Ecis).ToList();
        var edges = new List<double> { lo }; edges.AddRange(ratios); edges.Add(hi);
        var grades = s.ScaleTicks.Select(t => t.Grade).Append("7級").ToList();

        for (var i = 0; i < grades.Count; i++)
        {
            var x0 = X(edges[i]); var x1 = X(edges[i + 1]);
            var r = new RectangleF(x0 + 0.3f, bar.Top, Math.Max(x1 - x0 - 0.6f, 0.2f), bar.Height);
            var here = grades[i] == s.Grade;
            var c = s.MeetsPublicThreshold ? Good : Warn;
            Fill(g, r, here ? (s.MeetsPublicThreshold ? GoodSoft : WarnSoft) : Band, here ? c : null, 0.4f);
            TextCenter(g, grades[i], here ? bold : small, here ? Ink : Muted, r);
        }
        for (var i = 0; i < ratios.Count; i++)
            TextCenter(g, s.ScaleTicks[i].Eci.ToString("0"), small, Muted, new RectangleF(X(ratios[i]) - 6, bar.Bottom + 0.6f, 12, 4));

        var mx = X(s.Eci / s.Ecis);
        using var b = new SolidBrush(Ink);
        g.FillPolygon(b, new[] { new PointF(mx - 1.6f, bar.Top - 3.2f), new PointF(mx + 1.6f, bar.Top - 3.2f), new PointF(mx, bar.Top - 0.6f) });
        var label = $"本案 ECI {s.Eci:0.00}";
        var w = g.MeasureString(label, small).Width;
        Text(g, label, small, Ink, Math.Clamp(mx - w / 2, bar.Left, bar.Right - w), bar.Top - 7.5f);
        TextRight(g, $"刻度為碳排密度 kgCO2e/m²（基準 ECIs {s.Ecis:0.00}）", small, Muted, bar.Right, bar.Bottom + 5);
    }

    private static float DrawStages(Graphics g, LebrSummary s, float y, Font small, Font body, Font bold)
    {
        // 兩條堆疊長條
        var labelW = 16f; var valueW = 22f; var plotW = ContentW - labelW - valueW;
        var max = Math.Max(s.DesignTotal, s.BaselineTotal);
        if (max <= 0) max = 1;
        var rows = new[] { ("設計案", s.Stages.Select(x => x.Design).ToArray()), ("基準案", s.Stages.Select(x => x.Baseline).ToArray()) };
        foreach (var (name, vals) in rows)
        {
            Text(g, name, body, Ink, Margin, y + 0.6f);
            var x = Margin + labelW;
            for (var i = 0; i < vals.Length; i++)
            {
                var w = (float)(vals[i] / max) * plotW;
                if (w <= 0) continue;
                using var br = new SolidBrush(Series[i]);
                g.FillRectangle(br, x, y, Math.Max(w - 0.4f, 0.2f), 5);
                x += w;
            }
            Text(g, LebrSummaryBuilder.N0(vals.Sum()), bold, Ink, x + 1.5f, y + 0.4f);
            y += 7;
        }
        // 圖例
        var lx = Margin + labelW;
        for (var i = 0; i < s.Stages.Count; i++)
        {
            using var br = new SolidBrush(Series[i]);
            g.FillRectangle(br, lx, y + 0.8f, 2.6f, 2.6f);
            Text(g, s.Stages[i].Label, small, Muted, lx + 3.4f, y);
            lx += g.MeasureString(s.Stages[i].Label, small).Width + 9;
        }
        y += 6;

        // 表格
        float[] cols = { Margin, Margin + 64, Margin + 104, Margin + 144 };
        Text(g, "項目", small, Muted, cols[0], y);
        TextRight(g, "設計案", small, Muted, cols[1] + 30, y);
        TextRight(g, "基準案", small, Muted, cols[2] + 30, y);
        TextRight(g, "差值", small, Muted, cols[3] + 30, y);
        y += 4.6f;
        Line(g, Margin, y, Margin + ContentW, y, Rule, 0.25f);
        y += 0.8f;
        foreach (var r in s.Stages)
        {
            StageLine(g, r.Label, r.Design, r.Baseline, r.Diff, cols, y, body);
            y += 4.6f;
        }
        Line(g, Margin, y, Margin + ContentW, y, Muted, 0.25f);
        y += 0.8f;
        StageLine(g, "合計", s.DesignTotal, s.BaselineTotal, s.DesignTotal - s.BaselineTotal, cols, y, bold);
        return y + 5;
    }

    private static void StageLine(Graphics g, string label, double d, double b, double diff, float[] cols, float y, Font f)
    {
        Text(g, label, f, Ink, cols[0], y);
        TextRight(g, LebrSummaryBuilder.N0(d), f, Ink, cols[1] + 30, y);
        TextRight(g, LebrSummaryBuilder.N0(b), f, Ink, cols[2] + 30, y);
        var sign = Math.Abs(diff) < 0.5 ? "0" : (diff > 0 ? "+" : "−") + LebrSummaryBuilder.N0(Math.Abs(diff));
        TextRight(g, sign, f, Math.Abs(diff) < 0.5 ? Muted : diff < 0 ? Good : Warn, cols[3] + 30, y);
    }

    private static float DrawItems(Graphics g, LebrSummary s, float y, float bottomLimit, Font small, Font body)
    {
        // 左側三欄的起點與寬度；右側四個數字欄用右邊界對齊
        float idX = Margin, catX = Margin + 20, nameX = Margin + 58;
        float areaR = Margin + 124, newR = Margin + 142, renewR = Margin + 160, totalR = Margin + ContentW;
        Text(g, "B-LCC", small, Muted, idX, y);
        Text(g, "類別", small, Muted, catX, y);
        Text(g, "構件", small, Muted, nameX, y);
        TextRight(g, "面積 m²", small, Muted, areaR, y);
        TextRight(g, "新建", small, Muted, newR, y);
        TextRight(g, "更新", small, Muted, renewR, y);
        TextRight(g, "合計", small, Muted, totalR, y);
        y += 4.6f;
        Line(g, Margin, y, Margin + ContentW, y, Rule, 0.25f);
        y += 0.8f;
        if (s.Items.Count == 0)
        {
            Text(g, "尚未輸入非主結構構件", body, Muted, Margin, y);
            return y + 5;
        }
        var shown = 0;
        foreach (var it in s.Items)
        {
            if (y > bottomLimit - 5) break;
            Text(g, it.BlccId, body, Ink, idX, y, catX - idX - 1);
            Text(g, it.Category, small, Muted, catX, y + 0.4f, nameX - catX - 1);
            Text(g, it.Name, body, Ink, nameX, y, areaR - 18 - nameX);
            TextRight(g, LebrSummaryBuilder.N2(it.Area), body, Ink, areaR, y);
            TextRight(g, LebrSummaryBuilder.N0(it.NewCarbon), body, Ink, newR, y);
            TextRight(g, LebrSummaryBuilder.N0(it.RenewCarbon), body, Ink, renewR, y);
            TextRight(g, LebrSummaryBuilder.N0(it.Total), body, Ink, totalR, y);
            y += 4.6f;
            shown++;
        }
        if (shown < s.Items.Count)
        {
            Text(g, $"……另 {s.Items.Count - shown} 項，完整明細見 Excel 計算書", small, Muted, Margin, y);
            y += 4.2f;
        }
        return y;
    }

    // ───────────────────────── 小工具 ─────────────────────────

    private static void Text(Graphics g, string s, Font f, Color c, float x, float y, float maxWidth = 0)
    {
        using var b = new SolidBrush(c);
        if (maxWidth <= 0) { g.DrawString(s, f, b, x, y); return; }
        using var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(s, f, b, new RectangleF(x, y, maxWidth, f.GetHeight(g) * 1.1f), fmt);
    }

    private static void TextRight(Graphics g, string s, Font f, Color c, float right, float y)
    {
        var w = g.MeasureString(s, f).Width;
        Text(g, s, f, c, right - w, y);
    }

    private static void TextCenter(Graphics g, string s, Font f, Color c, RectangleF r)
    {
        using var b = new SolidBrush(c);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(s, f, b, r, fmt);
    }

    private static void Fill(Graphics g, RectangleF r, Color fill, Color? stroke, float strokeWidth)
    {
        using var b = new SolidBrush(fill);
        g.FillRectangle(b, r);
        if (stroke is { } sc)
        {
            using var p = new Pen(sc, strokeWidth);
            g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
        }
    }

    private static void Line(Graphics g, float x1, float y1, float x2, float y2, Color c, float w)
    {
        using var p = new Pen(c, w);
        g.DrawLine(p, x1, y1, x2, y2);
    }
}
