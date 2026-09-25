using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CorePilot.MacOS;

public sealed record DownloadedComponentAuditEntry(
    string ProductName,
    string Id,
    string Url,
    string Sha256,
    bool FolderPresent,
    bool FolderManifestPresent);

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

    public async Task<DownloadedComponentAuditResult> AuditAsync(
        OpCoreStagingResult stage,
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
            var manifestPresent = folderPresent &&
                                  File.Exists(Path.Combine(folder, "manifest.json"));

            if (!folderPresent || !manifestPresent)
                throw new InvalidDataException(
                    $"Downloaded component '{name}' is missing its integrity-manifested cache folder.");

            entries.Add(new(
                name,
                id,
                uri.ToString(),
                sha256.ToUpperInvariant(),
                folderPresent,
                manifestPresent));
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
