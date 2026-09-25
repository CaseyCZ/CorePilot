using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CorePilot.Core;
using CorePilot.MacOS;

namespace CorePilot.App;

public sealed record SupportBundleContext(
    MacOSWorkflowSnapshot Workflow,
    string SelectedSystem,
    string SelectedVariant,
    HardwareReport? Hardware,
    CompatibilityReport? Compatibility,
    MacOSAutomationProfile? AutomationProfile,
    UsbTargetSafetyReport? UsbSafety,
    OpCoreStagingResult? Workspace,
    OnlineSourceSnapshot? OnlineSources);

public sealed record SupportBundleResult(
    string BundlePath,
    int FileCount,
    long SizeBytes);

public sealed class SupportBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string[] WorkspaceDiagnosticFiles =
    [
        "CorePilotWorkspace.json",
        "CorePilotAutomationProfile.json",
        "CorePilotEfiBuild.json",
        "CorePilotDownloadedComponents.json",
        "CorePilotDownloadedComponents.sha256",
        "CorePilotRecovery.json",
        "CorePilotInstallerManifest.json",
        "CorePilotInstallerManifest.sha256",
        "CorePilotUsbWritePlan.json",
        "CorePilotUsbWritePlan.sha256",
        "CorePilotUsbExecutionPreflight.json",
        "CorePilotUsbExecutionPreflight.sha256",
        "CorePilotUsbTypedConfirmation.json",
        "CorePilotUsbTypedConfirmation.sha256",
        "CorePilotUsbSimulationTranscript.json",
        "CorePilotUsbSimulationTranscript.sha256",
        "CorePilotUsbPhysicalWrite.json",
        "CorePilotUsbPhysicalWrite.sha256"
    ];

    public async Task<SupportBundleResult> CreateAsync(
        ActivityLogService activityLog,
        SupportBundleContext context,
        CancellationToken cancellationToken = default)
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        var supportDirectory = Path.Combine(
            localAppData,
            "CorePilot",
            "support");

        Directory.CreateDirectory(supportDirectory);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var bundlePath = Path.Combine(
            supportDirectory,
            $"CorePilot-Support-{stamp}.zip");

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"CorePilot-Support-{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempRoot);

        try
        {
            await WriteSummaryAsync(tempRoot, activityLog, context, cancellationToken);
            await WriteHardwareAsync(tempRoot, context.Hardware, cancellationToken);
            await WriteJsonAsync(
                tempRoot,
                "compatibility.json",
                context.Compatibility,
                cancellationToken);
            await WriteJsonAsync(
                tempRoot,
                "automation-profile.json",
                context.AutomationProfile,
                cancellationToken);
            await WriteUsbSafetyAsync(
                tempRoot,
                context.UsbSafety,
                cancellationToken);
            await WriteJsonAsync(
                tempRoot,
                "online-sources.json",
                context.OnlineSources,
                cancellationToken);
            await CopySanitizedSessionLogAsync(
                tempRoot,
                activityLog.LogFilePath,
                cancellationToken);
            await CopyWorkspaceDiagnosticsAsync(
                tempRoot,
                context.Workspace,
                cancellationToken);

            await File.WriteAllTextAsync(
                Path.Combine(tempRoot, "README.txt"),
                BuildReadme(),
                Encoding.UTF8,
                cancellationToken);

            var fileCount = Directory
                .EnumerateFiles(tempRoot, "*", SearchOption.AllDirectories)
                .Count();

            await Task.Run(
                () => ZipFile.CreateFromDirectory(
                    tempRoot,
                    bundlePath,
                    CompressionLevel.Optimal,
                    includeBaseDirectory: false),
                cancellationToken);

            return new(
                bundlePath,
                fileCount,
                new FileInfo(bundlePath).Length);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // A support bundle failure must never damage the active CorePilot session.
            }
        }
    }

    private static async Task WriteSummaryAsync(
        string root,
        ActivityLogService activityLog,
        SupportBundleContext context,
        CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        var summary = new
        {
            schemaVersion = 1,
            createdAt = DateTimeOffset.Now,
            corePilot = new
            {
                informationalVersion,
                physicalDiskWritesEnabled = true
            },
            runtime = new
            {
                os = Environment.OSVersion.VersionString,
                dotnet = Environment.Version.ToString(),
                is64BitOperatingSystem = Environment.Is64BitOperatingSystem,
                is64BitProcess = Environment.Is64BitProcess
            },
            selection = new
            {
                system = context.SelectedSystem,
                variant = context.SelectedVariant
            },
            workflow = context.Workflow,
            activity = new
            {
                area = activityLog.CurrentArea,
                status = activityLog.CurrentStatus,
                errors = activityLog.ErrorCount
            },
            redaction = new
            {
                computerName = true,
                userName = true,
                localPaths = true,
                serialNumbers = true,
                targetIdentityFingerprint = true,
                confirmationPhrases = true
            }
        };

        await WriteJsonAsync(
            root,
            "support-summary.json",
            summary,
            cancellationToken);
    }

    private static async Task WriteHardwareAsync(
        string root,
        HardwareReport? hardware,
        CancellationToken cancellationToken)
    {
        if (hardware is null)
            return;

        var sanitized = hardware with
        {
            ComputerName = "<redacted>"
        };

        await WriteJsonAsync(
            root,
            "hardware.json",
            sanitized,
            cancellationToken);
    }

    private static async Task WriteUsbSafetyAsync(
        string root,
        UsbTargetSafetyReport? usbSafety,
        CancellationToken cancellationToken)
    {
        if (usbSafety is null)
            return;

        var sanitized = usbSafety with
        {
            SerialNumber = "<redacted>",
            IdentityFingerprint = "<redacted>"
        };

        await WriteJsonAsync(
            root,
            "usb-safety.json",
            sanitized,
            cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(
        string root,
        string name,
        T value,
        CancellationToken cancellationToken)
    {
        if (value is null)
            return;

        var raw = JsonSerializer.Serialize(value, JsonOptions);
        var sanitized = SanitizeJson(raw);

        await File.WriteAllTextAsync(
            Path.Combine(root, name),
            sanitized,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    private static async Task CopySanitizedSessionLogAsync(
        string root,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
            return;

        var text = await File.ReadAllTextAsync(
            sourcePath,
            cancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(root, "session.log"),
            RedactText(text),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    private static async Task CopyWorkspaceDiagnosticsAsync(
        string root,
        OpCoreStagingResult? workspace,
        CancellationToken cancellationToken)
    {
        if (workspace is null ||
            !Directory.Exists(workspace.WorkspaceDirectory))
            return;

        var destination = Path.Combine(root, "workspace");
        Directory.CreateDirectory(destination);

        foreach (var fileName in WorkspaceDiagnosticFiles)
        {
            var source = Path.Combine(
                workspace.WorkspaceDirectory,
                fileName);

            if (!File.Exists(source))
                continue;

            var text = await File.ReadAllTextAsync(
                source,
                cancellationToken);

            if (fileName.EndsWith(
                    ".json",
                    StringComparison.OrdinalIgnoreCase))
            {
                text = SanitizeJson(text);
            }
            else
            {
                text = RedactText(text);
            }

            await File.WriteAllTextAsync(
                Path.Combine(destination, fileName),
                text,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
        }
    }

    private static string SanitizeJson(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is null)
                return RedactText(json);

            var sanitized = SanitizeNode(node, null);
            return sanitized?.ToJsonString(JsonOptions)
                   ?? "{}";
        }
        catch
        {
            return RedactText(json);
        }
    }

    private static JsonNode? SanitizeNode(
        JsonNode? node,
        string? propertyName)
    {
        if (node is null)
            return null;

        if (propertyName is not null &&
            IsSensitiveProperty(propertyName))
            return JsonValue.Create("<redacted>");

        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(x => x.Key).ToArray())
            {
                var child = obj[key];

                if (IsSensitiveProperty(key))
                {
                    obj[key] = "<redacted>";
                    continue;
                }

                if (child is JsonValue value &&
                    value.TryGetValue<string>(out var text))
                {
                    obj[key] = RedactText(text);
                    continue;
                }

                SanitizeNode(child, key);
            }

            return obj;
        }

        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var child = array[i];

                if (child is JsonValue value &&
                    value.TryGetValue<string>(out var text))
                {
                    array[i] = RedactText(text);
                    continue;
                }

                SanitizeNode(child, null);
            }

            return array;
        }

        return node;
    }

    private static bool IsSensitiveProperty(string name) =>
        name.Contains("serial", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ComputerName", StringComparison.OrdinalIgnoreCase)
        || name.Contains("IdentityFingerprint", StringComparison.OrdinalIgnoreCase)
        || name.Contains("ConfirmationPhrase", StringComparison.OrdinalIgnoreCase)
        || name.Equals("AcceptedPhrase", StringComparison.OrdinalIgnoreCase)
        || name.Equals("RequiredConfirmationPhrase", StringComparison.OrdinalIgnoreCase)
        || name.Equals("FutureConfirmationPhrase", StringComparison.OrdinalIgnoreCase);

    private static string RedactText(string text)
    {
        var result = text;

        var replacements = new[]
        {
            (
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "<USERPROFILE>"),
            (
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "<LOCALAPPDATA>"),
            (
                Environment.MachineName,
                "<MACHINE>"),
            (
                Environment.UserName,
                "<USER>")
        };

        foreach (var (source, replacement) in replacements)
        {
            if (string.IsNullOrWhiteSpace(source))
                continue;

            result = result.Replace(
                source,
                replacement,
                StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static string BuildReadme() =>
        """
        CorePilot Support Bundle

        This archive was generated locally by CorePilot for troubleshooting.

        Included data can contain:
        - the current CorePilot workflow snapshot,
        - sanitized hardware and compatibility data,
        - the current session log,
        - CorePilot-generated workspace manifests and validation metadata.

        CorePilot does NOT copy user documents or arbitrary files into this bundle.

        Automatic redaction removes:
        - computer/user names where detected,
        - local user/profile paths,
        - USB serial numbers,
        - USB target identity fingerprints,
        - destructive confirmation phrases.

        Physical disk writing is enabled only through CorePilot's guarded macOS workflow after live source checks, target re-inspection, manifest verification, short-lived preflight and exact typed confirmation.
        """;
}
