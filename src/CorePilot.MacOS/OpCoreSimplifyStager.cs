using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace CorePilot.MacOS;

public sealed record PythonRuntimeInfo(
    bool Available,
    string Command,
    string ExecutablePath,
    string Version);

public sealed record OpCoreStagingResult(
    string WorkspaceDirectory,
    string UpstreamDirectory,
    string UpstreamCommit,
    string ArchiveSha256,
    string ReportPath,
    string AcpiDirectory,
    string AutomationProfilePath,
    bool PythonAvailable,
    string PythonCommand,
    string PythonExecutable,
    string PythonVersion);

public sealed class OpCoreSimplifyStager
{
    // Pinned to the upstream revision reviewed for CorePilot v0.4.
    public const string UpstreamCommit = "e5d8a9f551b1e2a96e2f968696b460b65a7dad2e";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly HttpClient _http;

    public OpCoreSimplifyStager(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CorePilot/0.4");
    }

    public async Task<OpCoreStagingResult> StageAsync(
        string reportPath,
        string acpiDirectory,
        MacOSAutomationProfile automationProfile,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(reportPath))
            throw new FileNotFoundException("Deep Scan Report.json is missing.", reportPath);

        if (!Directory.Exists(acpiDirectory))
            throw new DirectoryNotFoundException(
                $"Deep Scan ACPI directory is missing: {acpiDirectory}");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var toolCache = Path.Combine(
            appData,
            "CorePilot",
            "tools",
            "opcore-simplify",
            UpstreamCommit);

        var archivePath = Path.Combine(toolCache, "source.zip");
        var extractedRoot = Path.Combine(toolCache, "source");

        Directory.CreateDirectory(toolCache);

        if (!File.Exists(archivePath))
        {
            progress?.Report("Downloading pinned OpCore Simplify source…");

            var archiveUri = new Uri(
                $"https://github.com/lzhoang2801/OpCore-Simplify/archive/{UpstreamCommit}.zip");

            await DownloadHttpsAsync(archiveUri, archivePath, cancellationToken);
        }

        var archiveSha256 = await ComputeSha256Async(archivePath, cancellationToken);

        if (!Directory.Exists(extractedRoot) ||
            !File.Exists(Path.Combine(extractedRoot, ".corepilot-ready")))
        {
            progress?.Report("Extracting OpCore Simplify source…");

            if (Directory.Exists(extractedRoot))
                Directory.Delete(extractedRoot, recursive: true);

            var tempExtract = Path.Combine(toolCache, "extract-temp");
            if (Directory.Exists(tempExtract))
                Directory.Delete(tempExtract, recursive: true);

            Directory.CreateDirectory(tempExtract);
            ZipFile.ExtractToDirectory(archivePath, tempExtract);

            var sourceRoot = Directory
                .EnumerateDirectories(tempExtract)
                .SingleOrDefault()
                ?? throw new InvalidDataException(
                    "Unexpected OpCore Simplify archive layout.");

            Directory.Move(sourceRoot, extractedRoot);
            Directory.Delete(tempExtract, recursive: true);
            await File.WriteAllTextAsync(
                Path.Combine(extractedRoot, ".corepilot-ready"),
                UpstreamCommit,
                cancellationToken);
        }

        ValidateUpstreamSource(extractedRoot);

        var workspace = Path.Combine(
            appData,
            "CorePilot",
            "workspaces",
            $"macos-{automationProfile.TargetId}-{DateTime.Now:yyyyMMdd-HHmmss}");

        var sysReport = Path.Combine(workspace, "SysReport");
        var stagedAcpi = Path.Combine(sysReport, "ACPI");
        var stagedReport = Path.Combine(sysReport, "Report.json");
        var stagedProfile = Path.Combine(workspace, "CorePilotAutomationProfile.json");

        Directory.CreateDirectory(stagedAcpi);
        File.Copy(reportPath, stagedReport, overwrite: true);
        CopyDirectory(acpiDirectory, stagedAcpi);

        var profileJson = JsonSerializer.Serialize(automationProfile, JsonOptions);
        await File.WriteAllTextAsync(stagedProfile, profileJson, cancellationToken);

        var python = await DetectPythonAsync(cancellationToken);

        var manifest = new
        {
            createdAt = DateTimeOffset.Now,
            upstream = new
            {
                repository = "lzhoang2801/OpCore-Simplify",
                commit = UpstreamCommit,
                archiveSha256
            },
            input = new
            {
                report = stagedReport,
                acpi = stagedAcpi,
                profile = stagedProfile
            },
            python
        };

        await File.WriteAllTextAsync(
            Path.Combine(workspace, "CorePilotWorkspace.json"),
            JsonSerializer.Serialize(manifest, JsonOptions),
            cancellationToken);

        progress?.Report("OpenCore workspace is ready.");

        return new(
            workspace,
            extractedRoot,
            UpstreamCommit,
            archiveSha256,
            stagedReport,
            stagedAcpi,
            stagedProfile,
            python.Available,
            python.Command,
            python.ExecutablePath,
            python.Version);
    }

    private async Task DownloadHttpsAsync(
        Uri uri,
        string destination,
        CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected OpCore Simplify source URL.");

        var temp = destination + ".download";

        try
        {
            await using var input = await _http.GetStreamAsync(uri, cancellationToken);
            await using var output = File.Create(temp);
            await input.CopyToAsync(output, cancellationToken);
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static void ValidateUpstreamSource(string root)
    {
        var required = new[]
        {
            "OpCore-Simplify.py",
            Path.Combine("Scripts", "compatibility_checker.py"),
            Path.Combine("Scripts", "hardware_customizer.py"),
            Path.Combine("Scripts", "acpi_guru.py"),
            Path.Combine("Scripts", "kext_maestro.py"),
            Path.Combine("Scripts", "smbios.py"),
            Path.Combine("Scripts", "config_prodigy.py")
        };

        var missing = required
            .Where(path => !File.Exists(Path.Combine(root, path)))
            .ToArray();

        if (missing.Length > 0)
            throw new InvalidDataException(
                "Pinned OpCore Simplify source is incomplete: " +
                string.Join(", ", missing));
    }

    private static async Task<PythonRuntimeInfo> DetectPythonAsync(
        CancellationToken cancellationToken)
    {
        var candidates = new[]
        {
            (Command: "py", Args: new[] { "-3", "-c", "import sys;print(sys.executable);print(sys.version.split()[0])" }),
            (Command: "python", Args: new[] { "-c", "import sys;print(sys.executable);print(sys.version.split()[0])" }),
            (Command: "python3", Args: new[] { "-c", "import sys;print(sys.executable);print(sys.version.split()[0])" })
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = candidate.Command,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                foreach (var arg in candidate.Args)
                    psi.ArgumentList.Add(arg);

                using var process = new Process { StartInfo = psi };
                if (!process.Start())
                    continue;

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await process.WaitForExitAsync(cancellationToken);

                var output = await outputTask;
                _ = await errorTask;

                if (process.ExitCode != 0)
                    continue;

                var lines = output
                    .Split(
                        new[] { '\r', '\n' },
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (lines.Length >= 2)
                    return new(true, candidate.Command, lines[0], lines[1]);
            }
            catch
            {
                // Try the next known Python launcher.
            }
        }

        return new(false, "", "", "");
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(
                directory,
                Path.Combine(destination, Path.GetFileName(directory)));
    }
}
