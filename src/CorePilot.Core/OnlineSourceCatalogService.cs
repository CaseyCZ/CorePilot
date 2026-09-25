using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CorePilot.Core;

public sealed record OnlineSourceDefinition(
    string Id,
    string Name,
    IReadOnlyList<string> Systems,
    string Role,
    string Trust,
    string Strategy,
    string? Url,
    string? Repository,
    string? Branch,
    bool Critical,
    bool ResolveOnVerify,
    bool RequireVerifiedCommit,
    string? Notes);

public sealed record OnlineSourceCatalogDocument(
    int SchemaVersion,
    IReadOnlyList<OnlineSourceDefinition> Sources);

public sealed record OnlineSourceCatalogLoadResult(
    OnlineSourceCatalogDocument Catalog,
    string Origin,
    bool FromRemote);

public sealed record OnlineSourceResolution(
    string Id,
    string Name,
    string Role,
    string Trust,
    string Strategy,
    string? Repository,
    string? SourceUrl,
    bool Critical,
    bool Success,
    bool Live,
    bool FromCache,
    string? Version,
    string? ResolvedRef,
    DateTimeOffset? PublishedAt,
    bool? VerifiedCommit,
    DateTimeOffset CheckedAt,
    string? Error);

public sealed record OnlineSourceSnapshot(
    string SystemId,
    DateTimeOffset CheckedAt,
    string CatalogOrigin,
    bool CatalogFromRemote,
    IReadOnlyList<OnlineSourceResolution> Sources)
{
    public int LiveCount => Sources.Count(x => x.Live && x.Success);
    public int CriticalFailures => Sources.Count(x =>
        x.Critical &&
        (!x.Success || !x.Live));
}

public sealed class OnlineSourceCatalogService
{
    private const string CatalogRepository = "CaseyCZ/CorePilot";
    private const string CatalogBranch = "main";
    private const string CatalogPath = "src/CorePilot.Core/Data/source-catalog.json";
    private const string CatalogCommitApi =
        "https://api.github.com/repos/CaseyCZ/CorePilot/commits/main";

    private const string EmbeddedResourceName =
        "CorePilot.Core.Data.source-catalog.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly HttpClient _http;
    private readonly string _cacheDirectory;

