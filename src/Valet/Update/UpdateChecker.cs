using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Valet.App;
using Valet.Logging;

namespace Valet.Update;

internal enum UpdateStatus
{
    UpToDate,
    Available,
    NoReleases,
    Error,
}

internal sealed record UpdateCheckResult(
    UpdateStatus Status,
    Version CurrentVersion,
    Version? LatestVersion = null,
    string? LatestTag = null,
    GitHubAsset? Installer = null,
    string? ExpectedSha256 = null,
    string? ErrorMessage = null);

internal sealed class UpdateChecker : IDisposable
{
    private const string Owner = "yogiee";
    private const string Repo = "valet";
    private const string ApiBase = $"https://api.github.com/repos/{Owner}/{Repo}";

    // Tolerant: allows colon/equals/whitespace/backticks/quotes between the label and the hex run.
    private static readonly Regex Sha256InBody =
        new(@"\bSHA-?256\b[^0-9a-fA-F]*([0-9a-fA-F]{64})\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly Config _config;

    public UpdateChecker(Config config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Valet/{CurrentVersion()} ({Owner}/{Repo})");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var current = CurrentVersion();
        try
        {
            var release = await FetchReleaseAsync(_config.AutoUpdateChannel, ct).ConfigureAwait(false);
            if (release is null)
                return new UpdateCheckResult(UpdateStatus.NoReleases, current);

            var latest = ParseTagVersion(release.TagName);
            if (latest is null)
                return new UpdateCheckResult(UpdateStatus.Error, current,
                    ErrorMessage: $"Couldn't parse release tag '{release.TagName}'");

            if (latest <= current)
                return new UpdateCheckResult(UpdateStatus.UpToDate, current, latest, release.TagName);

            var installer = release.Assets.FirstOrDefault(a =>
                a.Name.StartsWith("Valet-Setup", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (installer is null)
                return new UpdateCheckResult(UpdateStatus.Error, current, latest, release.TagName,
                    ErrorMessage: "Release has no Valet-Setup-*.exe asset");

            var sha = ExtractSha256(release.Body);

            return new UpdateCheckResult(UpdateStatus.Available, current, latest, release.TagName, installer, sha);
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check failed: {ex.Message}");
            return new UpdateCheckResult(UpdateStatus.Error, current, ErrorMessage: ex.Message);
        }
    }

    // Downloads the installer, optionally verifies SHA256, and launches it in silent mode.
    // Returns the path it launched (or null on failure). The installer itself closes + restarts Valet.
    public async Task<string?> DownloadAndInstallAsync(UpdateCheckResult result, CancellationToken ct = default)
    {
        if (result.Status != UpdateStatus.Available || result.Installer is null)
        {
            Log.Warn("DownloadAndInstall called with no available update");
            return null;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), result.Installer.Name);
        try
        {
            Log.Info($"Downloading update: {result.Installer.BrowserDownloadUrl} → {tempPath}");
            using (var resp = await _http.GetAsync(result.Installer.BrowserDownloadUrl,
                       HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(tempPath);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(result.ExpectedSha256))
            {
                var actual = await ComputeSha256Async(tempPath, ct).ConfigureAwait(false);
                if (!string.Equals(actual, result.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Error($"SHA256 mismatch: expected {result.ExpectedSha256}, got {actual} — aborting install");
                    TryDelete(tempPath);
                    return null;
                }
                Log.Info("SHA256 verified");
            }
            else
            {
                Log.Info("No SHA256 in release body — skipping verification (TLS only)");
            }

            var psi = new ProcessStartInfo
            {
                FileName = tempPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true,
            };
            using var p = Process.Start(psi);
            Log.Info($"Installer launched (pid={p?.Id.ToString() ?? "?"}); Inno will close + restart Valet via Restart Manager");
            return tempPath;
        }
        catch (Exception ex)
        {
            Log.Error("Download/install", ex);
            TryDelete(tempPath);
            return null;
        }
    }

    // ----- helpers -----

    private async Task<GitHubRelease?> FetchReleaseAsync(string channel, CancellationToken ct)
    {
        if (string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase))
        {
            var all = await _http.GetFromJsonAsync<List<GitHubRelease>>(
                $"{ApiBase}/releases?per_page=10", ct).ConfigureAwait(false);
            return all?
                .Where(r => !r.Draft)
                .OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
                .FirstOrDefault();
        }

        // stable: GitHub's /releases/latest excludes drafts and prereleases automatically.
        try
        {
            return await _http.GetFromJsonAsync<GitHubRelease>($"{ApiBase}/releases/latest", ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static Version CurrentVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    private static Version? ParseTagVersion(string tag)
    {
        var t = tag.TrimStart('v', 'V').Trim();
        var dashIdx = t.IndexOf('-');
        if (dashIdx > 0) t = t[..dashIdx];
        return Version.TryParse(t, out var v) ? v : null;
    }

    private static string? ExtractSha256(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var m = Sha256InBody.Match(body);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose() => _http.Dispose();
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("body")] public string Body { get; set; } = "";
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = new();
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}
