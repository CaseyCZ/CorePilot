using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CorePilot.Core;

namespace CorePilot.Linux;

public sealed class LinuxIsoPreparationService
{
    private sealed record ResolvedLinuxImage(
        Uri IsoUri,
        Uri ChecksumUri,
        string FileName,
        string ExpectedSha256,
        string Provenance);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly HttpClient _http;

    public LinuxIsoPreparationService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CorePilot/linux-media");
    }

    public async Task<PreparedIsoImage> PrepareAsync(
        SystemVariant target,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(
            $"Resolving the current official {target.DisplayName} x64 ISO and checksum…");

        var resolved = target.Id switch
        {
            "ubuntu" => await ResolveUbuntuAsync(cancellationToken),
            "fedora" => await ResolveFedoraAsync(cancellationToken),
            "debian" => await ResolveDebianAsync(cancellationToken),
            "linux-mint" => await ResolveLinuxMintAsync(cancellationToken),
            _ => throw new InvalidOperationException(
                $"Unsupported Linux target '{target.Id}'.")
        };

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CorePilot",
            "media",
            "linux",
            target.Id);

        Directory.CreateDirectory(root);

        var isoPath = Path.Combine(root, resolved.FileName);
        var manifestPath =
            Path.Combine(root, resolved.FileName + ".corepilot.json");

        if (File.Exists(isoPath))
        {
            progress?.Report("Validating the cached Linux ISO against the current official checksum…");
            var cachedSha = await ComputeSha256Async(
                isoPath,
                cancellationToken);

            if (cachedSha.Equals(
                    resolved.ExpectedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                await WriteManifestAsync(
                    manifestPath,
                    target,
                    resolved,
                    isoPath,
                    cachedSha,
                    cached: true,
                    cancellationToken);

                var cachedInfo = new FileInfo(isoPath);

                return new(
                    "linux",
                    target.Id,
                    target.DisplayName,
                    isoPath,
                    resolved.FileName,
                    resolved.IsoUri.ToString(),
                    cachedSha,
                    resolved.ExpectedSha256,
                    IntegrityVerified: true,
                    cachedInfo.Length,
                    manifestPath,
                    resolved.Provenance);
            }

            File.Delete(isoPath);
        }

        progress?.Report($"Downloading {resolved.FileName}…");
        await DownloadIsoAsync(
            resolved.IsoUri,
            isoPath,
            progress,
            cancellationToken);

        progress?.Report("Verifying Linux ISO SHA-256 against the official checksum…");
        var sha256 = await ComputeSha256Async(isoPath, cancellationToken);

        if (!sha256.Equals(
                resolved.ExpectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(isoPath);
            throw new InvalidDataException(
                $"Linux ISO SHA-256 mismatch. Expected {resolved.ExpectedSha256}, got {sha256}.");
        }

        var info = new FileInfo(isoPath);
        if (info.Length < 300L * 1024L * 1024L)
            throw new InvalidDataException(
                "Downloaded Linux ISO is unexpectedly small.");

        await WriteManifestAsync(
            manifestPath,
            target,
            resolved,
            isoPath,
            sha256,
            cached: false,
            cancellationToken);

        progress?.Report(
            $"Linux ISO prepared and checksum-verified · {info.Length / 1024d / 1024d / 1024d:0.00} GB · SHA-256 {sha256[..16]}…");

        return new(
            "linux",
            target.Id,
            target.DisplayName,
            isoPath,
            resolved.FileName,
            resolved.IsoUri.ToString(),
            sha256,
            resolved.ExpectedSha256,
            IntegrityVerified: true,
            info.Length,
            manifestPath,
            resolved.Provenance);
    }

    private async Task<ResolvedLinuxImage> ResolveUbuntuAsync(
        CancellationToken cancellationToken)
    {
        var downloadPage = new Uri("https://ubuntu.com/download/desktop");
        var (html, finalDownloadUri) = await GetTextWithFinalUriAsync(
            downloadPage,
            cancellationToken);

        var isoHref = MatchFirst(
            html,
            @"href\s*=\s*[""'](?<value>https://(?:[a-z0-9.-]+\.)?releases\.ubuntu\.com/(?:releases/)?[^""']*/ubuntu-[^""'/]+-desktop-amd64\.iso)[""']",
            "current Ubuntu desktop amd64 ISO");

        var isoUri = new Uri(WebUtility.HtmlDecode(isoHref));

        if (isoUri.Scheme != Uri.UriSchemeHttps ||
            !isoUri.Host.EndsWith(
                "releases.ubuntu.com",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Ubuntu download page returned an unexpected ISO host: {isoUri.Host}");

        var fileName = Path.GetFileName(isoUri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidDataException(
                "Ubuntu download page returned an ISO URL without a filename.");

        var releaseDirectory = new Uri(isoUri, "./");
        var checksumUri = new Uri(releaseDirectory, "SHA256SUMS");
        var checksums = await _http.GetStringAsync(
            checksumUri,
            cancellationToken);

        var expected = ParseStandardSha256Sums(
            checksums,
            fileName);

        return new(
            isoUri,
            checksumUri,
            fileName,
            expected,
            $"Current Ubuntu desktop image discovered from {finalDownloadUri.Host} and verified against SHA256SUMS from {checksumUri.Host}.");
    }

    private async Task<ResolvedLinuxImage> ResolveDebianAsync(
        CancellationToken cancellationToken)
    {
        var pageUri = new Uri(
            "https://cdimage.debian.org/debian-cd/current/amd64/iso-cd/");

        var (html, finalPageUri) = await GetTextWithFinalUriAsync(
            pageUri,
            cancellationToken);

        var href = MatchFirst(
            html,
            @"href\s*=\s*[""'](?<value>debian-[^""']+-amd64-netinst\.iso)[""']",
            "Debian amd64 netinst ISO");

        var fileName = Path.GetFileName(
            WebUtility.HtmlDecode(href));

        var checksumUri = new Uri(finalPageUri, "SHA256SUMS");
        var checksums = await _http.GetStringAsync(
            checksumUri,
            cancellationToken);

        var expected = ParseStandardSha256Sums(
            checksums,
            fileName);

        return new(
            new Uri(finalPageUri, fileName),
            checksumUri,
            fileName,
            expected,
            "Debian current amd64 netinst image verified against the official Debian SHA256SUMS.");
    }

    private async Task<ResolvedLinuxImage> ResolveFedoraAsync(
        CancellationToken cancellationToken)
    {
        var pageUri = new Uri(
            "https://fedoraproject.org/workstation/download/");

        var (html, _) = await GetTextWithFinalUriAsync(
            pageUri,
            cancellationToken);

        var checksumHref = MatchFirst(
            html,
            @"href\s*=\s*[""'](?<value>https://dl\.fedoraproject\.org/[^""']+Workstation[^""']+x86_64-CHECKSUM)[""']",
            "Fedora Workstation x86_64 checksum");

        var checksumUri = new Uri(
            WebUtility.HtmlDecode(checksumHref));

        var checksumText = await _http.GetStringAsync(
            checksumUri,
            cancellationToken);

        var match = Regex.Match(
            checksumText,
            @"SHA256\s*\((?<file>Fedora-Workstation-Live-[^)]+(?:\.|-)x86_64\.iso)\)\s*=\s*(?<hash>[0-9A-Fa-f]{64})",
            RegexOptions.IgnoreCase);

        if (!match.Success)
            throw new InvalidDataException(
                "Fedora checksum file did not contain the Workstation x86_64 ISO.");

        var fileName = match.Groups["file"].Value;
        var expected = match.Groups["hash"].Value.ToUpperInvariant();

        return new(
            new Uri(checksumUri, fileName),
            checksumUri,
            fileName,
            expected,
            "Fedora Workstation image verified against the official Fedora x86_64 CHECKSUM file.");
    }

    private async Task<ResolvedLinuxImage> ResolveLinuxMintAsync(
        CancellationToken cancellationToken)
    {
        var downloadPage = new Uri(
            "https://linuxmint.com/download.php");

        var (downloadHtml, finalDownloadUri) =
            await GetTextWithFinalUriAsync(
                downloadPage,
                cancellationToken);

        var editionHref = MatchFirst(
            downloadHtml,
            @"href\s*=\s*[""'](?<value>edition\.php\?id=\d+)[""'][^>]*>\s*Download",
            "Linux Mint Cinnamon edition");

        var editionUri = new Uri(
            finalDownloadUri,
            WebUtility.HtmlDecode(editionHref));

        var (editionHtml, _) = await GetTextWithFinalUriAsync(
            editionUri,
            cancellationToken);

        var checksumHref = MatchFirst(
            editionHtml,
            @"href\s*=\s*[""'](?<value>https://[^""']+/sha256sum\.txt)[""']",
            "Linux Mint SHA-256 checksum");

        var checksumUri = new Uri(
            WebUtility.HtmlDecode(checksumHref));

        var checksumText = await _http.GetStringAsync(
            checksumUri,
            cancellationToken);

        var lines = checksumText.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            var match = Regex.Match(
                line,
                @"^(?<hash>[0-9A-Fa-f]{64})\s+\*?(?<file>linuxmint-[^\s]+-cinnamon-64bit\.iso)$",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                continue;

            var fileName = match.Groups["file"].Value;
            var expected = match.Groups["hash"].Value.ToUpperInvariant();

            return new(
                new Uri(checksumUri, fileName),
                checksumUri,
                fileName,
                expected,
                "Linux Mint Cinnamon image verified against the sha256sum.txt linked by linuxmint.com.");
        }

        throw new InvalidDataException(
            "Linux Mint checksum file did not contain the Cinnamon 64-bit ISO.");
    }

    private async Task<(string Text, Uri FinalUri)> GetTextWithFinalUriAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var finalUri = response.RequestMessage?.RequestUri
            ?? throw new InvalidOperationException(
                "HTTP source did not expose a final URI.");

        if (finalUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                "Linux media metadata redirected outside HTTPS.");

        var text = await response.Content.ReadAsStringAsync(
            cancellationToken);

        return (text, finalUri);
    }

    private async Task DownloadIsoAsync(
        Uri uri,
        string destination,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        const long maxIsoBytes = 10L * 1024L * 1024L * 1024L;

        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                "Linux ISO source must use HTTPS.");

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
                    "Linux ISO download did not expose a final URI.");

            if (finalUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException(
                    "Linux ISO redirected outside HTTPS.");

            var expectedLength = response.Content.Headers.ContentLength;
            if (expectedLength is > maxIsoBytes)
                throw new InvalidDataException(
                    "Linux ISO is unexpectedly large.");

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
                        "Linux ISO exceeded the maximum accepted size.");

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
                            $"Downloading Linux ISO… {percent}% ({total / 1024d / 1024d / 1024d:0.00} GB)");
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

    private static string ParseStandardSha256Sums(
        string text,
        string fileName)
    {
        foreach (var line in text.Split(
                     new[] { '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Regex.Match(
                line,
                @"^(?<hash>[0-9A-Fa-f]{64})\s+\*?(?<file>.+)$");

            if (!match.Success)
                continue;

            if (match.Groups["file"].Value.Trim().Equals(
                    fileName,
                    StringComparison.OrdinalIgnoreCase))
                return match.Groups["hash"].Value.ToUpperInvariant();
        }

        throw new InvalidDataException(
            $"Official checksum list did not contain '{fileName}'.");
    }

    private static string MatchFirst(
        string text,
        string pattern,
        string description)
    {
        var match = Regex.Match(
            text,
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match.Success)
            throw new InvalidDataException(
                $"Could not resolve {description} from the official page.");

        return match.Groups["value"].Value;
    }

    private static async Task WriteManifestAsync(
        string path,
        SystemVariant target,
        ResolvedLinuxImage resolved,
        string isoPath,
        string sha256,
        bool cached,
        CancellationToken cancellationToken)
    {
        var document = new
        {
            schemaVersion = 1,
            preparedAt = DateTimeOffset.UtcNow,
            system = "linux",
            target = target.Id,
            targetName = target.DisplayName,
            architecture = "x86_64",
            source = new
            {
                isoUrl = resolved.IsoUri.ToString(),
                checksumUrl = resolved.ChecksumUri.ToString(),
                provenance = resolved.Provenance
            },
            image = new
            {
                path = isoPath,
                fileName = Path.GetFileName(isoPath),
                sizeBytes = new FileInfo(isoPath).Length,
                expectedSha256 = resolved.ExpectedSha256,
                sha256,
                verified = true,
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
}
