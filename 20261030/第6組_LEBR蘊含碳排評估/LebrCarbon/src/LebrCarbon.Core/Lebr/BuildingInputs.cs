namespace LebrCarbon.Core.Lebr;

/// <summary>
/// LEBR 評估的建築參數（手冊 4-1 節參數說明與表 8）。
/// 「設計案與基準案同」的參數只有一份；設計案專屬參數另列；基準案參數預設依表 8 自動取值。
/// </summary>
public sealed class BuildingInputs
{
    // ── 基本資料（可由模型取得）────────────────────────────
    /// <summary>AFu 地上層總樓地板面積（m2）。</summary>
    public double AFu { get; set; }
    /// <summary>AFb 地下層總樓地板面積（m2）。</summary>
    public double AFb { get; set; }
    /// <summary>S 地上樓層數（不計屋突；不同樓層混合時可為面積加權值）。</summary>
    public double S { get; set; }
    /// <summary>Sb 地下樓層數。</summary>
    public double Sb { get; set; }
    /// <summary>BH 地上結構一樓底層樓高（m）。</summary>
    public double BH { get; set; } = 3.5;

    // ── 結構設計參數（取自結構計算書）──────────────────────
    /// <summary>I 設計地震力之用途係數。</summary>
    public double I { get; set; } = 1.0;
    /// <summary>SaD 工址設計水平加速度反應譜係數（取 x、y 向大者）。</summary>
    public double SaD { get; set; }
    /// <summary>Fu 結構系統地震力折減係數（取 x、y 向大者）。</summary>
    public double Fu { get; set; } = 1.0;
    /// <summary>Rs 靜力分析折減係數：不需動力分析者 0.95，反之 1.0。</summary>
    public double Rs { get; set; } = 1.0;
    /// <summary>L 活載重（kgf/m2）。</summary>
    public double L { get; set; } = 200;
    /// <summary>D0 額外靜載重（kgf/m2），參考表 4。</summary>
    public double D0 { get; set; } = 300;

    // ── 設計案專屬 ─────────────────────────────────────
    public StructureType Structure { get; set; } = StructureType.RC;
    /// <summary>構造係數 W；null 時依表 3 由構造類別取值（混合構造請自行面積加權後輸入）。</summary>
    public double? WOverride { get; set; }
    /// <summary>設計案跨距變化係數 Sp。</summary>
    public double Sp { get; set; } = 1.0;
    /// <summary>設計案形狀係數 F = f1 × f2 × f3。</summary>
    public double F { get; set; } = 1.0;
    /// <summary>CSER 水泥強度效益倍數；無資料為 0。</summary>
    public double CSER { get; set; }
    /// <summary>EBF 舊建築再利用地上面積（m2）。</summary>
    public double EBF { get; set; }

    // ── 基準案（null 時依表 8 自動取值）────────────────────
    public double? BaselineWOverride { get; set; }
    public double? BaselineSpOverride { get; set; }
    public double? BaselineFOverride { get; set; }

    // ── 其他 ──────────────────────────────────────────
    public WearLevel Wear { get; set; } = WearLevel.Low;
    public WasteUsage WasteUsage { get; set; } = WasteUsage.Other;
    /// <summary>LL 建築延壽優惠係數（表 11），上限 0.08。</summary>
    public double LL { get; set; }
    /// <summary>舊建材再利用減碳量 Σ(RMk×Rk)（kgCO2e）。</summary>
    public double ReuseCredit { get; set; }
    /// <summary>低碳循環建材減碳量 Σ(LCRk×Lk)（kgCO2e）。採計時應令 CSER=0（LCCR=1）。</summary>
    public double RecycledMaterialCredit { get; set; }
    /// <summary>低碳工法減碳量 ΣLCCm（kgCO2e）。</summary>
    public double LowCarbonMethodCredit { get; set; }

    // ── 衍生值 ────────────────────────────────────────
    public double W => WOverride ?? LebrTables.StructureFactorW(Structure);
    public double AverageFloorArea => S > 0 ? AFu / S : 0;
    public double BaselineW => BaselineWOverride ?? LebrTables.BaselineW(S);
    public double BaselineSp => BaselineSpOverride ?? LebrTables.BaselineSp(S, AverageFloorArea);
    public double BaselineF => BaselineFOverride ?? LebrTables.BaselineF(S);

    /// <summary>式 5：LCCR = 1 − CSER × 0.05；輕鋼構與木構造固定 1.0。</summary>
    public double LCCR => Structure is StructureType.LightSteel or StructureType.Wood ? 1.0 : 1.0 - CSER * 0.05;

    /// <summary>式 6：RN = (AFu − EBF) / AFu。</summary>
    public double RN => AFu > 0 ? (AFu - EBF) / AFu : 1.0;

    /// <summary>檢查必要參數，回傳問題清單（空清單代表可計算）。</summary>
    public List<string> Validate()
    {
        var e = new List<string>();
        if (AFu <= 0) e.Add("地上層總樓地板面積 AFu 必須大於 0");
        if (S <= 0) e.Add("地上樓層數 S 必須大於 0");
        if (AFb < 0 || Sb < 0) e.Add("地下層面積與樓層數不可為負");
        if (Sb > 0 && AFb <= 0) e.Add("有地下樓層時 AFb 必須大於 0");
        if (Fu <= 0) e.Add("結構系統地震力折減係數 Fu 必須大於 0");
        if (SaD <= 0) e.Add("工址設計水平加速度反應譜係數 SaD 未輸入");
        if (Sp < 1.0) e.Add("跨距變化係數 Sp 應 ≥ 1.0");
        if (EBF < 0 || EBF > AFu) e.Add("舊建築再利用面積 EBF 應介於 0 與 AFu 之間");
        if (LL < 0 || LL > LebrTables.MaxLifeExtension + 1e-9) e.Add("延壽優惠係數 LL 應介於 0 與 0.08 之間");
        if (CSER < 0) e.Add("CSER 不可為負");
        return e;
    }
}
