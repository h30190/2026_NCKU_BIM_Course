using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;

namespace RfiPlugin
{
    /// <summary>
    /// 「建立議題」指令（SPEC Ch4）：
    /// 1. 讀目前選取（取第一個；沒選就提示先選）
    /// 2. 匯出使用中視圖截圖（ImageExportOptions）
    /// 3. 小視窗輸入標題＋說明＋回報人（WinForms）
    /// 4. HttpClient POST 到 POST /api/issues（multipart：欄位＋screenshot）
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class CreateIssueCommand : IExternalCommand
    {
        // 課堂內網 demo 用；各組做自己題目時改成自己的後端位址。
        private const string BackendBase = "http://localhost:8080";

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiDoc = data.Application.ActiveUIDocument;
            if (uiDoc == null) { message = "請先開啟一個專案文件"; return Result.Failed; }
            var doc = uiDoc.Document;

            // 1. 選取
            var selIds = uiDoc.Selection.GetElementIds().ToList();
            if (selIds.Count == 0)
            {
                TaskDialog.Show("RFI", "請先在模型裡選一個元件，再按「建立議題」。");
                return Result.Cancelled;
            }
            string elementId = selIds[0].Value.ToString();

            // 1b. 使用中視圖（名稱＋UniqueId 一起記；圖台顯示用名，Revit 開視圖用 Id）
            var activeView = doc.ActiveView;
            string viewName = activeView != null ? activeView.Name : "";
            string viewUid = activeView != null ? activeView.UniqueId : "";
            // 1c. 所屬檔案/模型名（樹狀分組用；未存檔顯示專案名如「專案1」）
            string modelName = doc.Title;

            // 2. 截圖：匯出使用中視圖
            string shotPath = Path.Combine(Path.GetTempPath(), $"rfi_{DateTime.Now:yyyyMMddHHmmss}.png");
            try
            {
                var opt = new ImageExportOptions
                {
                    ExportRange = ExportRange.CurrentView,
                    FilePath = shotPath,
                    ImageResolution = ImageResolution.DPI_150,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                };
                doc.ExportImage(opt);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("RFI", "截圖匯出失敗：" + ex.Message);
                return Result.Failed;
            }

            // 3. 輸入標題＋說明（回報人預設 Revit 選項 General 的 Username，未設退回 Windows 帳號）
            string login = data.Application.Application.Username;
            if (string.IsNullOrWhiteSpace(login)) login = Environment.UserName;
            using (var form = new IssueForm(login))
            {
                if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return Result.Cancelled;

                // 4. POST 到後端（寫入要登入：token 從圖台登入後複製）
                if (string.IsNullOrWhiteSpace(form.Token))
                {
                    TaskDialog.Show("RFI sample", "請先到圖台網頁登入，複製 token 貼上來（只貼一次，會記住）。");
                    return Result.Cancelled;
                }
                try
                {
                    using (var http = new HttpClient())
                    {
                        http.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", form.Token);
                        using (var content = new MultipartFormDataContent())
                        {
                        content.Add(new StringContent(form.IssueTitle), "title");
                        content.Add(new StringContent(form.IssueDescription ?? ""), "description");
                        content.Add(new StringContent(form.Reporter ?? ""), "reporter");
                        content.Add(new StringContent("revit"), "source");
                        content.Add(new StringContent(elementId), "revit_element_id");
                        content.Add(new StringContent(viewName), "view_name");
                        content.Add(new StringContent(viewUid), "revit_view_id");
                        content.Add(new StringContent(modelName ?? ""), "model_name");
                        if (File.Exists(shotPath))
                        {
                            var bytes = File.ReadAllBytes(shotPath);
                            var img = new ByteArrayContent(bytes);
                            img.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                            content.Add(img, "screenshot", Path.GetFileName(shotPath));
                        }
                        var resp = http.PostAsync(BackendBase + "/api/issues", content).Result;
                        string body = resp.Content.ReadAsStringAsync().Result;
                        if (!resp.IsSuccessStatusCode)
                        {
                            TaskDialog.Show("RFI", $"後端回錯誤 {(int)resp.StatusCode}：{body}");
                            return Result.Failed;
                        }
                        var m = System.Text.RegularExpressions.Regex.Match(body, @"""id""\s*:\s*(\d+)");
                        string newId = m.Success ? m.Groups[1].Value : "?";
                        TokenStore.Save(form.Token); // token 有效才記
                        TaskDialog.Show("RFI", $"議題已送出！圖台編號 #{newId}\nElementId = {elementId}\n視圖 = {viewName}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("RFI", "送出失敗（後端有跑嗎？go run ./backend）：" + ex.Message);
                    return Result.Failed;
                }
            }
            return Result.Succeeded;
        }
    }
}
