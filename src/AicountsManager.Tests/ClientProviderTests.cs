using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AicountsManager;

namespace AicountsManager.Tests;
internal static partial class Program
{
    private sealed class SessionFixture : IClientSessionSource
    {
        public ClientSession? Value = new("test-access", "person@example.test", "pro");
        public int Reads;
        public ClientSession? Read() { Reads++; return Value; }
    }
    private sealed class SessionHttp : HttpMessageHandler
    {
        public int Calls;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return Reply(request, token); }
    }
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static JsonElement Element(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
    private static string ClaudeFixture() => $$$"""{"five_hour":{"utilization":14,"resets_at":"{{{DateTimeOffset.UtcNow.AddHours(4):O}}}"},"seven_day":{"utilization":30,"resets_at":"{{{DateTimeOffset.UtcNow.AddDays(6):O}}}"},"seven_day_sonnet":null,"limits":[{"kind":"weekly_scoped","scope":{"model":{"display_name":"Fable"}},"percent":8,"resets_at":"{{{DateTimeOffset.UtcNow.AddDays(6):O}}}"}],"extra_usage":{"is_enabled":true,"monthly_limit":10000,"used_credits":500}}""";
    private static string CursorFixture() => $$$"""{"enabled":true,"billingCycleStart":{{{DateTimeOffset.UtcNow.AddDays(-8).ToUnixTimeMilliseconds()}}},"billingCycleEnd":{{{DateTimeOffset.UtcNow.AddDays(22).ToUnixTimeMilliseconds()}}},"planUsage":{"totalPercentUsed":35,"autoPercentUsed":20,"apiPercentUsed":50,"limit":2000,"totalSpend":700}}""";
    private static string CursorJwt(string user) => "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = "auth0|" + user }))).TrimEnd('=').Replace('+','-').Replace('/','_') + ".fixture";

    private static Task ClientQuotaParsingAsync()
    {
        var claude = ClaudeQuotaProvider.Parse(Element(ClaudeFixture()));
        Check(claude.Count == 4 && claude.Single(q => q.Id == "five_hour").Minutes == 300 && claude.Single(q => q.Group == "Fable").Minutes == 10080, "Claude windows or scoped limits were not mapped.");
        Check(claude.Single(q => q.Id == "extra").UsedPercent == 5 && claude.Single(q => q.Id == "extra").Minutes is null, "Extra credit meter was assigned a fake weekly period.");
        Check(ClaudeQuotaProvider.Parse(Element("{\"five_hour\":null,\"seven_day\":{\"utilization\":\"bad\"}}")).Count == 0, "Missing Claude usage became measured zero.");
        var cursor = CursorQuotaProvider.Parse(Element(CursorFixture()));
        Check(cursor.Count == 3 && cursor.All(q => q.Minutes == 43200) && cursor[0].UsedPercent == 35, "Cursor billing cycle differs from source.");
        Check(cursor.All(q => QuotaPace.WeeklyReference(q.Window, DateTimeOffset.UtcNow) is null), "Monthly Cursor usage received a weekly pace indicator.");
        var team = CursorQuotaProvider.Parse(Element("{\"membershipType\":\"team\",\"planUsage\":{\"limit\":2000,\"totalSpend\":2500,\"autoPercentUsed\":10,\"apiPercentUsed\":20}}"));
        Check(team.Count == 2 && team.All(q => q.Id != "cycle"), "Team model pools invented a combined capacity.");
        var pooled = CursorQuotaProvider.Parse(Element("{\"spendLimitUsage\":{\"pooledLimit\":10000},\"planUsage\":{\"limit\":2000,\"totalSpend\":2500,\"autoPercentUsed\":10,\"apiPercentUsed\":20}}"));
        Check(pooled.Count == 2 && pooled.All(q => q.Id != "cycle"), "Pooled enterprise allowance invented a personal total.");
        Check(new ClaudeSessionSource("invalid\0path").Read() is null, "Malformed optional client path crashed the application.");
        var eventWithoutTokens = CursorTokenHistory.Parse(Element("{\"timestamp\":1792627200000,\"model\":\"cursor-tab\"}"));
        Check(eventWithoutTokens?.Tokens == 0, "A valid non-token event invalidated the whole Cursor history.");
        var fallback = CursorQuotaProvider.Parse(Element("{\"individualUsage\":{\"plan\":{\"totalPercentUsed\":42,\"autoPercentUsed\":10,\"apiPercentUsed\":80}}}"), true);
        Check(fallback.Count == 3 && fallback[0].UsedPercent == 42 && fallback[0].Minutes is null, "REST fallback changed units or invented reset.");
        return Task.CompletedTask;
    }
    private static async Task ClientQuotaTransportAsync()
    {
        var source = new SessionFixture(); var handler = new SessionHttp(); using var http = new HttpClient(handler);
        handler.Reply = (r, _) =>
        {
            Check(r.Method == HttpMethod.Get && r.RequestUri!.Host == "api.anthropic.com", "Claude sent an unexpected request.");
            Check(r.Headers.Authorization?.Parameter == "test-access" && r.Headers.GetValues("anthropic-beta").Single() == "oauth-2025-04-20", "Claude headers incorrect.");
            return Task.FromResult(Response(r.RequestUri!.AbsolutePath.EndsWith("profile") ? "{\"account\":{\"uuid\":\"one\",\"email\":\"a@example.test\"},\"organization\":{\"organization_type\":\"claude_max\",\"rate_limit_tier\":\"default_claude_max_5x\"}}" : ClaudeFixture()));
        };
        var claude = new ClaudeQuotaProvider(source, http);
        var read = await QuotaQuery.ReadAsync(claude, new CurrentQuotaRequest(), default);
        Check(read.Status == "ok" && read.Email == "a@example.test" && read.Plan == "max 5x", "Claude session/profile flow failed.");
        source.Value = null; var before = handler.Calls;
        Check((await claude.ReadAsync(new CurrentQuotaRequest(), default)).Status == "disconnected" && handler.Calls == before, "Missing login made a network request.");
        source.Value = new(CursorJwt("one"));
        handler.Reply = async (r, _) =>
        {
            if (r.RequestUri!.Host == "api2.cursor.sh")
            { Check(r.Headers.Contains("Connect-Protocol-Version") && await r.Content!.ReadAsStringAsync() == "{}", "Cursor Connect RPC format invalid."); return Response("{}"); }
            Check(r.RequestUri.Host == "cursor.com" && r.Headers.GetValues("Cookie").Single().StartsWith("WorkosCursorSessionToken=one%3A%3A"), "Cursor session cookie not scoped to signed-in user.");
            return Response("{\"individualUsage\":{\"plan\":{\"totalPercentUsed\":42}}}");
        };
        var cursor = new CursorQuotaProvider(source, http);
        Check((await QuotaQuery.ReadAsync(cursor,new CurrentQuotaRequest(),default)).Quotas.Single().UsedPercent == 42, "Cursor fallback did not load usage.");
        handler.Reply = (_, _) => { source.Value = new(CursorJwt("changed")); return Task.FromResult(Response(CursorFixture())); };
        Check((await cursor.ReadAsync(new CurrentQuotaRequest(),default)).Status == "disconnected", "Session change received previous account data.");
    }
    private static Task ClientCredentialReadOnlyAsync() => InTemporaryStoreAsync(async paths =>
    {
        var home = Path.Combine(paths.AppHome,"claude"); Directory.CreateDirectory(home);
        var file = Path.Combine(home,".credentials.json");
        await File.WriteAllTextAsync(file,"{\"claudeAiOauth\":{\"accessToken\":\"fake-token\",\"refreshToken\":\"never-used\",\"subscriptionType\":\"max\",\"rateLimitTier\":\"max_20x\"},\"mcp\":{\"keep\":true}}");
        var before = File.ReadAllBytes(file); var session = new ClaudeSessionSource(home).Read();
        Check(session?.AccessToken == "fake-token" && session.Plan == "max 20x" && before.SequenceEqual(File.ReadAllBytes(file)), "Claude credentials were changed or parsed incorrectly.");
        Check(!session!.ToString().Contains("fake-token"), "Session diagnostic string exposes access token.");
        var db = Path.Combine(paths.AppHome,"state.vscdb"); var jwt = CursorJwt("one");
        WriteTestDb(db,$"CREATE TABLE ItemTable(key TEXT PRIMARY KEY,value TEXT); INSERT INTO ItemTable VALUES('cursorAuth/accessToken','{jwt}'),('cursorAuth/cachedEmail','a@example.test'),('cursorAuth/stripeMembershipType','pro');");
        before = File.ReadAllBytes(db); var cursor = new CursorSessionSource(db).Read();
        Check(cursor?.Email == "a@example.test" && cursor.AccessToken == jwt && before.SequenceEqual(File.ReadAllBytes(db)), "Cursor store read modified the database.");
        await File.WriteAllTextAsync(file,"{}"); Check(new ClaudeSessionSource(home).Read() is null, "Malformed credentials were reused.");
    });
    private static Task ClientDisabledAndRateLimitAsync() => InTemporaryStoreAsync(async paths =>
    {
        var source = new SessionFixture(); var handler = new SessionHttp(); using var http = new HttpClient(handler);
        handler.Reply = (_,_) => { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10)); return Task.FromResult(response); };
        var registry = new CurrentProviderRegistry(paths,[new ClaudeQuotaProvider(source,http),new CursorQuotaProvider(source,http)]);
        await registry.RefreshAsync(default);
        Check(source.Reads == 0 && handler.Calls == 0 && registry.Settings.All(s => !s.Enabled), "Disabled providers performed background work.");
        await registry.SetEnabledAsync(ProviderIds.Claude,true,default);
        Check(handler.Calls == 1 && registry.Accounts.Single().HttpStatus == 429, "429 was not exposed.");
        await registry.RefreshAsync(default); Check(handler.Calls == 1, "Retry-After was ignored.");
        await registry.SetEnabledAsync(ProviderIds.Claude,false,default); var reads = source.Reads;
        await registry.RefreshAsync(default); Check(source.Reads == reads && registry.Accounts.Count == 0, "Disabling provider kept polling.");
        var restored = new CurrentProviderRegistry(paths,[new ClaudeQuotaProvider(source,http)]);
        Check(!restored.Settings.Single().Enabled, "Provider selection did not persist.");
    });
    private static async Task ClientFailureBoundariesAsync()
    {
        var source = new SessionFixture(); var handler = new SessionHttp(); using var http = new HttpClient(handler);
        handler.Reply = (_,_) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Check((await new ClaudeQuotaProvider(source,http).ReadAsync(new CurrentQuotaRequest(),default)).HttpStatus == 401 && handler.Calls == 1, "Unauthorized token was retried or rotated.");
        handler.Reply = async (_,token) => { await Task.Delay(Timeout.Infinite,token); return Response("{}"); };
        using var cancel = new CancellationTokenSource(50); bool cancelled=false;
        try { await new CursorQuotaProvider(source,http).ReadAsync(new CurrentQuotaRequest(),cancel.Token); } catch(OperationCanceledException) { cancelled=true; }
        Check(cancelled,"Provider request ignores cancellation.");
        handler.Reply = (_,_) => Task.FromResult(Response(new string('x',1024*1024+1)));
        bool bounded=false; try { await new ClaudeQuotaProvider(source,http).ReadAsync(new CurrentQuotaRequest(),default); } catch(InvalidDataException) { bounded=true; }
        Check(bounded,"Oversized response is not bounded.");
    }
    private static Task ClaudeIncrementalHistoryAsync() => InTemporaryStoreAsync(async paths =>
    {
        var home=Path.Combine(paths.AppHome,"claude"); var logs=Path.Combine(home,"projects","project"); Directory.CreateDirectory(logs);
        string Line(string id,int output) => JsonSerializer.Serialize(new {type="assistant",timestamp=DateTimeOffset.UtcNow.ToString("O"),message=new{id,model="claude-test",usage=new{input_tokens=100,output_tokens=output,cache_read_input_tokens=20,cache_creation_input_tokens=10}}})+"\n";
        var file=Path.Combine(logs,"session.jsonl"); var duplicate=Path.Combine(logs,"sidechain.jsonl");
        await File.WriteAllTextAsync(file,Line("one",10)+Line("one",20)); await File.WriteAllTextAsync(duplicate,Line("one",10));
        var cache=Path.Combine(paths.AppHome,"cache","claude.json"); var scanner=new ClaudeTokenHistory(home,cache);
        Check((await scanner.RefreshAsync(default)).Days.Single().Total==150,"Claude partial/duplicate messages were counted twice.");
        await scanner.RefreshAsync(default); Check(scanner.LastBytesRead==0,"Unchanged Claude history was reread.");
        await File.AppendAllTextAsync(file,Line("two",30));
        Check((await scanner.RefreshAsync(default)).Days.Single().Total==310,"Claude append changed historical usage.");
        var restarted=new ClaudeTokenHistory(home,cache);
        Check((await restarted.RefreshAsync(default)).Days.Single().Total==310 && restarted.LastBytesRead==0,"Claude index did not persist.");
        Check(!File.ReadAllText(cache).Contains("assistant") && !File.ReadAllText(cache).Contains("input_tokens"),"Claude cache includes raw log payloads.");
    });
    private static Task CursorHistoryIsolationAsync() => InTemporaryStoreAsync(async paths =>
    {
        var source=new SessionFixture {Value=new(CursorJwt("one"))}; var handler=new SessionHttp(); using var http=new HttpClient(handler);
        handler.Reply=(_,_)=>Task.FromResult(Response(JsonSerializer.Serialize(new {totalUsageEventsCount=1,usageEventsDisplay=new[]{new{id="one",timestamp=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),model="claude-test",tokenUsage=new{inputTokens=10,outputTokens=20,cacheWriteTokens=30,cacheReadTokens=40}}}})));
        var cache=Path.Combine(paths.AppHome,"cache","cursor.json"); var scanner=new CursorTokenHistory(source,http,cache);
        Check((await scanner.RefreshAsync(default)).Days.Single().Total==100,"Cursor events token units incorrect.");
        await scanner.RefreshAsync(default); Check(handler.Calls==1,"Cursor history ignored five-minute cooldown.");
        handler.Reply=(_,_)=>Task.FromResult(Response("{\"totalUsageEventsCount\":1000,\"usageEventsDisplay\":[]}"));
        var restarted=new CursorTokenHistory(source,http,cache); var read=await restarted.RefreshAsync(default);
        Check(read.Partial && read.Days.Single().Total==100,"Incomplete pagination erased the complete cache.");
        source.Value=new(CursorJwt("two"));
        Check((await restarted.RefreshAsync(default)).Days.Count==0,"Cursor identity change reused another account's tokens.");
        var combined=ProviderHistory.Merge(new Dictionary<string,TokenHistorySnapshot>{[ProviderIds.Codex]=SampleHistory(),[ProviderIds.Claude]=new([new(DateTime.Today.ToString("yyyy-MM-dd"),new Dictionary<string,long>{["same-model"]=3})],false,"now",null),[ProviderIds.Cursor]=new([new(DateTime.Today.ToString("yyyy-MM-dd"),new Dictionary<string,long>{["same-model"]=7})],false,"now",null)});
        Check(combined.Sources![ProviderIds.Cursor].Days.Single().Total==7 && combined.Days.Last().Models["Claude · same-model"]==3,"Multi-provider history mixed same-named models.");
    });
}
