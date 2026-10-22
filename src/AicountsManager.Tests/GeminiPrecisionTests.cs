using AicountsManager;
using C = System.Windows.Controls;
using W = System.Windows;
using M = System.Windows.Media;
namespace AicountsManager.Tests;
internal static partial class Program
{
    private static Task GeminiFirstDayAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(() =>
    {
        var now = DateTimeOffset.UtcNow; var reset = now.AddDays(7).AddHours(-6).ToUnixTimeSeconds();
        var weekly = new UsageWindow(.0066, 10080, reset);
        var expected = QuotaPace.WeeklyReference(weekly, now)!.Value;
        var pace = QuotaPace.Calculate(weekly, now)!;
        Check(Math.Abs(expected - 96.4285714286) < .001, "First-day reference does not deduct elapsed hours.");
        Check(Math.Abs(pace.ProjectedUsed - .1848) < .00001, "First-day usage projection does not match Codex formula.");
        var account = SampleAntigravity() with { Quotas = [new("gemini-weekly", "Gemini", "Semanal", .0066,10080,reset), new("gemini-5h", "Gemini", "Sesión · 5 h", 0,300,now.AddHours(4).ToUnixTimeSeconds())] };
        var card = new CurrentProviderCard(new UiTheme(true,false,false)); card.Update(account);
        static IEnumerable<W.FrameworkElement> Descendants(W.DependencyObject parent)
        {
            for (var i=0;i<M.VisualTreeHelper.GetChildrenCount(parent);i++)
            { var child=M.VisualTreeHelper.GetChild(parent,i); if(child is W.FrameworkElement e) yield return e; foreach(var nested in Descendants(child)) yield return nested; }
        }
        var elements = Descendants(card).ToArray();
        var texts = elements.OfType<C.TextBlock>().Select(t=>t.Text).ToArray();
        Check(texts.Contains("100%"), "Gemini does not use Codex integer rounding.");
        Check(texts.Contains("≈100% al reset"), "Gemini forecast differs from Codex formatting.");
        Check(texts.Count(t=>t=="100%") == 2, "Actual unused five-hour quota is not 100%.");
        Check(elements.Count(e=>Equals(e.Tag,"weekly-pace-marker"))==1, "First-day weekly reference is missing or applied to session quota.");
        Check(UsagePresentation.Forecast(pace)=="≈100% al reset", "Codex's existing presentation was changed.");
        Check(UsagePresentation.Percent(99.999999)=="100%" && UsagePresentation.Percent(.000001)=="0%", "Percentage formatting differs by provider.");
        return Task.CompletedTask;
    }));
    private static Task RenderGeminiFirstDayAsync(string output) => RunOnUiThreadAsync(() =>
    {
        var now = DateTimeOffset.UtcNow;
        var theme = new UiTheme(true, false, false);
        var card = new CurrentProviderCard(theme);
        card.Update(SampleAntigravity() with { Name = "Prueba Gemini", Email = "cuenta@example.test", Quotas =
            [new("gemini-weekly", "Gemini", "Semanal", .0066, 10080, now.AddDays(7).AddHours(-6).ToUnixTimeSeconds()),
             new("gemini-5h", "Gemini", "Sesión · 5 h", 0, 300, now.AddHours(4).ToUnixTimeSeconds())] });
        var body = new C.StackPanel { Margin = new W.Thickness(14) };
        body.Children.Add(new C.TextBlock { Text = "Reproducción del primer día · 6 horas", Margin = new W.Thickness(0,0,0,10) });
        body.Children.Add(card);
        var window = new W.Window { Width = 382, Height = 260, Left = -20000, Top = -20000, ShowInTaskbar = false,
            WindowStyle = W.WindowStyle.None, Content = body, Background = UiTheme.Brush(theme.Base), Foreground = UiTheme.Brush(theme.Text) };
        try { window.Show(); window.UpdateLayout(); Save(window, output); }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

}
