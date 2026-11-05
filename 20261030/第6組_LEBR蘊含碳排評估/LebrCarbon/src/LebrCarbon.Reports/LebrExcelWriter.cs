using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using LebrCarbon.Core.Lebr;

namespace LebrCarbon.Reports;

/// <summary>形狀係數的三個輸入（計算書中以公式求 f1、f2、f3、F）。</summary>
public sealed record ShapeValues(double PAr, double Aspect, double Rc);

/// <summary>
/// 輸出 LEBR Excel 計算書（.xlsx），版面參照手冊附錄三。
/// 每一格都是 Excel 公式，從參數到減碳率可逐格查核；藍字為輸入值，黑字為公式。
/// 直接以 System.IO.Compression 寫 SpreadsheetML，不依賴外部套件，避免與其他 Revit 外掛的 DLL 版本衝突。
/// 公式規格與驗證原型同源（以 LibreOffice 重算測試模型與附錄三實例）。
/// </summary>
public static class LebrExcelWriter
{
    public sealed class Ctx
    {
        public required LebrResult R { get; init; }
        public required BuildingInputs P { get; init; }
        public required ShapeValues Shape { get; init; }
    }

    private sealed record Row(
        string Kind,
        string? Key = null,
        string Label = "",
        string Sym = "",
        Func<Ctx, object?>? Value = null,
        string? C = null,
        string? D = null,
        string DFmt = "n2",
        Func<Ctx, double?>? Override = null,
        string Unit = "",
        string Note = "",
        string Fmt = "n2",
        double Mult = 0,
        string[]? Cols = null);

    private const string NsSheet = "'非主結構明細'";
    private const int FirstRow = 5;

