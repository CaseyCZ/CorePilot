using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace CorePilot.Hardware;

public sealed record HardwareSnifferExportResult(
    string Version,
    string ToolPath,
    string ToolSha256,
    string ReportPath,
    string AcpiDirectory,
    string OutputDirectory,
    string ProcessOutput);

public sealed class HardwareSnifferBridge
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/lzhoang2801/Hardware-Sniffer/releases/latest";

    private readonly HttpClient _http;

    public HardwareSnifferBridge(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CorePilot/0.3");
    }

    public async Task<HardwareSnifferExportResult> ExportAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("Checking latest Hardware Sniffer release…");

        using var releaseStream = await _http.GetStreamAsync(LatestReleaseApi, cancellationToken);
        using var release = await JsonDocument.ParseAsync(releaseStream, cancellationToken: cancellationToken);

        var root = release.RootElement;
        var version = root.TryGetProperty("tag_name", out var tag)
            ? tag.GetString() ?? "unknown"
            : "unknown";

        if (!root.TryGetProperty("assets", out var assets))
            throw new InvalidOperationException("Hardware Sniffer release has no assets list.");

        string? downloadUrl = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name) ||
                !string.Equals(name.GetString(), "Hardware-Sniffer-CLI.exe", StringComparison.OrdinalIgnoreCase))
                continue;

            if (asset.TryGetProperty("browser_download_url", out var url))
                downloadUrl = url.GetString();
            break;
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("Hardware-Sniffer-CLI.exe was not found in the latest release.");

        var uri = new Uri(downloadUrl);
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected Hardware Sniffer download source.");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var toolDirectory = Path.Combine(appData, "CorePilot", "tools", "hardware-sniffer", Sanitize(version));
        var toolPath = Path.Combine(toolDirectory, "Hardware-Sniffer-CLI.exe");
        Directory.CreateDirectory(toolDirectory);

        if (!File.Exists(toolPath))
        {
            progress?.Report($"Downloading Hardware Sniffer {version}…");
            var tempPath = toolPath + ".download";

            await using (var input = await _http.GetStreamAsync(uri, cancellationToken))
            await using (var output = File.Create(tempPath))
                await input.CopyToAsync(output, cancellationToken);

            File.Move(tempPath, toolPath, true);
        }
        else
        {
            progress?.Report($"Using cached Hardware Sniffer {version}…");
        }

        var sha256 = await ComputeSha256Async(toolPath, cancellationToken);

        var reportDirectory = Path.Combine(
            appData,
            "CorePilot",
            "reports",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));

        Directory.CreateDirectory(reportDirectory);

        progress?.Report("Exporting Report.json and ACPI tables…");

        var startInfo = new ProcessStartInfo
        {
            FileName = toolPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = toolDirectory
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(reportDirectory);

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
            throw new InvalidOperationException("Hardware Sniffer process could not be started.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var processOutput = string.Join(
            Environment.NewLine,
            new[] { stdout, stderr }.Where(x => !string.IsNullOrWhiteSpace(x)));

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Hardware Sniffer exited with code {process.ExitCode}. {stderr}".Trim());

        var reportPath = Path.Combine(reportDirectory, "Report.json");
        var acpiDirectory = Path.Combine(reportDirectory, "ACPI");

        if (!File.Exists(reportPath))
            throw new InvalidOperationException(
                $"Hardware Sniffer finished but Report.json was not created in {reportDirectory}.");

        progress?.Report("Hardware Sniffer export complete.");

        return new HardwareSnifferExportResult(
            version,
            toolPath,
            sha256,
            reportPath,
            acpiDirectory,
            reportDirectory,
            processOutput);
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

    private static string Sanitize(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value;
    }
}
