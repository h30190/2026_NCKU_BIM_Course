using System.Text.RegularExpressions;

namespace TemporalBim
{
    public static class RetiredPassportFields
    {
        public static bool Contains(string name) => Regex.IsMatch(name ?? "", "fire|防火|耐火|遮煙|阻熱|codeperformance", RegexOptions.IgnoreCase);
    }
}