    public OnlineSourceCatalogService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };

        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CorePilot/online-sources");

        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        _cacheDirectory = Path.Combine(
            localAppData,
            "CorePilot",
            "cache",
            "sources");

        Directory.CreateDirectory(_cacheDirectory);
    }

    public async Task<OnlineSourceCatalogLoadResult> LoadCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (json, origin) = await DownloadVerifiedRemoteCatalogAsync(
                cancellationToken);

            var catalog = ParseCatalog(json);
            ValidateExecutionCriticalDefinitions(catalog);

            await File.WriteAllTextAsync(
                Path.Combine(_cacheDirectory, "source-catalog.json"),
                json,
                new UTF8Encoding(false),
                cancellationToken);

            return new(catalog, origin, true);
        }
        catch
        {
            var cachedPath = Path.Combine(
                _cacheDirectory,
                "source-catalog.json");

            if (File.Exists(cachedPath))
            {
                try
                {
                    var cached = await File.ReadAllTextAsync(
                        cachedPath,
                        cancellationToken);
                    var catalog = ParseCatalog(cached);
                    ValidateExecutionCriticalDefinitions(catalog);

                    return new(
                        catalog,
                        cachedPath,
                        false);
                }
                catch
                {
                }
            }

            var bundled = LoadBundledCatalog();
            ValidateExecutionCriticalDefinitions(bundled);
            return new(
                bundled,
                EmbeddedResourceName,
                false);
        }
    }

    private async Task<(string Json, string Origin)> DownloadVerifiedRemoteCatalogAsync(
        CancellationToken cancellationToken)
    {
        using var commitResponse = await _http.GetAsync(
            CatalogCommitApi,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        commitResponse.EnsureSuccessStatusCode();

        await using var commitStream = await commitResponse.Content.ReadAsStreamAsync(
            cancellationToken);

        using var commitDocument = await JsonDocument.ParseAsync(
            commitStream,
            cancellationToken: cancellationToken);

        var root = commitDocument.RootElement;
        var sha = root.GetProperty("sha").GetString();

        var verified =
            root.TryGetProperty("commit", out var commit) &&
            commit.TryGetProperty("verification", out var verification) &&
            verification.TryGetProperty("verified", out var verifiedNode) &&
            verifiedNode.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            verifiedNode.GetBoolean();

        if (string.IsNullOrWhiteSpace(sha) || !verified)
            throw new InvalidOperationException(
                "CorePilot online catalog must come from a GitHub-verified main commit.");

        var url =
            $"https://raw.githubusercontent.com/{CatalogRepository}/{sha}/{CatalogPath}";

        var json = await _http.GetStringAsync(url, cancellationToken);
        return (json, url);
    }

    public static OnlineSourceCatalogDocument LoadBundledCatalog()
    {
        using var stream = Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                "Bundled online source catalog is missing.");

        using var reader = new StreamReader(stream);
        return ParseCatalog(reader.ReadToEnd());
    }

    public async Task<OnlineSourceSnapshot> ResolveForSystemAsync(
        string systemId,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadCatalogAsync(cancellationToken);

        var definitions = loaded.Catalog.Sources
            .Where(x =>
                x.ResolveOnVerify &&
                x.Systems.Contains(
                    systemId,
                    StringComparer.OrdinalIgnoreCase))
            .ToArray();

        cancellationToken.ThrowIfCancellationRequested();

        var results = await Task.WhenAll(
            definitions.Select(source =>
                ResolveAsync(
                    source,
                    allowCachedFallback: true,
                    cancellationToken)));

        return new(
            systemId,
            DateTimeOffset.UtcNow,
            loaded.Origin,
            loaded.FromRemote,
            results);
    }

    public async Task<OnlineSourceResolution> ResolveRequiredSourceAsync(
        string sourceId,
        bool requireLive = true,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadCatalogAsync(cancellationToken);
        var source = loaded.Catalog.Sources.FirstOrDefault(x =>
            x.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Online source '{sourceId}' is not in the CorePilot catalog.");

        var result = await ResolveAsync(
            source,
            allowCachedFallback: !requireLive,
            cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException(
                $"Online source '{source.Name}' could not be resolved: {result.Error}");

        if (requireLive && !result.Live)
            throw new InvalidOperationException(
                $"Online source '{source.Name}' is available only from cache.");

        if (source.RequireVerifiedCommit &&
            result.VerifiedCommit is not true)
            throw new InvalidOperationException(
                $"Online source '{source.Name}' did not resolve to a GitHub-verified commit.");

        return result;
    }

    private async Task<OnlineSourceResolution> ResolveAsync(
        OnlineSourceDefinition source,
        bool allowCachedFallback,
        CancellationToken cancellationToken)
    {
        try
        {
            var live = source.Strategy switch
            {
                "githubRelease" => await ResolveGitHubReleaseAsync(
                    source,
                    cancellationToken),
                "githubBranchHead" => await ResolveGitHubBranchHeadAsync(
                    source,
                    cancellationToken),
                "webPage" => await ResolveWebPageAsync(
                    source,
                    cancellationToken),
                _ => throw new InvalidOperationException(
                    $"Unsupported source strategy '{source.Strategy}'.")
            };

            await WriteResolutionCacheAsync(live, cancellationToken);
            return live;
        }
        catch (Exception ex)
        {
            if (allowCachedFallback)
            {
                var cached = await TryReadResolutionCacheAsync(
                    source.Id,
                    cancellationToken);

                if (cached is not null)
                    return cached with
                    {
                        Live = false,
                        FromCache = true,
                        Error = $"Live refresh failed: {ex.Message}",
                        CheckedAt = DateTimeOffset.UtcNow
                    };
            }

            return new(
                source.Id,
                source.Name,
                source.Role,
                source.Trust,
                source.Strategy,
                source.Repository,
                source.Url,
                source.Critical,
                Success: false,
                Live: false,
                FromCache: false,
                Version: null,
                ResolvedRef: null,
                PublishedAt: null,
                VerifiedCommit: null,
                CheckedAt: DateTimeOffset.UtcNow,
                Error: ex.Message);
        }
    }

    private async Task<OnlineSourceResolution> ResolveGitHubReleaseAsync(
        OnlineSourceDefinition source,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.Repository))
            throw new InvalidOperationException("GitHub release source has no repository.");

        var api = new Uri(
            $"https://api.github.com/repos/{source.Repository}/releases/latest");

        using var response = await _http.GetAsync(
            api,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);

        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        var published = root.TryGetProperty("published_at", out var publishedNode) &&
                        publishedNode.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(
                            publishedNode.GetString(),
                            out var parsedPublished)
            ? parsedPublished
            : (DateTimeOffset?)null;

        return new(
            source.Id,
            source.Name,
            source.Role,
            source.Trust,
            source.Strategy,
            source.Repository,
            $"https://github.com/{source.Repository}/releases/latest",
            source.Critical,
            Success: !string.IsNullOrWhiteSpace(tag),
            Live: true,
            FromCache: false,
            Version: tag,
            ResolvedRef: tag,
            PublishedAt: published,
            VerifiedCommit: null,
            CheckedAt: DateTimeOffset.UtcNow,
            Error: null);
    }

    private async Task<OnlineSourceResolution> ResolveGitHubBranchHeadAsync(
        OnlineSourceDefinition source,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.Repository))
            throw new InvalidOperationException("GitHub branch source has no repository.");

        var branch = string.IsNullOrWhiteSpace(source.Branch)
            ? "main"
            : source.Branch;

        var api = new Uri(
            $"https://api.github.com/repos/{source.Repository}/commits/{Uri.EscapeDataString(branch)}");

        using var response = await _http.GetAsync(
            api,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);

        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        var sha = root.GetProperty("sha").GetString();

        bool? verified = null;
        DateTimeOffset? published = null;

        if (root.TryGetProperty("commit", out var commit))
        {
            if (commit.TryGetProperty("verification", out var verification) &&
                verification.TryGetProperty("verified", out var verifiedNode) &&
                verifiedNode.ValueKind is JsonValueKind.True or JsonValueKind.False)
                verified = verifiedNode.GetBoolean();

            if (commit.TryGetProperty("committer", out var committer) &&
                committer.TryGetProperty("date", out var dateNode) &&
                dateNode.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(dateNode.GetString(), out var date))
                published = date;
        }

        if (source.RequireVerifiedCommit && verified is not true)
            throw new InvalidOperationException(
                "GitHub branch head is not a verified commit.");

        return new(
            source.Id,
            source.Name,
            source.Role,
            source.Trust,
            source.Strategy,
            source.Repository,
            $"https://github.com/{source.Repository}/commit/{sha}",
            source.Critical,
            Success: !string.IsNullOrWhiteSpace(sha),
            Live: true,
            FromCache: false,
            Version: sha is null ? null : sha[..Math.Min(8, sha.Length)],
            ResolvedRef: sha,
            PublishedAt: published,
            VerifiedCommit: verified,
            CheckedAt: DateTimeOffset.UtcNow,
            Error: null);
    }

    private async Task<OnlineSourceResolution> ResolveWebPageAsync(
        OnlineSourceDefinition source,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                "Web source must use an absolute HTTPS URL.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("Source returned HTTP 404.");

        response.EnsureSuccessStatusCode();

        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                "Web source redirected outside HTTPS.");

        return new(
            source.Id,
            source.Name,
            source.Role,
            source.Trust,
            source.Strategy,
            source.Repository,
            finalUri.ToString(),
            source.Critical,
            Success: true,
            Live: true,
            FromCache: false,
            Version: response.Headers.ETag?.Tag,
            ResolvedRef: null,
            PublishedAt: response.Content.Headers.LastModified,
            VerifiedCommit: null,
            CheckedAt: DateTimeOffset.UtcNow,
            Error: null);
    }

    private async Task WriteResolutionCacheAsync(
        OnlineSourceResolution result,
        CancellationToken cancellationToken)
    {
        var path = CachePath(result.Id);
        var json = JsonSerializer.Serialize(result, JsonOptions);

        await File.WriteAllTextAsync(
            path,
            json,
            new UTF8Encoding(false),
            cancellationToken);
    }

    private async Task<OnlineSourceResolution?> TryReadResolutionCacheAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var path = CachePath(sourceId);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(
                path,
                cancellationToken);

            return JsonSerializer.Deserialize<OnlineSourceResolution>(
                json,
                JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private string CachePath(string sourceId)
    {
        var safe = string.Concat(
            sourceId.Select(ch =>
                char.IsLetterOrDigit(ch) || ch is '-' or '_'
                    ? ch
                    : '-'));

        return Path.Combine(
            _cacheDirectory,
            safe + ".json");
    }

    private static void ValidateExecutionCriticalDefinitions(
        OnlineSourceCatalogDocument catalog)
    {
        var source = catalog.Sources.SingleOrDefault(x =>
            x.Id.Equals(
                "macos.opcore-simplify",
                StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                "Execution-critical OpCore Simplify source is missing.");

        if (!source.Repository?.Equals(
                "lzhoang2801/OpCore-Simplify",
                StringComparison.OrdinalIgnoreCase) ?? true ||
            !source.Branch?.Equals(
                "main",
                StringComparison.OrdinalIgnoreCase) ?? true ||
            source.Strategy != "githubBranchHead" ||
            !source.RequireVerifiedCommit ||
            !source.Critical)
            throw new InvalidDataException(
                "Execution-critical OpCore Simplify source definition was modified outside the trusted policy.");
    }

    private static OnlineSourceCatalogDocument ParseCatalog(string json)
    {
        var document = JsonSerializer.Deserialize<OnlineSourceCatalogDocument>(
            json,
            JsonOptions)
            ?? throw new InvalidDataException(
                "Online source catalog is empty.");

        if (document.SchemaVersion != 1)
            throw new InvalidDataException(
                $"Unsupported online source catalog schema {document.SchemaVersion}.");

        var duplicate = document.Sources
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicate is not null)
            throw new InvalidDataException(
                $"Duplicate source id '{duplicate.Key}'.");

        foreach (var source in document.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id) ||
                string.IsNullOrWhiteSpace(source.Name) ||
                source.Systems.Count == 0)
                throw new InvalidDataException(
                    "Online source catalog contains an incomplete source.");

            if (source.Strategy is "githubRelease" or "githubBranchHead")
            {
                if (string.IsNullOrWhiteSpace(source.Repository) ||
                    source.Repository.Split('/').Length != 2 ||
                    source.Repository.Any(char.IsWhiteSpace))
                    throw new InvalidDataException(
                        $"Source '{source.Id}' requires a simple owner/repository GitHub identifier.");
            }
            else if (source.Strategy == "webPage")
            {
                if (!Uri.TryCreate(
                        source.Url,
                        UriKind.Absolute,
                        out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException(
                        $"Source '{source.Id}' requires an HTTPS URL.");
            }
            else
            {
                throw new InvalidDataException(
                    $"Source '{source.Id}' uses unsupported strategy '{source.Strategy}'.");
            }

            if (source.Trust is not ("official" or "upstream" or "community" or "experimental"))
                throw new InvalidDataException(
                    $"Source '{source.Id}' uses unsupported trust class '{source.Trust}'.");
        }

        return document;
    }
}
