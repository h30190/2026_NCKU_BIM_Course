using System.Net;
using LebrCarbon.Dashboard;
using LebrCarbon.Data;

namespace LebrCarbon.Core.Tests;

/// <summary>套用控制台建議：建議換算成類型參數 LEBR_BlccId 的修改，以及外掛與後端的往返。</summary>
public class ProposalTests
{
    private static readonly BlccLibrary Lib = BlccLibrary.Load();

    // 專案1 的三個類型
    private static readonly TypeBlccIds[] Types =
    {
        new(257482, "LEBR測試 RC外牆15cm 貼磁磚", "牆", "CW-0;OW-1"),
        new(257484, "LEBR測試 輕隔間 138mm", "牆", "IW-3"),
        new(257486, "LEBR測試 鋁窗", "窗", "W-11-AL"),
    };

    [Fact]
    public void 只換對應的代碼_保留同類型的其他代碼()
    {
        var (edits, missing) = BlccIdEditor.Plan(Types, new[] { new ScenarioChange("CW-0", "CW-2") }, Lib);
        Assert.Empty(missing);
        var e = Assert.Single(edits);
        Assert.Equal(257482, e.TypeId);
        Assert.Equal("CW-0;OW-1", e.Old);
        Assert.Equal("CW-2;OW-1", e.New);
    }

    [Fact]
    public void 換成帷幕牆並拿掉外裝()
    {
        var (edits, _) = BlccIdEditor.Plan(Types, new[] { new ScenarioChange("CW-0", "CW-2", DropFinish: true) }, Lib);
        Assert.Equal("CW-2", Assert.Single(edits).New);
    }

    [Fact]
    public void 多筆替換與找不到的項目()
    {
        var (edits, missing) = BlccIdEditor.Plan(Types, new[]
        {
            new ScenarioChange("IW-3", "IW-1"),
            new ScenarioChange("IW-3", "IW-1", TypeName: "不存在的類型"),
            new ScenarioChange("F-9", "IW-1"),
            new ScenarioChange("IW-3", "XX-0"),
        }, Lib);

        Assert.Equal("IW-1", Assert.Single(edits).New);
        Assert.Equal(3, missing.Count);
        Assert.Contains(missing, m => m.Contains("不存在的類型"));
        Assert.Contains(missing, m => m.Contains("XX-0"));
    }

    [Fact]
    public void 沒有實際變更的類型不列入()
    {
        var types = new[] { new TypeBlccIds(1, "A", "牆", "IW-1 ; ") };
        var (edits, _) = BlccIdEditor.Plan(types, new[] { new ScenarioChange("IW-1", "IW-1") }, Lib);
        Assert.Empty(edits);
    }

    [Fact]
    public async Task 取回所有選定建議()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/health" => (HttpStatusCode.OK, "{}"),
            "/api/proposals/chosen" when req.RequestUri.Query.Contains(Uri.EscapeDataString("專案1")) => (HttpStatusCode.OK, """
                [{"id":"p1","project":"專案1","title":"內隔間改輕質","status":"chosen",
                  "changes":[{"from":"IW-3","to":"IW-1"}],"expected":{"cfr":0.1614,"grade":"1級"}},
                 {"id":"p2","project":"專案1","title":"窗框改塑鋼","status":"chosen",
                  "changes":[{"from":"W-11-AL","to":"W-1-PL"}]}]
                """),
            _ => (HttpStatusCode.OK, "[]"),
        });
        using var client = new DashboardClient("http://test", handler);

        var hit = await client.GetChosenProposalsAsync("專案1");
        Assert.True(hit.Ok);
        Assert.Equal(new[] { "p1", "p2" }, hit.Proposals.Select(p => p.Id));
        Assert.Equal("IW-1", Assert.Single(hit.Proposals[0].Changes).To);
        Assert.Equal("1級", hit.Proposals[0].Expected!.Grade);

        var none = await client.GetChosenProposalsAsync("專案2");
        Assert.True(none.Ok);
        Assert.Empty(none.Proposals);
    }

    [Fact]
    public void 多份建議合併_相同替換只留一個()
    {
        Proposal P(string id, params ScenarioChange[] cs) => new() { Id = id, Project = "專案1", Title = id, Changes = cs.ToList() };
        var merged = ProposalMerge.Changes(new[]
        {
            P("a", new ScenarioChange("IW-3", "IW-1"), new ScenarioChange("W-11-AL", "W-1-PL")),
            P("b", new ScenarioChange("iw-3 ", "IW-1"), new ScenarioChange("CW-0", "CW-2", DropFinish: true)),
        });
        Assert.Equal(new[] { "IW-3", "W-11-AL", "CW-0" }, merged.Select(c => c.From));

        // 合併後三個類型都改到，重複的替換不會變成「找不到」
        var (edits, missing) = BlccIdEditor.Plan(Types, merged, Lib);
        Assert.Empty(missing);
        Assert.Equal(3, edits.Count);
    }

    [Fact]
    public async Task 回報已套用()
    {
        string? body = null;
        var handler = new StubHandler(req =>
        {
            Assert.Equal(HttpMethod.Patch, req.Method);
            Assert.Equal("/api/proposals/p1/applied", req.RequestUri!.AbsolutePath);
            body = req.Content!.ReadAsStringAsync().Result;
            return (HttpStatusCode.OK, "{}");
        });
        using var client = new DashboardClient("http://test", handler);

        var r = await client.MarkAppliedAsync("p1", new ProposalResult(0.1614, "1級", 122309));
        Assert.True(r.Ok);
        using var json = System.Text.Json.JsonDocument.Parse(body!);
        var applied = json.RootElement.GetProperty("applied");
        Assert.Equal(0.1614, applied.GetProperty("cfr").GetDouble());
        Assert.Equal("1級", applied.GetProperty("grade").GetString());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (code, json) = respond(request);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(json) });
        }
    }
}
