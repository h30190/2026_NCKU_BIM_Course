using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TemporalBim
{
    /// <summary>
    /// 跟後端(backend)溝通的小工具。
    /// 每個 await 後面都加了 ConfigureAwait(false)——因為①③這兩個指令是直接在
    /// Execute() 裡用 .Result/.Wait() 同步等待這些方法跑完（Revit 的 IExternalCommand
    /// 本身不能宣告成 async），如果不加 ConfigureAwait(false)，await 後面的程式碼會想
    /// 跳回呼叫端那條執行緒（Revit 主執行緒）才能繼續，但那條執行緒正被 .Result 卡住
    /// 等待——兩邊互等，整個 Revit 直接卡死。加了 ConfigureAwait(false) 之後，
    /// await 後面的程式碼改在背景執行緒繼續跑，.Result 才等得到結果。
    /// </summary>
    public static class BackendClient
    {
        private const string BackendBase = "http://127.0.0.1:8080";

        private static HttpClient NewClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.Add("X-Green-Passport-Client", "RevitPlugin");
            return http;
        }

        public static async Task<VersionInfo> CreateSnapshotAsync(SnapshotRequest req)
        {
            using (var http = NewClient())
            {
                string json = JsonConvert.SerializeObject(req);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await http.PostAsync(BackendBase + "/snapshot", content).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new BackendException((int)resp.StatusCode, body);
                return JsonConvert.DeserializeObject<VersionInfo>(body);
            }
        }

        public static async Task<List<VersionInfo>> ListVersionsAsync()
        {
            using (var http = NewClient())
            {
                string body = await http.GetStringAsync(BackendBase + "/versions").ConfigureAwait(false);
                return JsonConvert.DeserializeObject<List<VersionInfo>>(body);
            }
        }

        public static async Task<DiffResult> GetDiffAsync(long fromId, long toId)
        {
            using (var http = NewClient())
            {
                string url = $"{BackendBase}/diff?from={fromId}&to={toId}";
                var resp = await http.GetAsync(url).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new BackendException((int)resp.StatusCode, body);
                return JsonConvert.DeserializeObject<DiffResult>(body);
            }
        }

        public static async Task<PassportInfo> CreatePassportAsync(PassportInfo req)
        {
            using (var http = NewClient())
            {
                string json = JsonConvert.SerializeObject(req);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await http.PostAsync(BackendBase + "/passports", content).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new BackendException((int)resp.StatusCode, body);
                return JsonConvert.DeserializeObject<PassportInfo>(body);
            }
        }

        public static async Task<List<PassportInfo>> ListPassportsAsync(string status = null)
        {
            using (var http = NewClient())
            {
                string url = BackendBase + "/passports";
                if (!string.IsNullOrEmpty(status)) url += "?status=" + status;
                string body = await http.GetStringAsync(url).ConfigureAwait(false);
                var passports = JsonConvert.DeserializeObject<List<PassportInfo>>(body) ?? new List<PassportInfo>();
                foreach (var passport in passports) PassportAutoFill.Apply(passport);
                return passports;
            }
        }

        public static async Task<List<MatchResultInfo>> MatchPassportsAsync(MatchRequestInfo req)
        {
            using (var http = NewClient())
            {
                string json = JsonConvert.SerializeObject(req);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await http.PostAsync(BackendBase + "/passports/match", content).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new BackendException((int)resp.StatusCode, body);
                return JsonConvert.DeserializeObject<List<MatchResultInfo>>(body);
            }
        }

        public static async Task<PassportInfo> ReviewPassportAsync(long id, ReviewPassportInfo review)
        {
            using (var http = NewClient())
            {
                string json = JsonConvert.SerializeObject(review);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var req = new HttpRequestMessage(new HttpMethod("PATCH"), $"{BackendBase}/passports/{id}/review") { Content = content };
                var resp = await http.SendAsync(req).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new BackendException((int)resp.StatusCode, body);
                var passport = JsonConvert.DeserializeObject<PassportInfo>(body);
                PassportAutoFill.Apply(passport);
                return passport;
            }
        }

        public static async Task<PassportInfo> PatchPassportAsync(
            long id,
            string status = null,
            string targetModel = null,
            string targetElementId = null,
            string targetUniqueId = null,
            string originalUniqueId = null,
            string reason = null,
            string actor = null,
            string originalTypeId = null,
            string newTypeId = null,
            int? expectedRevision = null)
        {
            using (var http = NewClient())
            {
                var fields = new Dictionary<string, object>();
                if (expectedRevision.HasValue) fields["expectedRevision"] = expectedRevision.Value;
                if (status != null) fields["status"] = status;
                if (targetModel != null) fields["targetModel"] = targetModel;
                if (targetElementId != null) fields["targetElementId"] = targetElementId;
                if (targetUniqueId != null) fields["targetUniqueId"] = targetUniqueId;
                if (originalUniqueId != null) fields["originalUniqueId"] = originalUniqueId;
                if (reason != null) fields["reason"] = reason;
                if (actor != null) fields["actor"] = actor;
                if (originalTypeId != null) fields["originalTypeId"] = originalTypeId;
                if (newTypeId != null) fields["newTypeId"] = newTypeId;
                string json = JsonConvert.SerializeObject(fields);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var req = new HttpRequestMessage(new HttpMethod("PATCH"), $"{BackendBase}/passports/{id}") { Content = content };
                var resp = await http.SendAsync(req).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new BackendException((int)resp.StatusCode, body);
                return JsonConvert.DeserializeObject<PassportInfo>(body);
            }
        }
    }

    /// <summary>
    /// .Result / .Wait() 丟出來的例外會被包成 AggregateException，
    /// 直接讀 .Message 只會看到「發生一或多項錯誤」這種沒用的制式文字。
    /// 這個方法把包裝拆開，回傳真正的錯誤內容，catch 區塊都呼叫它取代 ex.Message。
    /// </summary>
    public static class ErrorText
    {
        public static string RealMessage(Exception ex)
        {
            if (ex is AggregateException agg)
            {
                var flat = agg.Flatten();
                return string.Join("；", flat.InnerExceptions.Select(e => e.Message));
            }
            return ex.Message;
        }
    }

    public class BackendException : System.Exception
    {
        public int StatusCode { get; }
        public BackendException(int statusCode, string body) : base($"後端回錯誤 {statusCode}：{body}")
        {
            StatusCode = statusCode;
        }
    }
}
