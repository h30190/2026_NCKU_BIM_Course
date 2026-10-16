using System;
using System.IO;

namespace RfiPlugin
{
    /// <summary>圖台 token 存本機（%APPDATA%\RfiPlugin\token.txt），下次自動帶，學生看得懂。</summary>
    internal static class TokenStore
    {
        private static string Path =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RfiPlugin", "token.txt");

        public static string Load()
        {
            try { return File.Exists(Path) ? File.ReadAllText(Path).Trim() : ""; }
            catch { return ""; }
        }

        public static void Save(string token)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                File.WriteAllText(Path, (token ?? "").Trim());
            }
            catch { /* 存失敗不擋送出 */ }
        }
    }
}
