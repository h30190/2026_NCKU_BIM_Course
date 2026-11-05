using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace LebrCarbon
{
    /// <summary>
    /// 「在圖台開啟」的連結：結果 JSON → gzip → base64url，放在圖台網址的 #d= 後面。
    /// 圖台（js/app.js 的 decodeData）讀網址的 #d= 還原 JSON，# 後面的內容不會送到伺服器。不依賴 Revit API。
    /// </summary>
    public static class DashboardLink
    {
        /// <summary>沒有設定檔時使用的圖台網址。</summary>
        public const string DefaultBaseUrl = "https://lebr-g1-carbon-dashboard.netlify.app/";

        /// <summary>外掛資料夾裡的設定檔，第一行寫圖台網址，可改成自己的圖台。</summary>
        public const string SettingFile = "dashboard_url.txt";

        /// <summary>
        /// 網址超過這個長度時改用「匯出 JSON ＋ 開啟空白圖台」：
        /// Windows 用預設瀏覽器開網址時經過命令列，上限約 32,767 字元。
        /// </summary>
        public const int MaxUrlLength = 30000;

        /// <summary>讀外掛資料夾的 dashboard_url.txt；沒有檔案、空白或不是 http(s) 網址時用預設值。</summary>
        public static string ResolveBaseUrl(string addinFolder)
        {
            try
            {
                var path = Path.Combine(addinFolder ?? "", SettingFile);
                if (File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        var url = line.Trim();
                        if (url.Length == 0 || url.StartsWith("#")) continue;
                        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                            return url.Split('#')[0];
                        break;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return DefaultBaseUrl;
        }

        /// <summary>圖台網址加上 #d=（gzip 後的 base64url 結果 JSON）。</summary>
        public static string Build(string baseUrl, string resultJson)
        {
            if (resultJson == null) throw new ArgumentNullException("resultJson");
            return (baseUrl ?? DefaultBaseUrl).Split('#')[0] + "#d=" + Encode(resultJson);
        }

        public static string Encode(string json)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(json);
                    gz.Write(bytes, 0, bytes.Length);
                }
                return Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
        }

        /// <summary>Encode 的反向，供測試與除錯使用。</summary>
        public static string Decode(string encoded)
        {
            var s = encoded.Replace('-', '+').Replace('_', '/');
            s += new string('=', (4 - s.Length % 4) % 4);
            using (var input = new MemoryStream(Convert.FromBase64String(s)))
            using (var gz = new GZipStream(input, CompressionMode.Decompress))
            using (var reader = new StreamReader(gz, new UTF8Encoding(false)))
                return reader.ReadToEnd();
        }
    }
}
