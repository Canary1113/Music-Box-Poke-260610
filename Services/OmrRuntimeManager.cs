using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBox.Services
{
    public sealed class OmrRuntimeManager
    {
        private const string HomrPackageSpec = "homr==0.4.0";

        private static readonly string RuntimeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MusicBox",
            "omr",
            "py311");

        private static readonly string VenvPythonPath = Path.Combine(RuntimeRoot, "Scripts", "python.exe");
        private static readonly string VenvHomrPath = Path.Combine(RuntimeRoot, "Scripts", "homr.exe");

        public async Task<OmrRuntimeStatus> EnsureReadyAsync(CancellationToken cancellationToken = default)
        {
            OmrRuntimeStatus current = GetRuntimeStatus();
            if (current.Python311Ready && current.HomrReady)
            {
                return current;
            }

            var logs = new List<string>();
            PythonLaunchSpec? basePythonNullable = await DetectPython311Async(cancellationToken).ConfigureAwait(false);
            if (basePythonNullable == null)
            {
                return current with
                {
                    Message = "Python 3.11 not found. Please install Python 3.11 first."
                };
            }
            PythonLaunchSpec basePython = basePythonNullable.Value;

            logs.Add($"Python 3.11: {basePython.DisplayName}");

            if (!File.Exists(VenvPythonPath))
            {
                Directory.CreateDirectory(RuntimeRoot);
                ProcessResult venvRun = await RunProcessAsync(
                    basePython.FileName,
                    $"{basePython.ArgPrefix} -m venv \"{RuntimeRoot}\"".Trim(),
                    RuntimeRoot,
                    cancellationToken).ConfigureAwait(false);
                if (venvRun.ExitCode != 0)
                {
                    return current with
                    {
                        Message = $"Create venv failed: {FirstLine(venvRun.Stderr) ?? FirstLine(venvRun.Stdout) ?? "unknown error"}"
                    };
                }
            }

            ProcessResult pipUpgrade = await RunProcessAsync(
                VenvPythonPath,
                "-m pip install --upgrade pip",
                RuntimeRoot,
                cancellationToken).ConfigureAwait(false);
            if (pipUpgrade.ExitCode != 0)
            {
                return current with
                {
                    Message = $"pip upgrade failed: {FirstLine(pipUpgrade.Stderr) ?? FirstLine(pipUpgrade.Stdout) ?? "unknown error"}"
                };
            }

            ProcessResult homrInstall = await RunProcessAsync(
                VenvPythonPath,
                $"-m pip install {HomrPackageSpec}",
                RuntimeRoot,
                cancellationToken).ConfigureAwait(false);
            if (homrInstall.ExitCode != 0)
            {
                return current with
                {
                    Message = $"homr install failed: {FirstLine(homrInstall.Stderr) ?? FirstLine(homrInstall.Stdout) ?? "unknown error"}"
                };
            }

            string? homrCommand = GetHomrCommand();
            if (string.IsNullOrWhiteSpace(homrCommand))
            {
                return current with
                {
                    Message = "homr not found after install."
                };
            }

            Environment.SetEnvironmentVariable("MBX_HOMR_CLI", homrCommand, EnvironmentVariableTarget.Process);
            OmrRuntimeStatus ready = GetRuntimeStatus();
            string message = ready.HomrReady
                ? $"OMR runtime ready. {string.Join(" | ", logs)}"
                : "OMR runtime install attempted, but homr is still unavailable.";
            return ready with { Message = message };
        }

        public string? GetHomrCommand()
        {
            if (File.Exists(VenvHomrPath))
            {
                return VenvHomrPath;
            }

            if (File.Exists(VenvPythonPath))
            {
                return $"\"{VenvPythonPath}\" -m homr";
            }

            string? fromEnv = Environment.GetEnvironmentVariable("MBX_HOMR_CLI")
                ?? Environment.GetEnvironmentVariable("MBX_HOMR_CLI", EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable("MBX_HOMR_CLI", EnvironmentVariableTarget.Machine);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv.Trim();
            }

            string? fromPath = TryResolveFromPath("homr");
            if (!string.IsNullOrWhiteSpace(fromPath))
            {
                return fromPath;
            }

            return null;
        }

        public OmrRuntimeStatus GetRuntimeStatus()
        {
            bool pyReady = IsVenvPython311Ready();
            string? homrCommand = GetHomrCommand();
            bool homrReady = IsHomrRunnable(homrCommand);
            string? audiverisPath = ScoreOmrService.DetectAudiverisCliPath();
            bool audiverisReady = !string.IsNullOrWhiteSpace(audiverisPath);

            return new OmrRuntimeStatus
            {
                RuntimeRoot = RuntimeRoot,
                Python311Ready = pyReady,
                HomrReady = homrReady,
                AudiverisReady = audiverisReady,
                HomrCommand = homrCommand ?? string.Empty,
                AudiverisPath = audiverisPath ?? string.Empty,
                Message = string.Empty
            };
        }

        private static bool IsVenvPython311Ready()
        {
            if (!File.Exists(VenvPythonPath))
            {
                return false;
            }

            ProcessResult probe = RunProcess(
                VenvPythonPath,
                "-c \"import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')\"",
                RuntimeRoot,
                7000);
            if (probe.ExitCode != 0)
            {
                return false;
            }

            string line = FirstLine(probe.Stdout) ?? string.Empty;
            return string.Equals(line.Trim(), "3.11", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHomrRunnable(string? command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            ParseCommand(command, out string fileName, out string argPrefix);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            ProcessResult probe = RunProcess(
                fileName,
                $"{argPrefix} --help".Trim(),
                RuntimeRoot,
                15000);
            return probe.ExitCode == 0;
        }

        private static async Task<PythonLaunchSpec?> DetectPython311Async(CancellationToken cancellationToken)
        {
            var candidates = new List<PythonLaunchSpec>
            {
                new("py", "-3.11", "py -3.11"),
                new("python3.11", string.Empty, "python3.11"),
                new("python", string.Empty, "python")
            };

            foreach (PythonLaunchSpec candidate in candidates)
            {
                ProcessResult run = await RunProcessAsync(
                    candidate.FileName,
                    $"{candidate.ArgPrefix} -c \"import sys; print(str(sys.version_info.major)+'.'+str(sys.version_info.minor))\"".Trim(),
                    Path.GetTempPath(),
                    cancellationToken).ConfigureAwait(false);
                if (run.ExitCode != 0)
                {
                    continue;
                }

                string line = FirstLine(run.Stdout) ?? string.Empty;
                if (string.Equals(line.Trim(), "3.11", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static async Task<ProcessResult> RunProcessAsync(
            string fileName,
            string args,
            string workingDir,
            CancellationToken cancellationToken)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = args,
                        WorkingDirectory = workingDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    }
                };

                process.Start();
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                string stdout = await stdoutTask.ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);
                return new ProcessResult(process.ExitCode, stdout, stderr, false);
            }
            catch (Exception ex) when (
                ex is FileNotFoundException ||
                ex is DirectoryNotFoundException ||
                ex is System.ComponentModel.Win32Exception)
            {
                return new ProcessResult(-1, string.Empty, ex.Message, true);
            }
        }

        private static ProcessResult RunProcess(string fileName, string args, string workingDir, int timeoutMs)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = args,
                        WorkingDirectory = workingDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    }
                };

                process.Start();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return new ProcessResult(-1, string.Empty, "timeout", false);
                }

                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                return new ProcessResult(process.ExitCode, stdout, stderr, false);
            }
            catch (Exception ex)
            {
                return new ProcessResult(-1, string.Empty, ex.Message, true);
            }
        }

        private static string? TryResolveFromPath(string command)
        {
            ProcessResult whereResult = RunProcess("where.exe", command, Path.GetTempPath(), 3000);
            if (whereResult.ExitCode != 0 || string.IsNullOrWhiteSpace(whereResult.Stdout))
            {
                return null;
            }

            return whereResult.Stdout
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        }

        private static void ParseCommand(string command, out string fileName, out string argPrefix)
        {
            fileName = string.Empty;
            argPrefix = string.Empty;
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            string trimmed = command.Trim();
            if (trimmed.StartsWith("\"", StringComparison.Ordinal))
            {
                int endQuote = trimmed.IndexOf('"', 1);
                if (endQuote > 1)
                {
                    fileName = trimmed[1..endQuote];
                    argPrefix = trimmed[(endQuote + 1)..].Trim();
                    return;
                }
            }

            int space = trimmed.IndexOf(' ');
            if (space < 0)
            {
                fileName = trimmed;
                return;
            }

            fileName = trimmed[..space];
            argPrefix = trimmed[(space + 1)..].Trim();
        }

        private static string? FirstLine(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        }

        private readonly record struct PythonLaunchSpec(string FileName, string ArgPrefix, string DisplayName);
        private readonly record struct ProcessResult(int ExitCode, string Stdout, string Stderr, bool CommandNotFound);
    }

    public sealed record OmrRuntimeStatus
    {
        public bool Python311Ready { get; init; }
        public bool HomrReady { get; init; }
        public bool AudiverisReady { get; init; }
        public string HomrCommand { get; init; } = string.Empty;
        public string AudiverisPath { get; init; } = string.Empty;
        public string RuntimeRoot { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
    }
}