    // ── 由 spec.py 產生，勿手改：與驗證原型同一份公式規格 ──
    private static readonly Row[] Rows =
    {
        new("section", Label: "一、基本資料（設計案與基準案同）"),
        new("input", Key: "AFu", Label: "地上層總樓地板面積", Sym: "AFu", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return p.AFu; }, Unit: "m²", Note: "模型樓板面積或執照面積", Fmt: "n2"),
        new("input", Key: "AFb", Label: "地下層總樓地板面積", Sym: "AFb", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return p.AFb; }, Unit: "m²", Note: "", Fmt: "n2"),
        new("input", Key: "S", Label: "地上樓層數（不計屋突）", Sym: "S", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return p.S; }, Unit: "層", Note: "", Fmt: "c3"),
        new("input", Key: "Sb", Label: "地下樓層數", Sym: "Sb", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return p.Sb; }, Unit: "層", Note: "", Fmt: "c3"),
        new("input", Key: "BH", Label: "地上結構一樓底層樓高", Sym: "BH", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return p.BH; }, Unit: "m", Note: "", Fmt: "n2"),
        new("blank"),
        new("section", Label: "二、結構計算書參數（設計案與基準案同）"),
        new("input", Key: "I", Label: "設計地震力之用途係數", Sym: "I", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return p.I; }, Unit: "", Note: "耐震設計規範 2.8 節", Fmt: "c3"),
        new("input", Key: "SaD", Label: "工址設計水平加速度反應譜係數", Sym: "SaD", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return p.SaD; }, Unit: "", Note: "耐震設計規範 2.6 節，x、y 向取大者", Fmt: "c3"),
        new("input", Key: "Fu", Label: "結構系統地震力折減係數", Sym: "Fu", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return p.Fu; }, Unit: "", Note: "耐震設計規範 2.9 節，x、y 向取大者", Fmt: "c3"),
        new("input", Key: "Rs", Label: "靜力分析折減係數", Sym: "Rs", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return p.Rs; }, Unit: "", Note: "不需動力分析 0.95，反之 1.0", Fmt: "c3"),
        new("input", Key: "L", Label: "活載重", Sym: "L", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return p.L; }, Unit: "kgf/m²", Note: "技術規則構造篇第 17 條", Fmt: "n2"),
        new("input", Key: "D0", Label: "額外靜載重", Sym: "D0", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return p.D0; }, Unit: "kgf/m²", Note: "表 4", Fmt: "n2"),
        new("blank"),
        new("section", Label: "三、設計案主結構參數"),
        new("inputText", Key: "struct", Label: "構造類別", Sym: "", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return p.Structure.DisplayName(); }, Unit: "", Note: "表 3", Fmt: "txt"),
        new("input", Key: "W", Label: "構造係數", Sym: "W", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return r.Design.W; }, Unit: "", Note: "表 3", Fmt: "c3"),
        new("input", Key: "Sp", Label: "跨距變化係數", Sym: "Sp", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return p.Sp; }, Unit: "", Note: "式 a～c，由軸線計算", Fmt: "c3"),
        new("input", Key: "PAr", Label: "周長面積比係數", Sym: "PAr", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return shape.PAr; }, Unit: "", Note: "式 9：0.282×P/√A", Fmt: "c3"),
        new("input", Key: "b", Label: "平面長寬比", Sym: "b", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return shape.Aspect; }, Unit: "", Note: "表 6", Fmt: "c3"),
        new("input", Key: "Rc", Label: "出挑係數", Sym: "Rc", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return shape.Rc; }, Unit: "", Note: "表 7", Fmt: "c3"),
        new("formula", Key: "f1", Label: "平面形狀不規則修正係數", Sym: "f1", C: "=IF({PAr}<=1.2,1,IF({PAr}<=1.4,1.03,IF({PAr}<=1.6,1.05,1.08)))", Unit: "", Note: "表 5", Fmt: "c3"),
        new("formula", Key: "f2", Label: "平面長寬比修正係數", Sym: "f2", C: "=IF({b}<=4,1,IF({b}<=6,1.05,1.1))", Unit: "", Note: "表 6", Fmt: "c3"),
        new("formula", Key: "f3", Label: "平面出挑修正係數", Sym: "f3", C: "=IF({Rc}<=0.1,1,IF({Rc}<=0.2,0.98,0.93))", Unit: "", Note: "表 7", Fmt: "c3"),
        new("formula", Key: "F", Label: "形狀係數", Sym: "F", C: "={f1}*{f2}*{f3}", Unit: "", Note: "式 7", Fmt: "c3"),
        new("input", Key: "CSER", Label: "水泥強度效益倍數", Sym: "CSER", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return p.CSER; }, Unit: "", Note: "無資料為 0", Fmt: "c3"),
        new("formula", Key: "LCCR", Label: "低碳混凝土減碳率", Sym: "LCCR", C: "=IF(OR({struct}=\"輕鋼構造\",{struct}=\"木構造\"),1,1-{CSER}*0.05)", Unit: "", Note: "式 5", Fmt: "c3"),
        new("input", Key: "EBF", Label: "舊建築再利用地上面積", Sym: "EBF", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return p.EBF; }, Unit: "m²", Note: "", Fmt: "n2"),
        new("formula", Key: "RN", Label: "舊建築利用減碳率", Sym: "RN", C: "=({AFu}-{EBF})/{AFu}", Unit: "", Note: "式 6", Fmt: "c3"),
        new("blank"),
        new("section", Label: "四、基準案主結構參數（表 8）"),
        new("formula", Key: "AFa", Label: "地上單層平均樓地板面積", Sym: "AFa", C: "={AFu}/{S}", Unit: "m²", Note: "AFu ÷ S", Fmt: "n2"),
        new("baseline", Key: "Wc", Label: "基準案構造係數", Sym: "Wc", C: "=IF({S}<=25,1,0.9)", Override: c => c.P.BaselineWOverride, Note: "表 8：25F 以下 1.0，26F 以上 0.9", Fmt: "c3"),
        new("baseline", Key: "Spc", Label: "基準案跨距變化係數", Sym: "Spc", C: "=IF({AFa}>500,IF({S}<=8,2,IF({S}<=16,1.8,IF({S}<=25,1.6,1.4))),IF({AFa}>=200,IF({S}<=8,1.8,IF({S}<=16,1.6,1.4)),IF({S}<=8,1.6,1.4)))", Override: c => c.P.BaselineSpOverride, Note: "表 8，依 AFa 與樓層數", Fmt: "c3"),
        new("baseline", Key: "Fc", Label: "基準案形狀係數", Sym: "Fc", C: "=IF({S}<=8,1.2,IF({S}<=16,1.15,IF({S}<=25,1.1,1.05)))", Override: c => c.P.BaselineFOverride, Note: "表 8", Fmt: "c3"),
        new("blank"),
        new("section", Label: "五、主結構碳排（式 1～4）"),
        new("formula", Key: "Craw", Label: "設計案碳排密度（式 4 計算值）", Sym: "C", C: "=(224+4.11*({S}-10)+300*({I}*{SaD}/{Fu}-0.192)+68.74*({Sp}-1)+0.17*({D0}-300)+0.13*({L}-300)+1.05*({BH}-3.5))*{Rs}*{F}", Unit: "kgCO2e/m²", Note: "式 4", Fmt: "n2"),
        new("formula", Key: "Cd", Label: "設計案碳排密度（套用下限）", Sym: "C", C: "=MAX(165,{Craw})", Unit: "kgCO2e/m²", Note: "式 4：C ≥ 165", Fmt: "n2"),
        new("formula", Key: "Crawc", Label: "基準案碳排密度（式 4 計算值）", Sym: "Cc", C: "=(224+4.11*({S}-10)+300*({I}*{SaD}/{Fu}-0.192)+68.74*({Spc}-1)+0.17*({D0}-300)+0.13*({L}-300)+1.05*({BH}-3.5))*{Rs}*{Fc}", Unit: "kgCO2e/m²", Note: "式 4", Fmt: "n2"),
        new("formula", Key: "Cc", Label: "基準案碳排密度（套用下限）", Sym: "Cc", C: "=MAX(165,{Crawc})", Unit: "kgCO2e/m²", Note: "式 4：C ≥ 165", Fmt: "n2"),
        new("formula", Key: "Cu", Label: "設計案地上層主結構標準碳排", Sym: "Cu", C: "={AFu}*{Cd}*{W}", Unit: "kgCO2e", Note: "式 3", Fmt: "n0"),
        new("formula", Key: "CFs", Label: "設計案地上層主結構碳排", Sym: "CFs", C: "={Cu}*{LCCR}*{RN}", Unit: "kgCO2e", Note: "式 1-1", Fmt: "n0"),
        new("formula", Key: "Cuc", Label: "基準案地上層主結構標準碳排", Sym: "Cuc", C: "={AFu}*{Cc}*{Wc}", Unit: "kgCO2e", Note: "式 3", Fmt: "n0"),
        new("formula", Key: "CFsc", Label: "基準案地上層主結構碳排", Sym: "CFsc", C: "={Cuc}", Unit: "kgCO2e", Note: "式 1-2", Fmt: "n0"),
        new("formula", Key: "CFsU", Label: "地下層主結構碳排", Sym: "CFs'", C: "=330*{AFb}+45.5*({AFu}+{AFb})", Unit: "kgCO2e", Note: "式 2（不列入分級）", Fmt: "n0"),
        new("blank"),
        new("section", Label: "六、非主結構碳排（式 8～11，明細見「非主結構明細」工作表）"),
        new("header", Cols: new[] { "構件類別", "設計案新建", "設計案更新", "基準案新建", "基準案更新" }),
        new("nscat", Key: "ns_ow", Label: "一般外牆外裝"),
        new("nscat", Key: "ns_w", Label: "外窗及透光帷幕外窗"),
        new("nscat", Key: "ns_cw", Label: "不透光帷幕外牆及一般外牆"),
        new("nscat", Key: "ns_iw", Label: "內隔間"),
        new("nscat", Key: "ns_f", Label: "室內地坪"),
        new("nscat", Key: "ns_p", Label: "戶外地坪"),
        new("nstotal", Key: "ns_total", Label: "小計（CFns、CFrm）"),
        new("blank"),
        new("section", Label: "七、生命週期四階段碳排（地上層）"),
        new("header", Cols: new[] { "階段", "符號", "設計案", "基準案", "單位", "依據" }),
        new("formula", Key: "CFum", Label: "製造運輸階段", Sym: "CFum", C: "={CFs}+{B:ns_total}", D: "={CFsc}+{D:ns_total}", DFmt: "n0", Unit: "kgCO2e", Note: "式 9", Fmt: "n0"),
        new("formula", Key: "CFrm", Label: "更新修繕階段", Sym: "CFrm", C: "={C:ns_total}", D: "={E:ns_total}", DFmt: "n0", Unit: "kgCO2e", Note: "式 10", Fmt: "n0"),
        new("input", Key: "Wd", Label: "新建與拆除廢棄物量密度", Sym: "Wd", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return r.Wd; }, Unit: "kg/m²", Note: "表 9", Fmt: "n2"),
        new("formula", Key: "CFc", Label: "施工階段", Sym: "CFc", C: "=(0.14+0.95*{S})*{AFu}*(1+{CFrm}/{CFum})", D: "={CFc}", DFmt: "n0", Unit: "kgCO2e", Note: "式 12（基準案同設計案）", Fmt: "n0"),
        new("formula", Key: "CFdw", Label: "拆除廢棄階段", Sym: "CFdw", C: "=((0.06*{S}+2.01)*{AFu}+0.055*{Wd}*{AFu})*(1+{CFrm}/{CFum})", D: "={CFdw}", DFmt: "n0", Unit: "kgCO2e", Note: "式 14～16（基準案同設計案）", Fmt: "n0"),
        new("input", Key: "credit", Label: "減碳優惠（舊建材、循環建材、低碳工法）", Sym: "", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return p.ReuseCredit + p.RecycledMaterialCredit + p.LowCarbonMethodCredit; }, Unit: "kgCO2e", Note: "式 21", Fmt: "n0"),
        new("input", Key: "LL", Label: "建築延壽優惠係數", Sym: "LL", Value: c => { var p = c.P; var r = c.R; var shape = c.Shape; return p.LL; }, Unit: "", Note: "表 11，上限 0.08", Fmt: "c3"),
        new("blank"),
        new("section", Label: "八、分級評估（式 20～27）"),
        new("formula", Key: "EEC", Label: "設計案評估範疇蘊含碳排", Sym: "EEC", C: "=({CFum}+{CFrm}+{CFc}+{CFdw}-{credit})/(1+{LL})", Unit: "kgCO2e", Note: "式 21", Fmt: "n0"),
        new("formula", Key: "EECc", Label: "基準案評估範疇蘊含碳排", Sym: "EECc", C: "={D:CFum}+{D:CFrm}+{D:CFc}+{D:CFdw}", Unit: "kgCO2e", Note: "式 20", Fmt: "n0"),
        new("formula", Key: "CFcU", Label: "地下層施工碳排", Sym: "CFc'", C: "=(0.14+2.14*{Sb})*({AFu}+{AFb})", Unit: "kgCO2e", Note: "式 13（不列入分級）", Fmt: "n0"),
        new("formula", Key: "CFdwU", Label: "地下層拆除廢棄碳排", Sym: "CFdw'", C: "=(0.15*{Sb}+2.01)*{AFb}+0.135*{Wd}*{AFb}", Unit: "kgCO2e", Note: "式 17～19（不列入分級）", Fmt: "n0"),
        new("formula", Key: "TEC", Label: "設計案全生命週期蘊含碳排", Sym: "TEC", C: "={EEC}+{CFsU}+{CFcU}+{CFdwU}", Unit: "kgCO2e", Note: "式 23", Fmt: "n0"),
        new("formula", Key: "TECc", Label: "基準案全生命週期蘊含碳排", Sym: "TECc", C: "={EECc}+{CFsU}+{CFcU}+{CFdwU}", Unit: "kgCO2e", Note: "式 22", Fmt: "n0"),
        new("formula", Key: "ECI", Label: "設計案蘊含碳排密度", Sym: "ECI", C: "={EEC}/{AFu}", Unit: "kgCO2e/m²", Note: "式 25", Fmt: "n2"),
        new("formula", Key: "ECIs", Label: "基準案蘊含碳排密度", Sym: "ECIs", C: "={EECc}/{AFu}", Unit: "kgCO2e/m²", Note: "式 24", Fmt: "n2"),
        new("formula", Key: "dCF", Label: "碳排總減碳量", Sym: "ΔCF", C: "={EECc}-{EEC}", Unit: "kgCO2e", Note: "式 26", Fmt: "n0"),
        new("formula", Key: "CFR", Label: "碳排減碳率", Sym: "CFR", C: "={dCF}/{EECc}", Unit: "", Note: "式 27", Fmt: "pct"),
        new("formula", Key: "grade", Label: "LEBR 等級", Sym: "", C: "=IF({CFR}>0.2,\"1+級\",IF({CFR}>0.16,\"1級\",IF({CFR}>0.12,\"2級\",IF({CFR}>0.08,\"3級\",IF({CFR}>0.03,\"4級\",IF({CFR}>-0.1,\"5級\",IF({CFR}>-0.2,\"6級\",\"7級\")))))))", Unit: "", Note: "表 10", Fmt: "txtb"),
        new("blank"),
        new("section", Label: "九、八等級刻度碳排密度（表 12）"),
        new("header", Cols: new[] { "等級", "ECIs 乘數", "刻度 kgCO2e/m²", "", "" }),
        new("tick", Key: "tick_1+級", Label: "1+級", Mult: 0.8),
        new("tick", Key: "tick_1級", Label: "1級", Mult: 0.84),
        new("tick", Key: "tick_2級", Label: "2級", Mult: 0.88),
        new("tick", Key: "tick_3級", Label: "3級", Mult: 0.92),
        new("tick", Key: "tick_4級", Label: "4級", Mult: 0.97),
        new("tick", Key: "tick_5級", Label: "5級", Mult: 1.1),
        new("tick", Key: "tick_6級", Label: "6級", Mult: 1.2),
    };

    // 樣式索引（對應 styles.xml 的 cellXfs）
    private const int SNormal = 0, STitle = 1, SSection = 2, SHeader = 3,
        SInN2 = 4, SInN0 = 5, SInC3 = 6, SInText = 7,
        SFN2 = 8, SFN0 = 9, SFC3 = 10, SFPct = 11, SFTextBold = 12, SNote = 13, SUnit = 14;

    public static void Write(string path, LebrResult r, ShapeValues shape, string modelTitle)
    {
        var ctx = new Ctx { R = r, P = r.Inputs, Shape = shape };

        var rowOf = new Dictionary<string, int>();
        for (var i = 0; i < Rows.Length; i++)
            if (Rows[i].Key is { } k) rowOf[k] = FirstRow + i;

        string Resolve(string template) => Regex.Replace(template, @"\{(?:([A-F]):)?([A-Za-z_0-9+]+)\}",
            m => (m.Groups[1].Success ? m.Groups[1].Value : "C") + rowOf[m.Groups[2].Value]);

        var main = new SheetXml();
        main.Text(1, 1, "LEBR 低碳（低蘊含碳）建築評估計算書", STitle);
        main.Text(2, 1, $"模型：{modelTitle}　匯出：{DateTime.Now:yyyy-MM-dd HH:mm}　依據：《低碳（低蘊含碳）建築評估手冊》2025 年版", SNote);
        main.Text(3, 1, "藍字為輸入值（模型讀取或使用者輸入），黑字為公式。設計自主檢討用，非正式標示結果。", SNote);

        for (var i = 0; i < Rows.Length; i++)
        {
            var row = Rows[i];
            var n = FirstRow + i;
            switch (row.Kind)
            {
                case "section":
                    main.Text(n, 1, row.Label, SSection);
                    for (var col = 2; col <= 6; col++) main.Text(n, col, "", SSection);
                    break;
                case "header":
                    for (var col = 0; col < row.Cols!.Length; col++) main.Text(n, col + 1, row.Cols[col], SHeader);
                    break;
                case "input":
                case "inputText":
                {
                    main.Text(n, 1, row.Label);
                    main.Text(n, 2, row.Sym);
                    var v = row.Value!(ctx);
                    if (v is string sv) main.Text(n, 3, sv, SInText);
                    else main.Number(n, 3, Convert.ToDouble(v, CultureInfo.InvariantCulture), InputStyle(row.Fmt));
                    main.Text(n, 5, row.Unit, SUnit);
                    main.Text(n, 6, row.Note, SNote);
                    break;
                }
                case "formula":
                    main.Text(n, 1, row.Label);
                    main.Text(n, 2, row.Sym);
                    main.Formula(n, 3, Resolve(row.C!), FormulaStyle(row.Fmt));
                    if (row.D is not null) main.Formula(n, 4, Resolve(row.D), FormulaStyle(row.DFmt));
                    main.Text(n, 5, row.Unit, SUnit);
                    main.Text(n, 6, row.Note, SNote);
                    break;
                case "baseline":
                {
                    main.Text(n, 1, row.Label);
                    main.Text(n, 2, row.Sym);
                    var ov = row.Override?.Invoke(ctx);
                    if (ov is double d)
                    {
                        main.Number(n, 3, d, InputStyle(row.Fmt));
                        main.Text(n, 6, "人工指定（" + row.Note + "）", SNote);
                    }
                    else
                    {
                        main.Formula(n, 3, Resolve(row.C!), FormulaStyle(row.Fmt));
                        main.Text(n, 6, row.Note, SNote);
                    }
                    break;
                }
                case "nscat":
                {
                    main.Text(n, 1, row.Label);
                    var cols = new[] { ("B", "設計案", "H"), ("C", "設計案", "I"), ("D", "基準案", "H"), ("E", "基準案", "I") };
                    foreach (var (col, scen, src) in cols)
                        main.Formula(n, ColIndex(col),
                            $"SUMIFS({NsSheet}!${src}:${src},{NsSheet}!$A:$A,\"{scen}\",{NsSheet}!$B:$B,$A{n})", SFN0);
                    break;
                }
                case "nstotal":
                {
                    main.Text(n, 1, row.Label, SFTextBold);
                    var a = rowOf["ns_ow"];
                    var b = rowOf["ns_p"];
                    foreach (var col in new[] { "B", "C", "D", "E" })
                        main.Formula(n, ColIndex(col), $"SUM({col}{a}:{col}{b})", SFN0);
                    break;
                }
                case "tick":
                    main.Text(n, 1, row.Label);
                    main.Number(n, 2, row.Mult, SInC3);
                    main.Formula(n, 3, $"$C${rowOf["ECIs"]}*B{n}", SFN0);
                    break;
            }
        }
        main.ColumnWidths = new[] { 40.0, 14, 18, 18, 12, 44 };

        // 非主結構明細
        var ns = new SheetXml();
        var headers = new[] { "情境", "類別", "構件", "B-LCC ID", "面積 m²", "新建碳排標準 kgCO2e/m²", "更新碳排標準 kgCO2e/m²", "新建碳排 kgCO2e", "更新碳排 kgCO2e", "來源" };
        for (var i = 0; i < headers.Length; i++) ns.Text(1, i + 1, headers[i], SHeader);
        var rr = 2;
        foreach (var (scen, items) in new[] { ("設計案", r.DesignItems), ("基準案", r.BaselineItems) })
        {
            foreach (var it in items)
            {
                ns.Text(rr, 1, scen);
                ns.Text(rr, 2, it.Category.DisplayName());
                ns.Text(rr, 3, it.Name);
                ns.Text(rr, 4, it.BlccId ?? "自訂");
                ns.Number(rr, 5, it.Area, SInN2);
                ns.Number(rr, 6, it.NewEf, SInC3);
                ns.Number(rr, 7, it.RenewEf, SInC3);
                ns.Formula(rr, 8, $"E{rr}*F{rr}", SFN0);
                ns.Formula(rr, 9, $"E{rr}*G{rr}", SFN0);
                ns.Text(rr, 10, it.Source, SNote);
                rr++;
            }
        }
        ns.ColumnWidths = new[] { 8.0, 22, 40, 12, 12, 14, 14, 14, 14, 40 };

        // 說明
        var info = new SheetXml();
        var lines = new[]
        {
            ("LEBR 計算書說明", STitle),
            ("", SNormal),
            ("1. 本計算書依《低碳（低蘊含碳）建築評估手冊》2025 年版第四、五章（式 1～27、表 1～12）計算。", SNormal),
            ("2. 藍字為輸入值：建築參數由 Revit 模型讀取或使用者輸入，非主結構單位碳排取自手冊附錄二 B-LCC。黑字為公式，可直接修改藍字重算。", SNormal),
            ("3. 「計算書」工作表的非主結構小計以 SUMIFS 加總「非主結構明細」工作表，新增明細列時請保持「情境」與「類別」欄文字一致。", SNormal),
            ("4. 地下層碳排（CFs'、CFc'、CFdw'）只計入全生命週期 TEC，不列入分級（手冊第五章）。", SNormal),
            ("5. 本計算書為設計自主檢討用，非正式標示結果；正式標示須經評定專業機構審查。", SNormal),
            ("6. 係數資料著作權屬內政部建築研究所，僅供評估使用。", SNormal),
            ("", SNormal),
            ($"產生工具：LebrCarbon Revit 外掛　模型：{modelTitle}　時間：{DateTime.Now:yyyy-MM-dd HH:mm}", SNote),
        };
        for (var i = 0; i < lines.Length; i++) info.Text(i + 1, 1, lines[i].Item1, lines[i].Item2);
        info.ColumnWidths = new[] { 120.0 };

        WritePackage(path, new[] { ("計算書", main), ("非主結構明細", ns), ("說明", info) });
    }

    private static int InputStyle(string fmt) => fmt switch { "n0" => SInN0, "c3" => SInC3, "txt" => SInText, _ => SInN2 };
    private static int FormulaStyle(string fmt) => fmt switch { "n0" => SFN0, "c3" => SFC3, "pct" => SFPct, "txtb" => SFTextBold, _ => SFN2 };
    private static int ColIndex(string col) => col[0] - 'A' + 1;

    private static string ColName(int col)
    {
        var s = "";
        while (col > 0) { var m = (col - 1) % 26; s = (char)('A' + m) + s; col = (col - 1) / 26; }
        return s;
    }

    private static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>一張工作表的儲存格，最後輸出成 sheetN.xml。</summary>
    private sealed class SheetXml
    {
        private readonly SortedDictionary<int, SortedDictionary<int, string>> _cells = new();
        public double[] ColumnWidths { get; set; } = Array.Empty<double>();

        private void Put(int row, int col, string xml)
        {
            if (!_cells.TryGetValue(row, out var r)) _cells[row] = r = new SortedDictionary<int, string>();
            r[col] = xml;
        }

        public void Text(int row, int col, string text, int style = SNormal) =>
            Put(row, col, $"<c r=\"{ColName(col)}{row}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(text)}</t></is></c>");

        public void Number(int row, int col, double v, int style) =>
            Put(row, col, $"<c r=\"{ColName(col)}{row}\" s=\"{style}\"><v>{v.ToString("R", CultureInfo.InvariantCulture)}</v></c>");

        /// <summary>公式不含快取值；活頁簿設定開啟時全部重算。</summary>
        public void Formula(int row, int col, string formula, int style) =>
            Put(row, col, $"<c r=\"{ColName(col)}{row}\" s=\"{style}\"><f>{Esc(formula.TrimStart('='))}</f></c>");

        public string ToXml()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            sb.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>"); // 列印時縮放成一頁寬
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\" zoomScale=\"100\"/></sheetViews>");
            sb.Append("<sheetFormatPr defaultRowHeight=\"16.5\"/>");
            if (ColumnWidths.Length > 0)
            {
                sb.Append("<cols>");
                for (var i = 0; i < ColumnWidths.Length; i++)
                    sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{ColumnWidths[i].ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
                sb.Append("</cols>");
            }
            sb.Append("<sheetData>");
            foreach (var (r, cells) in _cells)
            {
                sb.Append($"<row r=\"{r}\">");
                foreach (var c in cells.Values) sb.Append(c);
                sb.Append("</row>");
            }
            sb.Append("</sheetData>");
            sb.Append("<pageMargins left=\"0.5\" right=\"0.5\" top=\"0.6\" bottom=\"0.6\" header=\"0.3\" footer=\"0.3\"/>");
            sb.Append("<pageSetup paperSize=\"9\" orientation=\"landscape\" fitToWidth=\"1\" fitToHeight=\"0\"/>"); // A4 橫向
            sb.Append("</worksheet>");
            return sb.ToString();
        }
    }

    private static void WritePackage(string path, IReadOnlyList<(string Name, SheetXml Sheet)> sheets)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        void Add(string name, string content)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(content);
        }

        const string Xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

        var ct = new StringBuilder(Xml + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (var i = 1; i <= sheets.Count; i++)
            ct.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        ct.Append("</Types>");
        Add("[Content_Types].xml", ct.ToString());

        Add("_rels/.rels", Xml + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");

        var wb = new StringBuilder(Xml + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        var rels = new StringBuilder(Xml + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var i = 1; i <= sheets.Count; i++)
        {
            wb.Append($"<sheet name=\"{Esc(sheets[i - 1].Name)}\" sheetId=\"{i}\" r:id=\"rId{i}\"/>");
            rels.Append($"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
        }
        wb.Append("</sheets><calcPr calcId=\"191029\" fullCalcOnLoad=\"1\"/></workbook>");
        rels.Append($"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        rels.Append("</Relationships>");
        Add("xl/workbook.xml", wb.ToString());
        Add("xl/_rels/workbook.xml.rels", rels.ToString());
        Add("xl/styles.xml", StylesXml);

        for (var i = 1; i <= sheets.Count; i++) Add($"xl/worksheets/sheet{i}.xml", sheets[i - 1].Sheet.ToXml());
    }

    // 字型：0 一般、1 標題、2 粗體、3 藍字輸入、4 灰色註解
    // cellXfs 順序須與上方 S* 常數一致
    private const string StylesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"0.000\"/></numFmts>" +
        "<fonts count=\"5\">" +
        "<font><sz val=\"11\"/><color rgb=\"FF000000\"/><name val=\"Microsoft JhengHei\"/><family val=\"2\"/></font>" +
        "<font><b/><sz val=\"14\"/><color rgb=\"FF000000\"/><name val=\"Microsoft JhengHei\"/><family val=\"2\"/></font>" +
        "<font><b/><sz val=\"11\"/><color rgb=\"FF000000\"/><name val=\"Microsoft JhengHei\"/><family val=\"2\"/></font>" +
        "<font><sz val=\"11\"/><color rgb=\"FF0000FF\"/><name val=\"Microsoft JhengHei\"/><family val=\"2\"/></font>" +
        "<font><sz val=\"10\"/><color rgb=\"FF808080\"/><name val=\"Microsoft JhengHei\"/><family val=\"2\"/></font>" +
        "</fonts>" +
        "<fills count=\"4\">" +
        "<fill><patternFill patternType=\"none\"/></fill>" +
        "<fill><patternFill patternType=\"gray125\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDDEBF7\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F2F2\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "</fills>" +
        "<borders count=\"2\">" +
        "<border><left/><right/><top/><bottom/><diagonal/></border>" +
        "<border><left/><right/><top/><bottom style=\"thin\"><color rgb=\"FFBFBFBF\"/></bottom><diagonal/></border>" +
        "</borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"15\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +                                  // 0 一般
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +                  // 1 標題
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +  // 2 章節
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>" + // 3 表頭
        "<xf numFmtId=\"4\" fontId=\"3\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyNumberFormat=\"1\"/>" +   // 4 輸入 #,##0.00
        "<xf numFmtId=\"3\" fontId=\"3\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyNumberFormat=\"1\"/>" +   // 5 輸入 #,##0
        "<xf numFmtId=\"164\" fontId=\"3\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyNumberFormat=\"1\"/>" + // 6 輸入 0.000
        "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +                  // 7 輸入文字
        "<xf numFmtId=\"4\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +          // 8 公式 #,##0.00
        "<xf numFmtId=\"3\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +          // 9 公式 #,##0
        "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +        // 10 公式 0.000
        "<xf numFmtId=\"10\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +         // 11 公式 0.00%
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +                  // 12 粗體
        "<xf numFmtId=\"0\" fontId=\"4\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +                  // 13 註解
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"left\" indent=\"1\"/></xf>" + // 14 單位（內縮）
        "</cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";
}
