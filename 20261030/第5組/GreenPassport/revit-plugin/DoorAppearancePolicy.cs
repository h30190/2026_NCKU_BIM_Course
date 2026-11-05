using System.Text.RegularExpressions;

namespace TemporalBim
{
    public static class DoorAppearancePolicy
    {
        public static bool HasWhiteFinish(string material)
        {
            return Regex.IsMatch(material ?? "", "白色|白漆|\\bwhite\\b", RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(material ?? "", "非白|非.*白色|非.*白漆|黑白|白.*或|或.*白|not\\s+white|off[- ]white|black.*white|white.*black", RegexOptions.IgnoreCase);
        }

        public static bool IsPanelMaterial(string parameterName)
        {
            return Regex.IsMatch(parameterName ?? "", "panel|leaf|門扇|門板|面板", RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(parameterName ?? "", "frame|handle|門框|把手|五金", RegexOptions.IgnoreCase);
        }
    }
}
