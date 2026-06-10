using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace MusicBox.Services
{
    public sealed class AppBuildInfo
    {
        public string VersionDisplay { get; init; } = string.Empty;
        public string VersionCode { get; init; } = string.Empty;
        public string BuildNumber { get; init; } = string.Empty;
        public string CommitHash { get; init; } = string.Empty;
    }

    public static class AppBuildInfoService
    {
        public const string DefaultVersionCode = "262001";

        public static AppBuildInfo GetCurrent()
        {
            string? repoRoot = TryFindRepoRoot();
            string versionCode = DefaultVersionCode;
            string commitDate = GetFallbackCommitDate();
            string commitCount = "0";
            string commitHash = string.Empty;
            string branchName = string.Empty;

            if (!string.IsNullOrWhiteSpace(repoRoot))
            {
                branchName = RunGit(repoRoot, "rev-parse", "--abbrev-ref", "HEAD") ?? string.Empty;
                commitDate = RunGit(repoRoot, "log", "-1", "--date=format:%m%d", "--format=%cd") ?? commitDate;
                commitCount = RunGit(repoRoot, "rev-list", "--count", "HEAD") ?? commitCount;
                commitHash = RunGit(repoRoot, "rev-parse", "--short=8", "HEAD") ?? string.Empty;
            }

            commitDate = NormalizeCommitDate(commitDate);
            commitCount = NormalizeCommitCount(commitCount);
            string buildNumber = $"{versionCode}.{commitDate}{commitCount}";

            if (TryParseBranchVersion(branchName, out string branchVersionCode, out string branchBuildNumber))
            {
                versionCode = branchVersionCode;
                buildNumber = branchBuildNumber;
            }

            string versionDisplay = BuildVersionDisplay(buildNumber, versionCode);

            return new AppBuildInfo
            {
                VersionDisplay = versionDisplay,
                VersionCode = versionCode,
                BuildNumber = buildNumber,
                CommitHash = commitHash
            };
        }

        private static string? TryFindRepoRoot()
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in GetCandidateDirectories())
            {
                var current = new DirectoryInfo(candidate);
                while (current != null)
                {
                    if (!visited.Add(current.FullName))
                    {
                        current = current.Parent;
                        continue;
                    }

                    string dotGitPath = Path.Combine(current.FullName, ".git");
                    if (Directory.Exists(dotGitPath) || File.Exists(dotGitPath))
                    {
                        return current.FullName;
                    }

                    current = current.Parent;
                }
            }

            return null;
        }

        private static IEnumerable<string> GetCandidateDirectories()
        {
            if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
            {
                yield return AppContext.BaseDirectory;
            }

            if (!string.IsNullOrWhiteSpace(Environment.CurrentDirectory))
            {
                yield return Environment.CurrentDirectory;
            }
        }

        private static string? RunGit(string repoRoot, params string[] arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                foreach (string argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using Process? process = Process.Start(startInfo);
                if (process == null)
                {
                    return null;
                }

                if (!process.WaitForExit(2000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                    }

                    return null;
                }

                string output = process.StandardOutput.ReadToEnd().Trim();
                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                {
                    return null;
                }

                return output;
            }
            catch
            {
                return null;
            }
        }

        private static string GetFallbackCommitDate()
        {
            try
            {
                string assemblyPath = typeof(AppBuildInfoService).Assembly.Location;
                return File.GetLastWriteTime(assemblyPath).ToString("MMdd");
            }
            catch
            {
                return DateTime.Now.ToString("MMdd");
            }
        }

        private static string NormalizeCommitDate(string raw)
        {
            string digits = new string((raw ?? string.Empty).Where(char.IsDigit).ToArray());
            if (digits.Length >= 4)
            {
                return digits.Substring(0, 4);
            }

            return GetFallbackCommitDate();
        }

        private static string NormalizeCommitCount(string raw)
        {
            string digits = new string((raw ?? string.Empty).Where(char.IsDigit).ToArray());
            return string.IsNullOrWhiteSpace(digits) ? "0" : digits;
        }

        private static string BuildVersionDisplay(string buildNumber, string versionCode)
        {
            string buildDigits = new string((buildNumber ?? string.Empty).Where(char.IsDigit).ToArray());
            string codeDigits = new string((versionCode ?? string.Empty).Where(char.IsDigit).ToArray());
            string source = buildDigits.Length >= 3 ? buildDigits : codeDigits;

            if (source.Length < 3)
            {
                return "00H0";
            }

            return $"{source.Substring(0, 2)}H{source[2]}";
        }

        private static bool TryParseBranchVersion(string branchName, out string versionCode, out string buildNumber)
        {
            versionCode = string.Empty;
            buildNumber = string.Empty;

            string normalized = (branchName ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
            if (string.IsNullOrWhiteSpace(normalized) || string.Equals(normalized, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                return false;
            }

            string buildCandidate = parts[^1];
            string codeCandidate = buildCandidate.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
            string versionDigits = new string(codeCandidate.Where(char.IsDigit).ToArray());

            if (string.IsNullOrWhiteSpace(versionDigits) || !buildCandidate.Contains('.'))
            {
                return false;
            }

            versionCode = versionDigits;
            buildNumber = buildCandidate;
            return true;
        }
    }
}
