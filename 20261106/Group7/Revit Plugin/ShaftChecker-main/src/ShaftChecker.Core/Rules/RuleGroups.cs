namespace ShaftChecker.Core.Rules
{
    /// <summary>規則分組（儀表板與報表依此分欄）。</summary>
    public static class RuleGroups
    {
        public const string Fire = "fire";
        public const string Maintenance = "maint";
        public const string Mep = "mep";
        public const string Structure = "struct";

        public static string Of(string ruleId)
        {
            string id = (ruleId ?? "").ToUpperInvariant();
            if (id == "A1" || id == "A2" || id == "A4") return Fire;
            if (id.StartsWith("P")) return Mep;
            if (id.StartsWith("S")) return Structure;
            return Maintenance;
        }

        public static string Label(string group)
        {
            switch (group)
            {
                case Fire: return "防火時效及區劃";
                case Maintenance: return "維修門與維修空間";
                case Mep: return "管線分隔與間距";
                case Structure: return "避開結構柱梁";
                default: return group;
            }
        }
    }

    /// <summary>
    /// 引用的條文。條號已對照建築技術規則條文；穿梁限制不是建築法規，屬結構實務，標為設計參數。
    /// </summary>
    public static class LegalRefs
    {
        public const string Shaft79_2 = "設計施工編 第79-2條";
        public const string Underground203 = "地下建築物：設計施工編 第203條（第十一章，管道間維修門應具一小時以上防火時效及遮煙性能）";
        public const string Stair97 = "設計施工編 第97條（特別安全梯：管道間之維修孔不得開向樓梯間）";
        public const string Stair97Underground = "地下層：同第97條；屬地下建築物者另依設計施工編第十一章";
        public const string HighRise246Space = "設計施工編 第246條（高層建築物配管管道間應考慮維修及更換空間）";
        public const string HighRise246Gas = "設計施工編 第246條（瓦斯管之管道間應單獨設置）";
        public const string Equip2_1 = "建築設備編 第2-1條（電氣管道與給水排水管、消防水管、燃氣供氣管路、空調用水管等應予分隔）";
        public const string Damper = "建築設備編 第92條第4款、第94條；設計施工編 第85條（貫穿防火區劃之風管應設防火閘門）";
        public const string StructCoord = "設計參數：管道間淨空與結構協調（設計施工編 第246條 維修及更換空間）";
        public const string BeamHole = "設計參數：結構實務穿梁限制（孔徑 ≤ 梁深 1/3、位於梁深中央 1/3、距柱面 ≥ 2 倍梁深、孔距 ≥ 3 倍孔徑；以結構技師要求為準）";
    }
}
