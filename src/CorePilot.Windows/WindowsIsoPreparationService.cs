using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CorePilot.Core;

namespace CorePilot.Windows;

public sealed class WindowsIsoPreparationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly OnlineSourceCatalogService _sources;
    private readonly HttpClient _http;

    public WindowsIsoPreparationService(
        OnlineSourceCatalogService? sources = null,
        HttpClient? httpClient = null)
    {
        _sources = sources ?? new OnlineSourceCatalogService();
        _http = httpClient ?? new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CorePilot/windows-media");
    }

    public async Task<PreparedIsoImage> PrepareAsync(
        SystemVariant target,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Windows ISO preparation currently runs on Windows.");

        if (target.Id is not ("windows-11" or "windows-10"))
            throw new InvalidOperationException(
                $"Unsupported Windows target '{target.Id}'.");

        progress?.Report("Resolving the current verified Windows ISO resolver…");

        var fido = await _sources.ResolveRequiredSourceAsync(
            "windows.fido",
            requireLive: true,
            cancellationToken);

        if (fido.VerifiedCommit is not true ||
            string.IsNullOrWhiteSpace(fido.ResolvedRef))
            throw new InvalidOperationException(
                "Fido did not resolve to a GitHub-verified commit.");

        var commit = fido.ResolvedRef;
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CorePilot",
            "media",
            "windows",
            target.Id);

        var toolDir = Path.Combine(root, "fido", commit);
        Directory.CreateDirectory(toolDir);

        var scriptPath = Path.Combine(toolDir, "Fido.ps1");
        var scriptUrl =
            $"https://raw.githubusercontent.com/pbatard/Fido/{commit}/Fido.ps1";

        progress?.Report($"Staging Fido from verified commit {commit[..8]}…");
        await DownloadSmallTrustedFileAsync(
            new Uri(scriptUrl),
            scriptPath,
            maxBytes: 2 * 1024 * 1024,
            cancellationToken);

        var scriptSha = await ComputeSha256Async(scriptPath, cancellationToken);

        progress?.Report(
            $"Resolving the current official Microsoft {target.DisplayName} x64 ISO URL…");

        var isoUrl = await ResolveIsoUrlWithFidoAsync(
            scriptPath,
            target,
            cancellationToken);

        ValidateMicrosoftIsoUri(isoUrl);

        var fileName = Path.GetFileName(isoUrl.AbsolutePath);
        if (string.IsNullOrWhiteSpace(fileName) ||
            !fileName.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The Microsoft media resolver did not return an ISO filename.");

        Directory.CreateDirectory(root);
        var isoPath = Path.Combine(root, fileName);
        var manifestPath = Path.Combine(root, fileName + ".corepilot.json");

        if (File.Exists(isoPath))
        {
            progress?.Report("Validating the cached official Windows ISO…");
            var cachedSha = await ComputeSha256Async(isoPath, cancellationToken);
            var cachedInfo = new FileInfo(isoPath);

            if (cachedInfo.Length >= 1024L * 1024L * 1024L)
            {
                await WriteManifestAsync(
                    manifestPath,
                    target,
                    isoUrl,
                    isoPath,
                    cachedSha,
                    scriptUrl,
                    commit,
                    scriptSha,
                    cached: true,
                    cancellationToken);

                return new(
                    "windows",
                    target.Id,
                    target.DisplayName,
                    isoPath,
                    fileName,
                    isoUrl.ToString(),
                    cachedSha,
                    ExpectedSha256: null,
                    IntegrityVerified: true,
                    cachedInfo.Length,
                    manifestPath,
                    $"Official Microsoft retail ISO URL resolved through Fido verified commit {commit}.");
            }

            File.Delete(isoPath);
        }

        progress?.Report($"Downloading official Microsoft ISO: {fileName}…");
        await DownloadLargeIsoAsync(
            isoUrl,
            isoPath,
            progress,
            cancellationToken);

        var sha256 = await ComputeSha256Async(isoPath, cancellationToken);
        var info = new FileInfo(isoPath);

        if (info.Length < 1024L * 1024L * 1024L)
            throw new InvalidDataException(
                "Downloaded Windows ISO is unexpectedly small.");

        await WriteManifestAsync(
            manifestPath,
            target,
            isoUrl,
            isoPath,
            sha256,
            scriptUrl,
            commit,
            scriptSha,
            cached: false,
            cancellationToken);

        progress?.Report(
            $"Windows ISO prepared · {info.Length / 1024d / 1024d / 1024d:0.00} GB · SHA-256 {sha256[..16]}…");

        return new(
            "windows",
            target.Id,
            target.DisplayName,
            isoPath,
            fileName,
            isoUrl.ToString(),
            sha256,
            ExpectedSha256: null,
            IntegrityVerified: true,
            info.Length,
            manifestPath,
            $"Official Microsoft retail ISO URL resolved through Fido verified commit {commit}.");
    }

    private static async Task<Uri> ResolveIsoUrlWithFidoAsync(
        string scriptPath,
        SystemVariant target,
        CancellationToken cancellationToken)
    {
        var version = target.Id == "windows-11" ? "11" : "10";

        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in new[]
        {
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath,
            "-Win",
            version,
            "-Rel",
            "Latest",
            "-Arch",
            "x64",
            "-GetUrl"
        })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Unable to start the verified Windows ISO resolver.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Windows ISO resolver exited with code {process.ExitCode}: {LastUsefulLine(stderr, stdout)}");

        var urls = Regex.Matches(
                stdout,
                @"https://[^\s""']+",
                RegexOptions.IgnoreCase)
            .Select(x => x.Value.TrimEnd('.', ',', ';'))
            .ToArray();

        foreach (var value in urls.Reverse())
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                uri.AbsolutePath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                return uri;
        }

        throw new InvalidOperationException(
            "The verified Windows ISO resolver did not return an official ISO URL.");
    }

    private async Task DownloadLargeIsoAsync(
        Uri uri,
        string destination,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        const long maxIsoBytes = 12L * 1024L * 1024L * 1024L;
        var temp = destination + ".download";

        try
        {
            using var response = await _http.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var finalUri = response.RequestMessage?.RequestUri
                ?? throw new InvalidOperationException(
                    "Windows ISO download did not expose a final URI.");

            ValidateMicrosoftIsoUri(finalUri);

            var expectedLength = response.Content.Headers.ContentLength;
            if (expectedLength is > maxIsoBytes)
                throw new InvalidDataException(
                    "Windows ISO is unexpectedly large.");

            await using var input =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                temp,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[1024 * 1024];
            long total = 0;
            var lastReportedPercent = -1;

            while (true)
            {
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken);

                if (read == 0)
                    break;

                total += read;
                if (total > maxIsoBytes)
                    throw new InvalidDataException(
                        "Windows ISO exceeded the maximum accepted size.");

                await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);

                if (expectedLength is > 0)
                {
                    var percent = (int)Math.Min(
                        100,
                        total * 100L / expectedLength.Value);

                    if (percent >= lastReportedPercent + 5)
                    {
                        lastReportedPercent = percent;
                        progress?.Report(
                            $"Downloading Windows ISO… {percent}% ({total / 1024d / 1024d / 1024d:0.00} GB)");
                    }
                }
            }

            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);

            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private async Task DownloadSmallTrustedFileAsync(
        Uri uri,
        string destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals(
                "raw.githubusercontent.com",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Unexpected Windows ISO resolver source.");

        using var response = await _http.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var finalUri = response.RequestMessage?.RequestUri
            ?? throw new InvalidOperationException(
                "Resolver source did not expose a final URI.");

        if (finalUri.Scheme != Uri.UriSchemeHttps ||
            !finalUri.Host.Equals(
                "raw.githubusercontent.com",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Resolver source redirected outside the trusted GitHub raw host.");

        if (response.Content.Headers.ContentLength is long length &&
            length > maxBytes)
            throw new InvalidDataException(
                "Resolver script is unexpectedly large.");

        await using var input =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous);

        var buffer = new byte[64 * 1024];
        long total = 0;

        while (true)
        {
            var read = await input.ReadAsync(
                buffer.AsMemory(0, buffer.Length),
                cancellationToken);

            if (read == 0)
                break;

            total += read;
            if (total > maxBytes)
                throw new InvalidDataException(
                    "Resolver script exceeded the maximum accepted size.");

            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }

        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private static void ValidateMicrosoftIsoUri(Uri uri)
    {
        var host = uri.Host;

        if (uri.Scheme != Uri.UriSchemeHttps ||
            !(host.Equals("microsoft.com", StringComparison.OrdinalIgnoreCase) ||
              host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Windows ISO URL is outside Microsoft HTTPS infrastructure: {host}");

        if (!uri.AbsolutePath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Windows media URL is not an ISO.");
    }

    private static async Task WriteManifestAsync(
        string path,
        SystemVariant target,
        Uri isoUrl,
        string isoPath,
        string isoSha,
        string scriptUrl,
        string commit,
        string scriptSha,
        bool cached,
        CancellationToken cancellationToken)
    {
        var document = new
        {
            schemaVersion = 1,
            preparedAt = DateTimeOffset.UtcNow,
            system = "windows",
            target = target.Id,
            targetName = target.DisplayName,
            architecture = "x64",
            source = new
            {
                officialIsoUrl = isoUrl.ToString(),
                resolverRepository = "pbatard/Fido",
                resolverCommit = commit,
                resolverVerifiedCommit = true,
                resolverScriptUrl = scriptUrl,
                resolverScriptSha256 = scriptSha
            },
            image = new
            {
                path = isoPath,
                fileName = Path.GetFileName(isoPath),
                sizeBytes = new FileInfo(isoPath).Length,
                sha256 = isoSha,
                cached
            }
        };

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(document, JsonOptions),
            new UTF8Encoding(false),
            cancellationToken);
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

    private static string LastUsefulLine(params string[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var lines = value.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (lines.Length > 0)
                return lines[^1];
        }

        return "no diagnostic output";
    }
}
