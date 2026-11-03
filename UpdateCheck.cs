using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;

namespace Ets2Gps;

/// <summary>A newer release found on GitHub.</summary>
public sealed record UpdateInfo(Version Latest, string Tag, string PageUrl);

/// <summary>
/// Asks GitHub which release is the newest one (one small request to the public API; nothing about you or your PC is sent beyond a normal web request).
/// It never downloads or installs anything: the user is shown the version and chooses to open the release page.
/// </summary>
public static class UpdateCheck
{
    public const string Repo = "johnevansau320/ETS2-ATS-GPS-Win";
    public static string ReleasesPage => "https://github.com/" + Repo + "/releases/latest";

    public static Version Current
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string CurrentText => Current.ToString(3);

    /// <summary>Returns the newer release, null when this version is the latest; throws when GitHub cannot be reached.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ETS2-GPS-Windows/" + CurrentText);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        string json = await http.GetStringAsync("https://api.github.com/repos/" + Repo + "/releases/latest", ct);
        return FromJson(json, Current);
    }

    /// <summary>The newer release described by a GitHub "latest release" answer, or null (also for a draft, a pre-release or a release without a Windows file).</summary>
    public static UpdateInfo? FromJson(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        bool hasExe = false;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
                if (a.TryGetProperty("name", out var n) && (n.GetString() ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) hasExe = true;
        if (!hasExe) return null;
        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        string url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
        if (!TryVersion(tag, out var latest)) return null;
        // only ever open a page on github.com
        if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) url = ReleasesPage;
        return latest > current ? new UpdateInfo(latest, tag, url) : null;
    }

    public static bool TryVersion(string tag, out Version v)
    {
        tag = tag.Trim().TrimStart('v', 'V');
        if (Version.TryParse(tag, out var p)) { v = new Version(p.Major, p.Minor, Math.Max(0, p.Build)); return true; }
        v = new Version(0, 0, 0); return false;
    }

    public static void OpenPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser */ }
    }
}

/// <summary>The update check of the main window: once shortly after start and then about once a day; the result is shown as a toast and in Settings.</summary>
public sealed partial class MainWindow
{
    UpdateInfo? update;
    readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };

    void StartUpdateChecks()
    {
        updateTimer.Tick += async (_, _) => await AutoCheckUpdateAsync();
        updateTimer.Start();
        _ = Task.Run(async () => { await Task.Delay(20_000); await Dispatcher.InvokeAsync(AutoCheckUpdateAsync); });
    }

    async Task AutoCheckUpdateAsync()
    {
        if (!cfg.CheckUpdates) return;
        long now = DateTime.UtcNow.Ticks;
        if (now - cfg.LastUpdateCheckUtc < TimeSpan.FromHours(20).Ticks) return;
        try
        {
            var u = await UpdateCheck.CheckAsync();
            cfg.LastUpdateCheckUtc = now; cfg.Save();
            SetUpdate(u, announce: true);
        }
        catch { /* offline: try again later */ }
    }

    void SetUpdate(UpdateInfo? u, bool announce)
    {
        if (u != null && u.Latest.ToString(3) == cfg.SkippedVersion) u = null;
        bool isNew = u != null && (update == null || update.Latest != u.Latest);
        update = u;
        if (u != null && announce && isNew) Toast("Update " + u.Latest.ToString(3) + " available - see Settings > Updates");
    }

    /// <summary>For the tablet / browser version, which is served by this app: is a newer Windows app available?</summary>
    string UpdateJson() => JsonSerializer.Serialize(new { available = update != null, version = update?.Latest.ToString(3) ?? "", current = UpdateCheck.CurrentText, url = update?.PageUrl ?? "" });

    string UpdateStatusText() => update != null
        ? "Version " + update.Latest.ToString(3) + " is available (you have " + UpdateCheck.CurrentText + ")."
        : "You have version " + UpdateCheck.CurrentText + ".";
}
