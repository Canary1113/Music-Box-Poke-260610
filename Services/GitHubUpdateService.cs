using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBox.Services
{
    public enum AppUpdateState
    {
        Current,
        UpdateAvailable,
        NoReleaseFound,
        UnsupportedInstall,
        Error
    }

    public sealed class AppUpdateInfo
    {
        public AppUpdateState State { get; init; }
        public string CurrentVersion { get; init; } = string.Empty;
        public string RemoteVersion { get; init; } = string.Empty;
        public string ReleaseName { get; init; } = string.Empty;
        public string DownloadUrl { get; init; } = string.Empty;
        public string ErrorMessage { get; init; } = string.Empty;
        public DateTimeOffset? PublishedAt { get; init; }
    }

    public sealed class GitHubUpdateService
    {
        private const string Owner = "Canary1113";
        private const string Repo = "MusicBox";
        private const string ReleasesPageUrl = "https://github.com/Canary1113/MusicBox/releases";
        private const string ReleaseApiUrl = "https://api.github.com/repos/Canary1113/MusicBox/releases?per_page=20";
        private const string AssetNamePrefix = "MusicBox-win-x64";
        private const string AppExecutableName = "MusicBox.exe";

        private static readonly Lazy<GitHubUpdateService> LazyInstance = new(() => new GitHubUpdateService());
        private readonly HttpClient _httpClient;

        public static GitHubUpdateService Instance => LazyInstance.Value;

        private GitHubUpdateService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MusicBox-Updater");
        }

        public async Task<AppUpdateInfo> CheckAsync(CancellationToken cancellationToken = default)
        {
            AppBuildInfo current = AppBuildInfoService.GetCurrent();

            try
            {
                ReleaseCandidate? candidate = await TryFindReleaseFromPageOrNullAsync(cancellationToken).ConfigureAwait(false)
                    ?? await TryFindReleaseFromApiOrNullAsync(cancellationToken).ConfigureAwait(false);
                if (candidate == null)
                {
                    return BuildNoReleaseResult(current);
                }

                bool updateAvailable = IsRemoteNewer(current, candidate);
                return new AppUpdateInfo
                {
                    State = updateAvailable ? AppUpdateState.UpdateAvailable : AppUpdateState.Current,
                    CurrentVersion = current.BuildNumber,
                    RemoteVersion = candidate.TagName,
                    ReleaseName = candidate.Name,
                    DownloadUrl = candidate.DownloadUrl,
                    PublishedAt = candidate.PublishedAt
                };
            }
            catch (Exception ex)
            {
                return new AppUpdateInfo
                {
                    State = AppUpdateState.Error,
                    CurrentVersion = current.BuildNumber,
                    ErrorMessage = ex.Message
                };
            }
        }

        private async Task<ReleaseCandidate?> TryFindReleaseFromPageOrNullAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await TryFindReleaseFromPageAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        private async Task<ReleaseCandidate?> TryFindReleaseFromApiOrNullAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await TryFindReleaseFromApiAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        private async Task<ReleaseCandidate?> TryFindReleaseFromPageAsync(CancellationToken cancellationToken)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, ReleasesPageUrl);
            request.Headers.Accept.ParseAdd("text/html");

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            foreach (ReleasePageInfo release in FindReleasePageInfos(html))
            {
                ReleaseCandidate? candidate = await TryFindReleaseFromExpandedAssetsAsync(release, cancellationToken).ConfigureAwait(false);
                if (candidate != null)
                {
                    return candidate;
                }
            }

            // Some GitHub responses still include asset links directly in the release page.
            string escapedOwner = Regex.Escape(Owner);
            string escapedRepo = Regex.Escape(Repo);
            string pattern = $"href=\"(?<href>/{escapedOwner}/{escapedRepo}/releases/download/(?<tag>[^\"]+)/(?<asset>{Regex.Escape(AssetNamePrefix)}[^\"]*\\.zip))\"";

            Match match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                return null;
            }

            string href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            string tagName = WebUtility.UrlDecode(match.Groups["tag"].Value);
            string downloadUrl = "https://github.com" + href;
            DateTimeOffset? publishedAt = TryFindNearbyPublishedTime(html, match.Index);
            return new ReleaseCandidate(tagName, tagName, downloadUrl, publishedAt);
        }

        private async Task<ReleaseCandidate?> TryFindReleaseFromExpandedAssetsAsync(ReleasePageInfo release, CancellationToken cancellationToken)
        {
            string url = $"https://github.com/{Owner}/{Repo}/releases/expanded_assets/{Uri.EscapeDataString(release.TagName)}";
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("text/html");

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string escapedOwner = Regex.Escape(Owner);
            string escapedRepo = Regex.Escape(Repo);
            string pattern = $"href=\"/{escapedOwner}/{escapedRepo}/releases/download/{Regex.Escape(release.TagName)}/(?<asset>{Regex.Escape(AssetNamePrefix)}[^\"]*\\.zip)\"";

            Match match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                return null;
            }

            string href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            string downloadUrl = "https://github.com" + href;
            return new ReleaseCandidate(release.TagName, release.Name, downloadUrl, release.PublishedAt);
        }

        private async Task<ReleaseCandidate?> TryFindReleaseFromApiAsync(CancellationToken cancellationToken)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, ReleaseApiUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? FindReleaseCandidate(document.RootElement)
                : null;
        }

        public async Task DownloadAndInstallAsync(AppUpdateInfo updateInfo, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(updateInfo.DownloadUrl))
            {
                throw new InvalidOperationException("The update package download URL is empty.");
            }

            string? currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            {
                throw new InvalidOperationException("Cannot locate the current executable.");
            }

            string? targetDir = Path.GetDirectoryName(currentExe);
            if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
            {
                throw new InvalidOperationException("Cannot locate the application directory.");
            }

            string workDir = Path.Combine(Path.GetTempPath(), "MusicBoxUpdate", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            string zipPath = Path.Combine(workDir, "update.zip");
            string extractDir = Path.Combine(workDir, "extracted");

            using (HttpResponseMessage response = await _httpClient.GetAsync(updateInfo.DownloadUrl, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using FileStream output = File.Create(zipPath);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            ZipFile.ExtractToDirectory(zipPath, extractDir);
            string sourceDir = LocatePayloadDirectory(extractDir);
            string scriptPath = WriteUpdaterScript(workDir);
            StartUpdaterScript(scriptPath, sourceDir, targetDir, currentExe);
        }

        private static AppUpdateInfo BuildNoReleaseResult(AppBuildInfo current)
        {
            return new AppUpdateInfo
            {
                State = AppUpdateState.NoReleaseFound,
                CurrentVersion = current.BuildNumber
            };
        }

        private static ReleaseCandidate? FindReleaseCandidate(JsonElement releases)
        {
            foreach (JsonElement release in releases.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out JsonElement draft) && draft.GetBoolean())
                {
                    continue;
                }

                string tagName = GetString(release, "tag_name");
                string name = GetString(release, "name");
                DateTimeOffset? publishedAt = TryParseDate(GetString(release, "published_at"));

                if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    string assetName = GetString(asset, "name");
                    if (!assetName.StartsWith(AssetNamePrefix, StringComparison.OrdinalIgnoreCase)
                        || !assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string downloadUrl = GetString(asset, "browser_download_url");
                    if (!string.IsNullOrWhiteSpace(downloadUrl))
                    {
                        return new ReleaseCandidate(tagName, string.IsNullOrWhiteSpace(name) ? tagName : name, downloadUrl, publishedAt);
                    }
                }
            }

            return null;
        }

        private static bool IsRemoteNewer(AppBuildInfo current, ReleaseCandidate candidate)
        {
            if (IsComparableVersionPair(candidate.TagName, current.BuildNumber)
                && TryParseVersion(candidate.TagName, out Version? remoteVersion)
                && TryParseVersion(current.BuildNumber, out Version? currentVersion)
                && remoteVersion != null
                && currentVersion != null
                && remoteVersion != currentVersion)
            {
                return remoteVersion > currentVersion;
            }

            if (candidate.PublishedAt.HasValue)
            {
                DateTimeOffset localBuildTime = GetLocalBuildTime();
                return candidate.PublishedAt.Value > localBuildTime.AddMinutes(1);
            }

            return !string.Equals(candidate.TagName, current.BuildNumber, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsComparableVersionPair(string remoteTag, string currentBuildNumber)
        {
            int remoteMajor = GetLeadingNumber(remoteTag);
            int currentMajor = GetLeadingNumber(currentBuildNumber);

            if (remoteMajor < 0 || currentMajor < 0)
            {
                return false;
            }

            // The app uses build numbers such as 262001.0526, while public releases may use v0.1.0.
            // Only compare version numbers directly when both sides clearly use the same scale.
            return (remoteMajor >= 1000 && currentMajor >= 1000) || (remoteMajor < 1000 && currentMajor < 1000);
        }

        private static int GetLeadingNumber(string value)
        {
            string normalized = (value ?? string.Empty).Trim().TrimStart('v', 'V');
            string digits = new string(normalized.TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int result) ? result : -1;
        }

        private static DateTimeOffset GetLocalBuildTime()
        {
            try
            {
                string assemblyPath = typeof(GitHubUpdateService).Assembly.Location;
                return File.GetLastWriteTimeUtc(assemblyPath);
            }
            catch
            {
                return DateTimeOffset.MinValue;
            }
        }

        private static bool TryParseVersion(string value, out Version? version)
        {
            version = null;
            string normalized = (value ?? string.Empty).Trim().TrimStart('v', 'V');
            string numeric = new string(normalized.Select(ch => char.IsDigit(ch) || ch == '.' ? ch : ' ').ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(numeric))
            {
                return false;
            }

            string[] parts = numeric.Split('.', StringSplitOptions.RemoveEmptyEntries);
            while (parts.Length < 2)
            {
                parts = parts.Append("0").ToArray();
            }

            return Version.TryParse(string.Join('.', parts), out version);
        }

        private static string LocatePayloadDirectory(string extractDir)
        {
            string? exePath = Directory.EnumerateFiles(extractDir, AppExecutableName, SearchOption.AllDirectories)
                .OrderBy(path => path.Length)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(exePath))
            {
                throw new InvalidOperationException("The update package does not contain MusicBox.exe.");
            }

            return Path.GetDirectoryName(exePath)
                ?? throw new InvalidOperationException("Cannot locate the update package directory.");
        }

        private static string WriteUpdaterScript(string workDir)
        {
            string scriptPath = Path.Combine(workDir, "ApplyMusicBoxUpdate.ps1");
            // The app cannot overwrite its own running executable, so a short external script
            // waits for this process to exit and then copies the downloaded publish folder.
            string script = @"
param(
    [int]$ProcessId,
    [string]$SourceDir,
    [string]$TargetDir,
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'

try {
    Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue
} catch {
}

Get-ChildItem -LiteralPath $SourceDir -Force | Copy-Item -Destination $TargetDir -Recurse -Force
Start-Process -FilePath $ExePath
";

            File.WriteAllText(scriptPath, script);
            return scriptPath;
        }

        private static void StartUpdaterScript(string scriptPath, string sourceDir, string targetDir, string exePath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-ProcessId");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-SourceDir");
            startInfo.ArgumentList.Add(sourceDir);
            startInfo.ArgumentList.Add("-TargetDir");
            startInfo.ArgumentList.Add(targetDir);
            startInfo.ArgumentList.Add("-ExePath");
            startInfo.ArgumentList.Add(exePath);

            Process.Start(startInfo);
        }

        private static string GetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        private static DateTimeOffset? TryParseDate(string value)
        {
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset result)
                ? result
                : null;
        }

        private static DateTimeOffset? TryFindNearbyPublishedTime(string html, int assetIndex)
        {
            int start = Math.Max(0, assetIndex - 8000);
            int length = Math.Min(html.Length - start, 12000);
            string area = html.Substring(start, length);
            MatchCollection matches = Regex.Matches(area, "datetime=\"(?<date>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (matches.Count == 0)
            {
                return null;
            }

            return TryParseDate(matches[^1].Groups["date"].Value);
        }

        private static IEnumerable<ReleasePageInfo> FindReleasePageInfos(string html)
        {
            string escapedOwner = Regex.Escape(Owner);
            string escapedRepo = Regex.Escape(Repo);
            string pattern = $"href=\"/{escapedOwner}/{escapedRepo}/releases/tag/(?<tag>[^\"]+)\"";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                string tagName = WebUtility.UrlDecode(match.Groups["tag"].Value);
                if (string.IsNullOrWhiteSpace(tagName) || !seen.Add(tagName))
                {
                    continue;
                }

                DateTimeOffset? publishedAt = TryFindNearbyPublishedTime(html, match.Index);
                yield return new ReleasePageInfo(tagName, tagName, publishedAt);
            }
        }

        private sealed record ReleasePageInfo(string TagName, string Name, DateTimeOffset? PublishedAt);
        private sealed record ReleaseCandidate(string TagName, string Name, string DownloadUrl, DateTimeOffset? PublishedAt);
    }
}
