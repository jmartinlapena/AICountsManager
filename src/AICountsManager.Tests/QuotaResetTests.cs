using AICountsManager;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using C = System.Windows.Controls;
using W = System.Windows;
using M = System.Windows.Media;

namespace AICountsManager.Tests;
internal static partial class Program
{
    private static JsonNode HostQuota(double remaining, long reset)
    {
        var payload = QuotaPayload();
        foreach (var group in payload["response"]!["groups"]!.AsArray())
            foreach (var bucket in group!["buckets"]!.AsArray())
            {
                bucket!["remainingFraction"] = remaining;
                bucket["resetTime"] = DateTimeOffset.FromUnixTimeSeconds(reset).ToString("O");
            }
        return payload;
    }
    private static async Task AntigravityHostSelectionAsync()
    {
        var reset = DateTimeOffset.UtcNow.AddDays(7).AddHours(-8).ToUnixTimeSeconds();
        var placeholder = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds();
        foreach (var auth in new string?[] { "consumer", "oauth-personal", null, "business", "enterprise", "gcp", "oauth-business" })
        {
            var business = auth is "business" or "enterprise" or "gcp" or "oauth-business";
            var expected = business ? "cloudcode-pa.googleapis.com" : "daily-cloudcode-pa.googleapis.com";
            var credentials = new CloudCredentials(); credentials.Value = credentials.Value! with { AuthMethod = auth };
            var hosts = new List<string>(); var handler = new CloudHandler(); using var http = new HttpClient(handler);
            handler.Reply = r =>
            {
                if (!r.RequestUri!.AbsolutePath.EndsWith("retrieveUserQuotaSummary")) return JsonResponse(new JsonObject());
                hosts.Add(r.RequestUri.Host);
                return JsonResponse(r.RequestUri.Host == expected ? HostQuota(.99984396, reset) : HostQuota(1, placeholder));
            };
            var read = await new AntigravityCloudProvider(credentials, http).ReadAsync(new CurrentQuotaRequest(), default);
            var gemini = read.Quotas.Single(q => q.Id == "gemini-weekly");
            Check(read.Status == "ok" && gemini.ResetsAt == reset && gemini.UsedPercent > 0 && hosts.SequenceEqual([expected]),
                "The wrong quota deployment won for auth method " + auth);
            Check(handler.Refreshes == 0, "Host selection unnecessarily renewed a valid session.");
        }
        var current = new JsonObject { ["id_token_email"] = "a@example.test", ["auth_method"] = "consumer", ["token"] = new JsonObject() };
        Check(WindowsAntigravityCredentials.Parse(System.Text.Encoding.UTF8.GetBytes(current.ToJsonString()))?.AuthMethod == "consumer",
            "Credential parsing discarded the provider's account type.");

        var calls = new List<string>(); var mergeHandler = new CloudHandler(); using var mergeHttp = new HttpClient(mergeHandler);
        mergeHandler.Reply = r =>
        {
            if (!r.RequestUri!.AbsolutePath.EndsWith("retrieveUserQuotaSummary")) return JsonResponse(new JsonObject());
            calls.Add(r.RequestUri.Host);
            var payload = HostQuota(r.RequestUri.Host.StartsWith("daily-") ? 1 : .4, r.RequestUri.Host.StartsWith("daily-") ? reset : placeholder);
            if (r.RequestUri.Host.StartsWith("daily-"))
            {
                payload["response"]!["groups"]![0]!["buckets"]![1]!["remainingFraction"] = null;
                payload["response"]!["groups"]!.AsArray().RemoveAt(2);
            }
            return JsonResponse(payload);
        };
        var merged = await new AntigravityCloudProvider(new CloudCredentials(), mergeHttp).ReadAsync(new CurrentQuotaRequest(), default);
        Check(calls.SequenceEqual(["daily-cloudcode-pa.googleapis.com", "cloudcode-pa.googleapis.com"])
            && merged.Quotas.Count == 5, "Fallback lost a missing window or Argon.");
        Check(merged.Quotas.Single(q => q.Id == "gemini-weekly") is { UsedPercent: 0 } weekly && weekly.ResetsAt == reset,
            "Fallback replaced the preferred host's valid zero-consumption window.");
        Check(Math.Abs(merged.Quotas.Single(q => q.Id == "gemini-5h").UsedPercent!.Value - 60) < .00001,
            "Fallback did not fill an unknown window.");
    }

