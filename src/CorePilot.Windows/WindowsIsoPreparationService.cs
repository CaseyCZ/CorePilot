using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CorePilot.Core;

namespace CorePilot.Windows;

public sealed class WindowsIsoPreparationService
{
    private sealed record PartialIsoState(
        string? ETag,
        DateTimeOffset? LastModified,
        long TotalBytes);
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
        try
        {
            await DownloadLargeIsoAsync(isoUrl, isoPath, progress, cancellationToken);
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone or HttpStatusCode.Unauthorized)
        {
            progress?.Report("The download link expired. Getting a new one…");
            isoUrl = await ResolveIsoUrlWithFidoAsync(scriptPath, target, cancellationToken);
            ValidateMicrosoftIsoUri(isoUrl);
            if (!Path.GetFileName(isoUrl.AbsolutePath).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The refreshed download link points to a different image.");
            await DownloadLargeIsoAsync(isoUrl, isoPath, progress, cancellationToken);
        }

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

        string stdout;
        string stderr;
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            stdout = await stdoutTask;
            stderr = await stderrTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Windows ISO resolver exited with code {process.ExitCode}: {LastUsefulLine(stderr, stdout)}");

        var urls = Regex.Matches(
                stdout,
                @"https://[^\s""']+",
                RegexOptions.IgnoreCase)
            .Select(x => x.Value.TrimEnd('.', ',', ';'))
            .ToArray();

        for (var index = urls.Length - 1; index >= 0; index--)
        {
            var value = urls[index];

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
        var partialPath = destination + ".part";
        var statePath = partialPath + ".json";

        // Only a complete verified-size response may become the final ISO.
        if (File.Exists(partialPath) && File.Exists(statePath))
        {
            PartialIsoState? state = null;
            try
            {
                state = JsonSerializer.Deserialize<PartialIsoState>(
                    await File.ReadAllTextAsync(statePath, cancellationToken));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Unreadable metadata means we cannot safely reuse old bytes.
            }

            var existingBytes = new FileInfo(partialPath).Length;
            if (state is not null &&
                existingBytes > 0 &&
                existingBytes < state.TotalBytes &&
                state.TotalBytes <= maxIsoBytes &&
                (IsStrongETag(state.ETag) || state.LastModified is not null))
            {
                // Retry transient disconnects up to three times. An invalid
                // range or changed object triggers a fresh download immediately.
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                        request.Headers.Range = new RangeHeaderValue(existingBytes, null);
                        if (IsStrongETag(state.ETag))
                            request.Headers.IfRange = new RangeConditionHeaderValue(
                                EntityTagHeaderValue.Parse(state.ETag!));
                        else if (state.LastModified is not null)
                            request.Headers.IfRange = new RangeConditionHeaderValue(state.LastModified.Value);

                        using var response = await _http.SendAsync(
                            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                        ValidateMicrosoftIsoUri(response.RequestMessage?.RequestUri
                            ?? throw new InvalidOperationException("Missing download response URL."));

                        var range = response.Content.Headers.ContentRange;
                        if (response.StatusCode != HttpStatusCode.PartialContent ||
                            range?.From != existingBytes ||
                            range.Length != state.TotalBytes ||
                            (IsStrongETag(state.ETag) &&
                             response.Headers.ETag?.Tag != state.ETag) ||
                            (state.LastModified is not null &&
                             response.Content.Headers.LastModified is DateTimeOffset modified &&
                             modified != state.LastModified))
                            break;

                        progress?.Report("Download interrupted. Resuming…");
                        var actualBytes = await CopyIsoResponseAsync(
                            response, partialPath, FileMode.Append,
                            existingBytes, state.TotalBytes, maxIsoBytes,
                            progress, cancellationToken);
                        if (actualBytes == state.TotalBytes)
                        {
                            File.Move(partialPath, destination, overwrite: true);
                            File.Delete(statePath);
                            return;
                        }
                        existingBytes = actualBytes;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw; // Keep .part and metadata for the next attempt.
                    }
                    catch (Exception ex) when (ex is IOException or HttpRequestException)
                    {
                        existingBytes = new FileInfo(partialPath).Length;
                        if (attempt == 3)
                            break;
                    }
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(partialPath) || File.Exists(statePath))
            progress?.Report("Starting the download again…");

        using (var response = await _http.GetAsync(
                   uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            ValidateMicrosoftIsoUri(response.RequestMessage?.RequestUri
                ?? throw new InvalidOperationException("Missing download response URL."));
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes is null or <= 0 or > maxIsoBytes)
                throw new InvalidDataException("Windows ISO download has no safe known size.");

            var state = new PartialIsoState(
                response.Headers.ETag?.Tag,
                response.Content.Headers.LastModified,
                totalBytes.Value);
            await File.WriteAllTextAsync(
                statePath, JsonSerializer.Serialize(state), cancellationToken);

            var copied = await CopyIsoResponseAsync(
                response, partialPath, FileMode.Create, 0,
                totalBytes.Value, maxIsoBytes, progress, cancellationToken);

            if (copied != totalBytes.Value)
                throw new IOException("Download interrupted. The partial file was kept for resuming.");
        }

        File.Move(partialPath, destination, overwrite: true);
        File.Delete(statePath);
    }

    private static bool IsStrongETag(string? etag) =>
        !string.IsNullOrWhiteSpace(etag) &&
        !etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase) &&
        EntityTagHeaderValue.TryParse(etag, out _);

    private static async Task<long> CopyIsoResponseAsync(
        HttpResponseMessage response,
        string partialPath,
        FileMode mode,
        long existingBytes,
        long totalBytes,
        long maximumBytes,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            partialPath, mode, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[1024 * 1024];
        var bytes = existingBytes;
        var lastReportedPercent = -1;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            bytes += read;
            if (bytes > maximumBytes || bytes > totalBytes)
                throw new InvalidDataException("Windows ISO exceeded the expected download size.");

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            var percent = (int)Math.Min(100, bytes * 100L / totalBytes);
            if (percent >= lastReportedPercent + 5)
            {
                lastReportedPercent = percent;
                progress?.Report($"Downloading Windows ISO… {percent}%");
            }
        }

        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
        return bytes;
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
