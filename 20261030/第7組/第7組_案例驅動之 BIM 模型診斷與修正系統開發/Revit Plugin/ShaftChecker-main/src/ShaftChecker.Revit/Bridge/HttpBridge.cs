using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Autodesk.Revit.UI;
using ShaftChecker.Revit.Services;

namespace ShaftChecker.Revit.Bridge
{
    /// <summary>
    /// 一個待 Revit 主執行緒執行的工作。HTTP 執行緒建立工作後等待 Done，
    /// ExternalEvent 觸發時由 BridgeEventHandler 在主執行緒執行。
    /// </summary>
    internal class BridgeJob
    {
        public Func<UIApplication, object> Work;
        public object Result;
        public Exception Error;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    }

    /// <summary>Revit API 只能在主執行緒呼叫；所有 HTTP 請求都經由這個 handler 執行。</summary>
    internal class BridgeEventHandler : IExternalEventHandler
    {
        public readonly ConcurrentQueue<BridgeJob> Queue = new ConcurrentQueue<BridgeJob>();

        public void Execute(UIApplication app)
        {
            BridgeJob job;
            while (Queue.TryDequeue(out job))
            {
                try { job.Result = job.Work(app); }
                catch (Exception ex) { job.Error = ex; }
                finally { job.Done.Set(); }
            }
        }

        public string GetName()
        {
            return "ShaftChecker AI Bridge";
        }
    }

    /// <summary>
    /// 本機 HTTP 入口（只監聽 http://localhost:PORT/），讓 MCP 伺服器呼叫檢討功能。
    /// 不對外開放網路，非系統管理員也能啟動。
    /// </summary>
    public static class HttpBridge
    {
        private static HttpListener _listener;
        private static Thread _thread;
        private static ExternalEvent _event;
        private static BridgeEventHandler _handler;

        public static int Port { get; private set; }
        public static bool IsRunning { get { return _listener != null && _listener.IsListening; } }
        public static string LastError { get; private set; }

        /// <summary>必須在 Revit API 有效的情境（例如 OnStartup）呼叫，因為要建立 ExternalEvent。</summary>
        public static void Start(int port)
        {
            if (IsRunning) return;
            Port = port;

            _handler = new BridgeEventHandler();
            _event = ExternalEvent.Create(_handler);

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://localhost:" + port + "/");
                _listener.Start();
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = "無法監聽 localhost:" + port + "：" + ex.Message;
                _listener = null;
                return;
            }

            _thread = new Thread(Loop) { IsBackground = true, Name = "ShaftChecker HTTP" };
            _thread.Start();
        }

        public static void Stop()
        {
            try { if (_listener != null) _listener.Stop(); } catch (Exception) { }
            _listener = null;
            if (_event != null) { _event.Dispose(); _event = null; }
        }

        private static void Loop()
        {
            while (IsRunning)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (Exception) { break; } // Stop() 時會丟例外
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        private static void Handle(HttpListenerContext ctx)
        {
            object response;
            try
            {
                string path = ctx.Request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();

                // 檢討儀表板（靜態網頁，不需經過 Revit 主執行緒）
                if (path == "" || path == "/ui")
                {
                    WriteHtml(ctx);
                    return;
                }

                Dictionary<string, object> body = ReadBody(ctx.Request);
                Func<UIApplication, object> work = Route(path, body);

                response = work == null
                    ? ShaftCheckService.Error("未知的端點：" + path +
                        "。可用：/status /rules /shafts /check /highlight /clear /select /report /detect /confirm /testmodel")
                    : RunOnRevitThread(work, path == "/status" ? 10 : 180);
            }
            catch (Exception ex)
            {
                response = ShaftCheckService.Error(ex.Message);
            }

            Write(ctx, response);
        }

        private static Func<UIApplication, object> Route(string path, Dictionary<string, object> body)
        {
            switch (path)
            {
                case "/status": return app => ShaftCheckService.Status(app);
                case "/rules": return app => ShaftCheckService.Rules();
                case "/shafts": return app => ShaftCheckService.Shafts(app, body);
                case "/check": return app => ShaftCheckService.Check(app, body);
                case "/highlight": return app => ShaftCheckService.Highlight(app);
                case "/clear": return app => ShaftCheckService.Clear(app);
                case "/select": return app => ShaftCheckService.Select(app, body);
                case "/report": return app => ShaftCheckService.Report(app);
                case "/detect": return app => ShaftCheckService.Detect(app, body);
                case "/confirm": return app => ShaftCheckService.Confirm(app, body);
                case "/testmodel": return app => TestModelBuilder.Build(app);
                default: return null;
            }
        }

        private static object RunOnRevitThread(Func<UIApplication, object> work, int timeoutSeconds)
        {
            var job = new BridgeJob { Work = work };
            _handler.Queue.Enqueue(job);
            ExternalEventRequest req = _event.Raise();
            if (req == ExternalEventRequest.Denied || req == ExternalEventRequest.TimedOut)
                return ShaftCheckService.Error("Revit 拒絕了請求（" + req + "），請稍後再試。");

            if (!job.Done.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
                return ShaftCheckService.Error(
                    "Revit 在 " + timeoutSeconds + " 秒內沒有回應。可能正開著對話框或正在執行其他指令，請回到 Revit 關閉對話框後再試。");

            if (job.Error != null) return ShaftCheckService.Error(job.Error.Message);
            return job.Result;
        }

        /// <summary>同時接受 POST 的 JSON body 與網址參數（方便用瀏覽器測試，例如 /shafts?level=3F）。</summary>
        private static Dictionary<string, object> ReadBody(HttpListenerRequest req)
        {
            var body = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            if (req.HasEntityBody)
            {
                using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
                {
                    string text = reader.ReadToEnd();
                    foreach (var kv in MiniJson.ParseObject(text)) body[kv.Key] = kv.Value;
                }
            }

            foreach (string key in req.QueryString.AllKeys)
            {
                if (key != null && !body.ContainsKey(key)) body[key] = req.QueryString[key];
            }
            return body;
        }

        public static string DashboardUrl
        {
            get { return "http://localhost:" + Port + "/"; }
        }

        private static void WriteHtml(HttpListenerContext ctx)
        {
            try
            {
                string file = Path.Combine(Utils.AddinPaths.AddinFolder, "ui", "index.html");
                byte[] bytes = File.Exists(file)
                    ? File.ReadAllBytes(file)
                    : Encoding.UTF8.GetBytes("<meta charset=utf-8><p>找不到儀表板檔案：" + WebUtility.HtmlEncode(file) + "</p>");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.Headers["Cache-Control"] = "no-cache";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch (Exception)
            {
                // 用戶端已中斷連線
            }
        }

        private static void Write(HttpListenerContext ctx, object response)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(MiniJson.Serialize(response));
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch (Exception)
            {
                // 用戶端已中斷連線
            }
        }
    }
}
