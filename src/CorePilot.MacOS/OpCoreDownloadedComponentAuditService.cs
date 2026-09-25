using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CorePilot.Core;

namespace CorePilot.MacOS;

public sealed record DownloadedComponentAuditEntry(
    string ProductName,
    string Id,
    string Url,
    string Sha256,
    bool FolderPresent,
    bool FolderManifestPresent,
    string? CatalogSourceId = null,
    string? CatalogVersion = null,
    bool? CatalogAligned = null);

public sealed record DownloadedComponentAuditResult(
    string AuditPath,
    string AuditSha256,
    string HistoryPath,
    string HistorySha256,
    int ComponentCount,
    IReadOnlyList<DownloadedComponentAuditEntry> Components);

public sealed class OpCoreDownloadedComponentAuditService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public Task<DownloadedComponentAuditResult> AuditAsync(
        OpCoreStagingResult stage,
        CancellationToken cancellationToken = default) =>
        AuditAsync(stage, null, cancellationToken);

    public async Task<DownloadedComponentAuditResult> AuditAsync(
        OpCoreStagingResult stage,
        OnlineSourceSnapshot? sourceSnapshot,
        CancellationToken cancellationToken = default)
    {
        var ockRoot = Path.Combine(stage.UpstreamDirectory, "OCK_Files");
        var historyPath = Path.Combine(ockRoot, "history.json");

        if (!Directory.Exists(ockRoot) || !File.Exists(historyPath))
            throw new InvalidOperationException(
                "OpCore Simplify component download history is missing.");

        var json = await File.ReadAllTextAsync(historyPath, cancellationToken);
        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(
                "OpCore Simplify component history is not an array.");

        var entries = new List<DownloadedComponentAuditEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in document.RootElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = ReadString(item, "product_name");
            var id = ReadFlexibleId(item);
            var url = ReadString(item, "url");
            var sha256 = ReadString(item, "sha256");

            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException(
                    "Downloaded component history contains an unnamed product.");

            if (!names.Add(name))
                throw new InvalidDataException(
                    $"Downloaded component history contains duplicate product '{name}'.");

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException(
                    $"Downloaded component '{name}' does not have an HTTPS source URL.");

            if (!IsSha256(sha256))
                throw new InvalidDataException(
                    $"Downloaded component '{name}' does not have a valid SHA-256 source digest.");

            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidDataException(
                    $"Downloaded component '{name}' does not have a stable release/build id.");

            var folder = Path.Combine(ockRoot, name);
            var folderPresent = Directory.Exists(folder);
            var folderManifestPath = Path.Combine(folder, "manifest.json");
            var manifestPresent = folderPresent &&
                                  File.Exists(folderManifestPath);

            if (!folderPresent || !manifestPresent)
                throw new InvalidDataException(
                    $"Downloaded component '{name}' is missing its integrity-manifested cache folder.");

            await VerifyFolderManifestAsync(
                folder,
                folderManifestPath,
                cancellationToken);

            var catalogMatch = ResolveCatalogMatch(
                name,
                uri,
                sourceSnapshot);

            entries.Add(new(
                name,
                id,
                uri.ToString(),
                sha256.ToUpperInvariant(),
                folderPresent,
                manifestPresent,
                catalogMatch.SourceId,
                catalogMatch.Version,
                catalogMatch.Aligned));
        }

        if (entries.Count == 0)
            throw new InvalidDataException(
                "OpCore Simplify did not record any downloaded components.");

        if (!entries.Any(x =>
                x.ProductName.Equals(
                    "OpenCorePkg",
                    StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(
                "OpenCorePkg is missing from the verified component history.");

        if (sourceSnapshot is not null)
        {
            var mismatches = entries
                .Where(x => x.CatalogAligned is false)
                .Select(x => $"{x.ProductName} != {x.CatalogVersion ?? "current catalog release"}")
                .ToArray();

            if (mismatches.Length > 0)
                throw new InvalidDataException(
                    "Downloaded OpenCore/kext component versions do not match the live CorePilot catalog: " +
                    string.Join("; ", mismatches));

            var openCore = entries.Single(x =>
                x.ProductName.Equals(
                    "OpenCorePkg",
                    StringComparison.OrdinalIgnoreCase));

            if (openCore.CatalogAligned is not true)
                throw new InvalidDataException(
                    "OpenCorePkg could not be bound to the current live CorePilot source catalog.");
        }

        foreach (var directory in Directory.EnumerateDirectories(ockRoot))
        {
            var product = Path.GetFileName(directory);
            if (!names.Contains(product))
                throw new InvalidDataException(
                    $"Component cache folder '{product}' has no matching hashed download-history entry.");
        }

        var historySha = await ComputeSha256Async(
            historyPath,
            cancellationToken);

        var audit = new
        {
            schemaVersion = 1,
            createdAt = DateTimeOffset.UtcNow,
            opCoreSimplifyCommit = stage.UpstreamCommit,
            opCoreSimplifyArchiveSha256 = stage.ArchiveSha256,
            historyPath,
            historySha256 = historySha,
            componentCount = entries.Count,
            components = entries
        };

        var auditPath = Path.Combine(
            stage.WorkspaceDirectory,
            "CorePilotDownloadedComponents.json");

        await File.WriteAllTextAsync(
            auditPath,
            JsonSerializer.Serialize(audit, JsonOptions),
            new UTF8Encoding(false),
            cancellationToken);

        var auditSha = await ComputeSha256Async(
            auditPath,
            cancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(
                stage.WorkspaceDirectory,
                "CorePilotDownloadedComponents.sha256"),
            $"{auditSha}  {Path.GetFileName(auditPath)}{Environment.NewLine}",
            Encoding.ASCII,
            cancellationToken);

        return new(
            auditPath,
            auditSha,
            historyPath,
            historySha,
            entries.Count,
            entries);
    }

    private static async Task VerifyFolderManifestAsync(
        string folder,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var manifestJson = await File.ReadAllTextAsync(
            manifestPath,
            cancellationToken);

        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(
            manifestJson,
            JsonOptions)
            ?? throw new InvalidDataException(
                $"Component integrity manifest is invalid: {manifestPath}");

        var expected = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var pair in manifest)
        {
            var relative = pair.Key.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative) ||
                relative.Split(
                        Path.DirectorySeparatorChar,
                        StringSplitOptions.RemoveEmptyEntries)
                    .Any(x => x == ".."))
                throw new InvalidDataException(
                    $"Component integrity manifest contains an unsafe path: {pair.Key}");

            if (!IsSha256(pair.Value))
                throw new InvalidDataException(
                    $"Component integrity manifest contains an invalid SHA-256 for {pair.Key}.");

            var fullPath = Path.GetFullPath(Path.Combine(folder, relative));
            var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;

            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Component integrity manifest escapes its cache folder: {pair.Key}");

            expected[NormalizeRelativePath(pair.Key)] = pair.Value;
        }

        var actualFiles = Directory
            .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFullPath(path).Equals(
                Path.GetFullPath(manifestPath),
                StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                FullPath = path,
                Relative = NormalizeRelativePath(
                    Path.GetRelativePath(folder, path))
            })
            .ToArray();

        if (actualFiles.Length != expected.Count)
            throw new InvalidDataException(
                $"Component cache file count does not match its integrity manifest: {folder}");

        foreach (var file in actualFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!expected.TryGetValue(file.Relative, out var expectedHash))
                throw new InvalidDataException(
                    $"Component cache contains an untracked file: {file.Relative}");

            var actualHash = await ComputeSha256Async(
                file.FullPath,
                cancellationToken);

            if (!actualHash.Equals(
                    expectedHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Component cache SHA-256 mismatch: {file.Relative}");
        }
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static readonly IReadOnlyDictionary<string, string> CatalogSourceByProduct =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OpenCorePkg"] = "macos.opencore",
            ["Lilu"] = "macos.kext.lilu",
            ["VirtualSMC"] = "macos.kext.virtualsmc",
            ["WhateverGreen"] = "macos.kext.whatevergreen",
            ["AppleALC"] = "macos.kext.applealc",
            ["IntelMausi"] = "macos.kext.intelmausi",
            ["AirportBrcmFixup"] = "macos.kext.airportbrcmfixup",
            ["NVMeFix"] = "macos.kext.nvmefix",
            ["RestrictEvents"] = "macos.kext.restrictevents"
        };

    private static (string? SourceId, string? Version, bool? Aligned) ResolveCatalogMatch(
        string productName,
        Uri downloadUri,
        OnlineSourceSnapshot? sourceSnapshot)
    {
        if (sourceSnapshot is null ||
            !CatalogSourceByProduct.TryGetValue(productName, out var sourceId))
            return (null, null, null);

        var source = sourceSnapshot.Sources.FirstOrDefault(x =>
            x.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase));

        if (source is null || !source.Live || !source.Success)
            return (sourceId, source?.Version, false);

        var repositoryAligned =
            !string.IsNullOrWhiteSpace(source.Repository) &&
            downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            downloadUri.AbsolutePath.Contains(
                "/" + source.Repository + "/",
                StringComparison.OrdinalIgnoreCase);

        var resolved = source.ResolvedRef ?? source.Version;
        if (string.IsNullOrWhiteSpace(resolved))
            return (sourceId, source.Version, false);

        var tag = resolved.Trim();
        var normalized = tag.TrimStart('v', 'V');

        var versionAligned =
            downloadUri.AbsolutePath.Contains(
                "/download/" + tag + "/",
                StringComparison.OrdinalIgnoreCase) ||
            downloadUri.AbsolutePath.Contains(
                "/download/v" + normalized + "/",
                StringComparison.OrdinalIgnoreCase) ||
            downloadUri.AbsolutePath.Contains(
                "/download/" + normalized + "/",
                StringComparison.OrdinalIgnoreCase);

        return (
            sourceId,
            source.Version ?? source.ResolvedRef,
            repositoryAligned && versionAligned);
    }

    private static string ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var node) &&
        node.ValueKind == JsonValueKind.String
            ? node.GetString()?.Trim() ?? ""
            : "";

    private static string ReadFlexibleId(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var node))
            return "";

        return node.ValueKind switch
        {
            JsonValueKind.String => node.GetString()?.Trim() ?? "",
            JsonValueKind.Number => node.GetRawText(),
            _ => ""
        };
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 &&
        value.All(ch =>
            ch is >= '0' and <= '9' ||
            ch is >= 'a' and <= 'f' ||
            ch is >= 'A' and <= 'F');

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
