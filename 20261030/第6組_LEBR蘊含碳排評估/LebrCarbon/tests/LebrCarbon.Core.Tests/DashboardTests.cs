using System.Net;
using System.Text.Json;
using LebrCarbon.Core.Lebr;
using LebrCarbon.Dashboard;
using LebrCarbon.Data;

namespace LebrCarbon.Core.Tests;

/// <summary>網頁控制台傳送層：快照數字與 LebrResult 一致、JSON 格式、後端沒開時的處理。</summary>
public class DashboardTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void 由專案1輸入檔重算_與Revit實測一致()
    {
        var file = ProjectStore.TryLoad(Fixture("專案1.lebr.json"))!;
        var r = ProjectEvaluator.Evaluate(file, BlccLibrary.Load());

        Assert.InRange(r.EEC, 119_525.5, 119_526.5);
        Assert.InRange(r.EECc, 145_860.5, 145_861.5);
        Assert.InRange(r.CFR, 0.18045, 0.18055);   // 18.05%
        Assert.Equal("1級", r.Grade);
    }

    [Fact]
    public void 快照數字直接取自LebrResult()
    {
        var r = LebrCalculator.Evaluate(LebrExampleTests.Inputs(), LebrExampleTests.Design(), LebrExampleTests.Baseline());
        var s = SnapshotBuilder.Build(r, "Z社宅", "高雄市 Z 社會住宅", "附錄三實例");

        Assert.Equal(r.EEC, s.Result.Eec);
        Assert.Equal(r.EECc, s.Result.Eecc);
        Assert.Equal(r.CFR, s.Result.Cfr);
        Assert.Equal("1+級", s.Result.Grade);
        Assert.Equal(LebrTables.ScaleTicks.Count, s.Result.Scale.Count);   // 7 個刻度分出 8 個等級

        // 四階段合計扣優惠、除延壽後等於 EEC；基準案合計等於 EECc
        Assert.Equal(r.EEC, (s.Result.Stages.Design.Total - s.Result.Credits) / (1 + s.Result.Ll), 3);
        Assert.Equal(r.EECc, s.Result.Stages.Baseline.Total, 3);

        Assert.Equal(r.DesignItems.Count, s.Items.Count(i => i.Side == "design"));
        Assert.Equal(r.BaselineItems.Count, s.Items.Count(i => i.Side == "baseline"));
        Assert.Equal(r.CFns + r.CFrm, s.Items.Where(i => i.Side == "design").Sum(i => i.Carbon), 3);
    }

    [Fact]
    public void 快照JSON欄位為camelCase且可來回轉換()
    {
        var r = LebrCalculator.Evaluate(LebrExampleTests.Inputs(), LebrExampleTests.Design(), LebrExampleTests.Baseline());
        var json = SnapshotBuilder.ToJson(SnapshotBuilder.Build(r, "k", "t", "n"));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("k", root.GetProperty("project").GetProperty("key").GetString());
        Assert.Equal("n", root.GetProperty("snapshot").GetProperty("name").GetString());
        Assert.Equal("1+級", root.GetProperty("result").GetProperty("grade").GetString());
        Assert.Equal("ow", root.GetProperty("items")[0].GetProperty("category").GetString());
        Assert.Equal("RC", root.GetProperty("inputs").GetProperty("structure").GetString());

        var back = SnapshotBuilder.FromJson(json)!;
        Assert.Equal(r.EEC, back.Result.Eec);
    }

    [Fact]
    public async Task 後端沒開時回傳提示_不丟例外()
    {
        using var client = new DashboardClient(handler: new FakeHandler(_ => throw new HttpRequestException("refused")));
        var r = await client.UploadAsync(MinimalSnapshot());

        Assert.False(r.Ok);
        Assert.Contains("控制台後端沒有運行", r.Message);
    }

    [Fact]
    public async Task 上傳成功取回快照ID_格式版本不符時提示()
    {
        using var ok = new DashboardClient(handler: new FakeHandler(req =>
            req.RequestUri!.AbsolutePath == "/api/health" ? Json(HttpStatusCode.OK, """{"ok":"true"}""")
                                                         : Json(HttpStatusCode.Created, """{"id":7,"projectId":1}""")));
        var r = await ok.UploadAsync(MinimalSnapshot());
        Assert.True(r.Ok);
        Assert.Equal(7, r.SnapshotId);

        using var old = new DashboardClient(handler: new FakeHandler(req =>
            req.RequestUri!.AbsolutePath == "/api/health" ? Json(HttpStatusCode.OK, "{}")
                                                         : Json(HttpStatusCode.BadRequest, """{"error":"unsupported schemaVersion 2"}""")));
        var bad = await old.UploadAsync(MinimalSnapshot());
        Assert.False(bad.Ok);
        Assert.Contains("版本不符", bad.Message);
    }

    private static SnapshotDocument MinimalSnapshot()
    {
        var r = LebrCalculator.Evaluate(LebrExampleTests.Inputs(), LebrExampleTests.Design(), LebrExampleTests.Baseline());
        return SnapshotBuilder.Build(r, "k", "t", "n");
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