    private static async Task AntigravityHostFallbackAsync()
    {
        foreach (var status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
        {
            var handler = new CloudHandler(); using var http = new HttpClient(handler);
            handler.Reply = r => r.RequestUri!.Host.StartsWith("daily-") ? new(status) : JsonResponse(QuotaPayload());
            var read = await new AntigravityCloudProvider(new CloudCredentials(), http).ReadAsync(new CurrentQuotaRequest(), default);
            Check(read.Status == "ok" && handler.Refreshes == 0 && handler.Quotas == 2, "Host-specific failure prevented fallback or rotated credentials.");
        }
        var limited = new CloudHandler { Reply = _ => new(HttpStatusCode.TooManyRequests) }; using var limitedHttp = new HttpClient(limited);
        Check((await new AntigravityCloudProvider(new CloudCredentials(), limitedHttp).ReadAsync(new CurrentQuotaRequest(), default)).HttpStatus == 429
            && limited.Quotas == 1 && limited.Refreshes == 0, "429 triggered requests to the other deployment.");
        var cancelled = new CloudHandler(); using var cancelledHttp = new HttpClient(cancelled); using var cts = new CancellationTokenSource(); cts.Cancel();
        var stopped = false;
        try { await new AntigravityCloudProvider(new CloudCredentials(), cancelledHttp).ReadAsync(new CurrentQuotaRequest(), cts.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped && cancelled.Quotas == 0, "Cancellation still queried Google.");
    }

    private static Task QuotaResetStatesAsync()
    {
        var now = DateTimeOffset.Parse("2026-11-10T20:00:00+01:00");
        foreach (var minutes in new[] { 300, 10080 })
        {
            var full = new UsageWindow(0, minutes, now.AddMinutes(minutes).ToUnixTimeSeconds());
            Check(QuotaDisplay.Create(full, 100, true, now, now.ToString("O")).ResetNotice == QuotaResetNotice.Unused, "Full unused quota lacks NEW.");
            Check(QuotaDisplay.Create(full, 100, true, now.AddMinutes(5), now.ToString("O")).ResetNotice == QuotaResetNotice.Unused, "Clock ticks prematurely removed NEW.");
            Check(QuotaDisplay.Create(full with { UsedPercent = .015604 }, 99.984396, true, now).ResetNotice == QuotaResetNotice.None,
                "Rounded 100% was treated as an unused quota.");
            Check(QuotaDisplay.Create(full with { ResetsAt = now.AddMinutes(minutes - 10).ToUnixTimeSeconds() }, 100, true, now).ResetNotice == QuotaResetNotice.None,
                "A running full-percentage window was treated as not started.");
            var elapsed = full with { UsedPercent = 73, ResetsAt = now.AddSeconds(-1).ToUnixTimeSeconds() };
            var display = QuotaDisplay.Create(elapsed, null, false, now);
            Check(display.ResetNotice == QuotaResetNotice.Expected && display.RemainingText == "Sin datos" && display.ResetText == "Pendiente de actualizar",
                "Theoretical reset invented a fresh balance or lost its reminder.");
            Check(QuotaDisplay.Create(full, 100, false, now).ResetNotice == QuotaResetNotice.None, "Stale reading asserted a confirmed reset.");
            Check(QuotaDisplay.Create(full, 100, true, now.AddMinutes(16), now.ToString("O")).ResetNotice == QuotaResetNotice.None, "Old observation was considered fresh.");
        }
        Check(QuotaDisplay.Create(new(0, 1440, now.AddDays(1).ToUnixTimeSeconds()), 100, true, now).ResetNotice == QuotaResetNotice.None
            && QuotaDisplay.Create(new(null,null,null), null, false, now).ResetNotice == QuotaResetNotice.None, "Unknown or other windows gained reset notices.");
        return Task.CompletedTask;
    }

    private static Task QuotaResetUiAsync() => RunOnUiThreadAsync(() =>
    {
        var now = DateTimeOffset.UtcNow; var full = new UsageWindow(0,300,now.AddHours(5).ToUnixTimeSeconds());
        foreach (var theme in new[] { new UiTheme(true,false,false), new UiTheme(false,false,false), new UiTheme(true,false,true) })
        {
            using var row = new AccountRow(theme); using var external = new CurrentProviderCard(theme);
            foreach (var scenario in new[] { "unused", "started", "elapsed", "unknown" })
            {
                var window = scenario == "elapsed" ? full with { UsedPercent = 80, ResetsAt = now.AddMinutes(-1).ToUnixTimeSeconds() }
                    : scenario == "started" ? full with { UsedPercent = .01 }
                    : scenario == "unknown" ? new UsageWindow(null,null,null) : full;
                var quota = new ProviderQuota("session","Gemini","Sesión · 5 h",window.UsedPercent,window.WindowDurationMins,window.ResetsAt);
                var remaining = quota.CurrentUsed is double used ? 100 - used : (double?)null;
                row.Update(SampleProfile("Codex",true) with { SessionWindow=window,WeeklyWindow=null,SessionRemainingPercent=remaining,CheckedAt=now.ToString("O") },false);
                external.Update(SampleAntigravity() with { CheckedAt=now.ToString("O"),Quotas=[quota] });
                var badges = new[] { UiField<QuotaResetBadge[]>(row,"_resetBadges")[0], QuotaElements(external).OfType<QuotaResetBadge>().Single() };
                foreach(var badge in badges)
                {
                    Check((badge.Visibility == W.Visibility.Visible) == (scenario is "unused" or "elapsed"), "Reset badge differs between providers or retained a previous state.");
                    if (badge.Visibility == W.Visibility.Visible) Check(((C.TextBlock)badge.Child).Text == (scenario == "elapsed" ? "RESET" : "NEW"), "Badge text does not distinguish a theoretical reset.");
                    Check(badge.ToolTip is null && !badge.HasAnimatedProperties, "Reset notice introduced a hover or an animation.");
                }
            }
        }
        return Task.CompletedTask;
    });

    private static Task RenderQuotaResetAsync(string output) => RunOnUiThreadAsync(() =>
    {
        var theme = new UiTheme(true,false,false); var now=DateTimeOffset.UtcNow;
        using var codex=new AccountRow(theme); using var google=new CurrentProviderCard(theme);
        codex.Update(SampleProfile("Personal",true) with { CheckedAt=now.ToString("O"), PlanType="plus", SessionWindow=new(0,300,now.AddHours(5).ToUnixTimeSeconds()),SessionRemainingPercent=100,
            WeeklyWindow=new(60,10080,now.AddMinutes(-2).ToUnixTimeSeconds()),WeeklyRemainingPercent=null },false);
        google.Update(SampleAntigravity() with { CheckedAt=now.ToString("O"),Quotas=[new("gemini-weekly","Gemini","Semanal",0,10080,now.AddDays(7).ToUnixTimeSeconds()),
            new("gemini-5h","Gemini","Sesión · 5 h",.015604,300,now.AddHours(4).ToUnixTimeSeconds())] });
        var body=new C.StackPanel { Margin=new(14) }; body.Children.Add(codex); body.Children.Add(google);
        var window=new W.Window { Width=382,SizeToContent=W.SizeToContent.Height,Left=-20000,Top=-20000,ShowInTaskbar=false,WindowStyle=W.WindowStyle.None,
            Background=UiTheme.Brush(theme.Base),Foreground=UiTheme.Brush(theme.Text),Content=body };
        try { window.Show();window.UpdateLayout();Save(window,output); } finally { window.Close(); }
        return Task.CompletedTask;
    });
}
