using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LebrCarbon.Revit
{
    public enum ParamTarget { TypeCode, InstanceFlag, Project }

    /// <summary>一個 LEBR 共用參數的定義。GUID 固定，不同專案載入後才能互相辨識。</summary>
    public class LebrParamDef
    {
        public LebrParamDef(string name, ForgeTypeId spec, string guid, ParamTarget target, string description, params BuiltInCategory[] categories)
        {
            Name = name;
            Spec = spec;
            Guid = new Guid(guid);
            Target = target;
            Description = description;
            Categories = categories;
        }

        public string Name { get; }
        public ForgeTypeId Spec { get; }
        public Guid Guid { get; }
        public ParamTarget Target { get; }
        public string Description { get; }
        public BuiltInCategory[] Categories { get; }
    }

    /// <summary>計畫書第五章「構件對應」的參數清單。</summary>
    public static class LebrParameters
    {
        public const string Code = "LEBR_Code";
        public const string CodeBody = "LEBR_Code_Body";
        public const string Exclude = "LEBR_Exclude";

        public static readonly BuiltInCategory[] ComponentCategories =
        {
            BuiltInCategory.OST_Walls, BuiltInCategory.OST_Windows,
            BuiltInCategory.OST_CurtainWallPanels, BuiltInCategory.OST_Floors,
        };

        private static readonly BuiltInCategory[] ProjectInfo = { BuiltInCategory.OST_ProjectInformation };

        public static readonly IReadOnlyList<LebrParamDef> All = new List<LebrParamDef>
        {
            new LebrParamDef(Code, SpecTypeId.String.Text, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a01", ParamTarget.TypeCode,
                "LEBR 構件代號（附錄二 B-LCC），可省略建築類別，例如 IW-03、F-G01、W-G01-AL", ComponentCategories),
            new LebrParamDef(CodeBody, SpecTypeId.String.Text, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a02", ParamTarget.TypeCode,
                "外牆本體代號（附表 2-4），例如 CW-00 傳統 15cm RC 外牆；外裝填在 LEBR_Code", BuiltInCategory.OST_Walls),
            new LebrParamDef(Exclude, SpecTypeId.Boolean.YesNo, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a03", ParamTarget.InstanceFlag,
                "勾選後不計入 LEBR（樓梯間牆、電梯間牆、分戶牆、教室隔間牆、停車場地坪等）", ComponentCategories),

            new LebrParamDef("LEBR_WearClass", SpecTypeId.String.Text, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a10", ParamTarget.Project,
                "建築類別（表 3-1）：H 高耗損、M 中耗損、L 低耗損", ProjectInfo),
            new LebrParamDef("LEBR_Structure", SpecTypeId.String.Text, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a11", ParamTarget.Project,
                "構造（表 4-1）：RC、SRC、S、light_steel、wood、masonry", ProjectInfo),
            new LebrParamDef("LEBR_UseType", SpecTypeId.String.Text, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a12", ParamTarget.Project,
                "用途（表 4-7）：residential 住宿類、factory 廠房類、other 其他", ProjectInfo),
            new LebrParamDef("LEBR_I", SpecTypeId.Number, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a13", ParamTarget.Project, "用途係數 I（結構計算書）", ProjectInfo),
            new LebrParamDef("LEBR_SaD", SpecTypeId.Number, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a14", ParamTarget.Project, "工址設計水平加速度反應譜係數 SaD（結構計算書）", ProjectInfo),
            new LebrParamDef("LEBR_Fu", SpecTypeId.Number, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a15", ParamTarget.Project, "結構系統地震力折減係數 Fu（結構計算書）", ProjectInfo),
            new LebrParamDef("LEBR_L", SpecTypeId.Number, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a16", ParamTarget.Project, "活載重 L（kgf/m²）", ProjectInfo),
            new LebrParamDef("LEBR_D0", SpecTypeId.Number, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a17", ParamTarget.Project, "額外靜載重 D0（kgf/m²，表 4-2）", ProjectInfo),
            new LebrParamDef("LEBR_Rs", SpecTypeId.Number, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a18", ParamTarget.Project, "靜力分析折減係數 Rs：不需動力分析 0.95，否則 1.0", ProjectInfo),
            new LebrParamDef("LEBR_CSER", SpecTypeId.Number, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a19", ParamTarget.Project, "低碳混凝土強度效益倍數 CSER，沒有填 0", ProjectInfo),
            new LebrParamDef("LEBR_LL", SpecTypeId.Number, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a1a", ParamTarget.Project, "延壽優惠係數 LL（表 5-2）合計值，沒有填 0", ProjectInfo),
            new LebrParamDef("LEBR_LL_Durable", SpecTypeId.Number, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a22", ParamTarget.Project, "表 5-2 耐久設計所選項目編號（1 或 2，0 表示無），供專案設定還原選單", ProjectInfo),
            new LebrParamDef("LEBR_LL_Quality", SpecTypeId.Number, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a23", ParamTarget.Project, "表 5-2 高品質施工所選項目編號（3 或 4，0 表示無），供專案設定還原選單", ProjectInfo),
            new LebrParamDef("LEBR_AFu", SpecTypeId.Number, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a1b", ParamTarget.Project, "（選填）地上樓地板面積 m²，填了就不用模型計算值", ProjectInfo),
            new LebrParamDef("LEBR_AFb", SpecTypeId.Number, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a1c", ParamTarget.Project, "（選填）地下樓地板面積 m²", ProjectInfo),
            new LebrParamDef("LEBR_S", SpecTypeId.Number, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a1d", ParamTarget.Project, "（選填）地上樓層數", ProjectInfo),
            new LebrParamDef("LEBR_Sb", SpecTypeId.Number, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a1e", ParamTarget.Project, "（選填）地下樓層數", ProjectInfo),
            new LebrParamDef("LEBR_BH", SpecTypeId.Number, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a1f", ParamTarget.Project, "（選填）一樓樓高 m", ProjectInfo),
            new LebrParamDef("LEBR_Sp", SpecTypeId.Number, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a20", ParamTarget.Project, "（選填）跨距變化係數 Sp，填了就不用格線計算", ProjectInfo),
            new LebrParamDef("LEBR_F", SpecTypeId.Number, "6f1c2a10-4b3e-4c8a-9a51-0d2e7b3c4a21", ParamTarget.Project, "（選填）形狀係數 F = f1×f2×f3，填了就不用樓板外框計算", ProjectInfo),
            new LebrParamDef("LEBR_CFs", SpecTypeId.Number, "0dbc1252-2e5a-4ac7-b058-b97057d16078", ParamTarget.Project, "結構碳排估算值 CFs（kgCO2e），由估算視窗寫入；沒有填 0", ProjectInfo),
            new LebrParamDef("LEBR_EstimateJson", SpecTypeId.String.Text, "e901a98d-f808-4b97-954c-a141e504b939", ParamTarget.Project, "結構碳排估算紀錄（JSON），供重現估算結果", ProjectInfo),
        };
    }
}
