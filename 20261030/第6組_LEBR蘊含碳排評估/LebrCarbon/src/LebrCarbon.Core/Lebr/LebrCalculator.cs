namespace LebrCarbon.Core.Lebr;

/// <summary>
/// LEBR 生命週期四階段碳排評估（手冊第四、五章，式 1 ~ 27）。
/// 純計算，不依賴 Revit；輸入為建築參數與非主結構構件清單。
/// </summary>
public static class LebrCalculator
{
    /// <summary>式 4 的下限值。</summary>
    public const double MinC = 165;

    /// <summary>式 4：地上層主結構單位樓地板面積碳排密度 C。回傳（套用下限後, 套用前）。</summary>
    public static (double C, double Raw) UnitCarbon(BuildingInputs p, double sp, double f)
    {
        var raw = (224
                   + 4.11 * (p.S - 10)
                   + 300 * (p.I * p.SaD / p.Fu - 0.192)
                   + 68.74 * (sp - 1.0)
                   + 0.17 * (p.D0 - 300)
                   + 0.13 * (p.L - 300)
                   + 1.05 * (p.BH - 3.5))
                  * p.Rs * f;
        return (Math.Max(MinC, raw), raw);
    }

    public static StructureScenario DesignStructure(BuildingInputs p)
    {
        var (c, raw) = UnitCarbon(p, p.Sp, p.F);
        var cu = p.AFu * c * p.W;                                   // 式 3
        return new(p.W, p.Sp, p.F, p.LCCR, p.RN, c, raw, cu, cu * p.LCCR * p.RN); // 式 1-1
    }

    public static StructureScenario BaselineStructure(BuildingInputs p)
    {
        var (c, raw) = UnitCarbon(p, p.BaselineSp, p.BaselineF);
        var cu = p.AFu * c * p.BaselineW;
        return new(p.BaselineW, p.BaselineSp, p.BaselineF, 1.0, 1.0, c, raw, cu, cu); // 式 1-2
    }

    /// <summary>式 2：地下層主結構碳排 CFs' = 330×AFb + 45.5×(AFu+AFb)。</summary>
    public static double UndergroundStructure(BuildingInputs p) => 330 * p.AFb + 45.5 * (p.AFu + p.AFb);

    public static LebrResult Evaluate(
        BuildingInputs p,
        IReadOnlyList<NonStructuralItem> designItems,
        IReadOnlyList<NonStructuralItem> baselineItems)
    {
        var problems = p.Validate();
        if (problems.Count > 0)
            throw new ArgumentException("建築參數不完整：\n" + string.Join("\n", problems));

        var design = DesignStructure(p);
        var baseline = BaselineStructure(p);

        // 式 8、10、11：非主結構新建與更新碳排
        var dSub = Subtotals(designItems);
        var bSub = Subtotals(baselineItems);
        var cfns = dSub.Sum(s => s.New);
        var cfrm = dSub.Sum(s => s.Renew);
        var cfnsB = bSub.Sum(s => s.New);
        var cfrmB = bSub.Sum(s => s.Renew);

        // 式 9
        var cfum = design.CFs + cfns;
        var cfumB = baseline.CFs + cfnsB;

        // 式 12 ~ 19：施工與拆除廢棄只以設計案計算一次，基準案同
        var renewRatio = 1.0 + cfrm / cfum;
        var af = p.AFu + p.AFb;
        var cfc = (0.14 + 0.95 * p.S) * p.AFu * renewRatio;          // 式 12
        var cfcU = (0.14 + 2.14 * p.Sb) * af;                         // 式 13

        var wd = LebrTables.WasteDensity(p.Structure, p.WasteUsage);
        var cfd = (0.06 * p.S + 2.01) * p.AFu;                        // 式 15
        var cfwa = 0.055 * wd * p.AFu;                                 // 式 16
        var cfdw = (cfd + cfwa) * renewRatio;                         // 式 14
        var cfdU = (0.15 * p.Sb + 2.01) * p.AFb;                      // 式 18
        var cfwaU = 0.135 * wd * p.AFb;                               // 式 19
        var cfdwU = cfdU + cfwaU;                                     // 式 17

        var cfsU = UndergroundStructure(p);

        // 式 20 ~ 27
        var eecc = cfumB + cfrmB + cfc + cfdw;
        var credits = p.ReuseCredit + p.RecycledMaterialCredit + p.LowCarbonMethodCredit;
        var eec = (cfum + cfrm + cfc + cfdw - credits) / (1.0 + p.LL);
        var underground = cfsU + cfcU + cfdwU;
        var delta = eecc - eec;
        var cfr = delta / eecc;

        return new LebrResult
        {
            Inputs = p,
            Design = design,
            Baseline = baseline,
            CFsUnderground = cfsU,
            DesignItems = designItems,
            BaselineItems = baselineItems,
            DesignSubtotals = dSub,
            BaselineSubtotals = bSub,
            CFns = cfns,
            CFnsBaseline = cfnsB,
            CFum = cfum,
            CFumBaseline = cfumB,
            CFrm = cfrm,
            CFrmBaseline = cfrmB,
            CFc = cfc,
            CFcUnderground = cfcU,
            CFdw = cfdw,
            CFdwUnderground = cfdwU,
            Wd = wd,
            EEC = eec,
            EECc = eecc,
            TEC = eec + underground,
            TECc = eecc + underground,
            ECI = eec / p.AFu,
            ECIs = eecc / p.AFu,
            DeltaCF = delta,
            CFR = cfr,
            Grade = LebrTables.Grade(cfr),
        };
    }

    private static List<NsSubtotal> Subtotals(IEnumerable<NonStructuralItem> items) =>
        Enum.GetValues<NsCategory>()
            .Select(c =>
            {
                var xs = items.Where(i => i.Category == c).ToList();
                return new NsSubtotal(c, xs.Sum(i => i.Area), xs.Sum(i => i.NewEmission), xs.Sum(i => i.RenewEmission));
            })
            .ToList();
}
