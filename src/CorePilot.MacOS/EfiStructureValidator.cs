using System.Xml.Linq;

namespace CorePilot.MacOS;

public sealed record EfiStructureAuditResult(
    bool Success,
    int CheckedEntries,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed class EfiStructureValidator
{
    public EfiStructureAuditResult Validate(string efiDirectory, string configPath)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var checkedEntries = 0;

        var efiRoot = Path.GetFullPath(efiDirectory);
        var ocRoot = Path.Combine(efiRoot, "OC");

        CheckFile(Path.Combine(efiRoot, "BOOT", "BOOTx64.efi"), "BOOTx64.efi", errors, ref checkedEntries);
        CheckFile(Path.Combine(ocRoot, "OpenCore.efi"), "OpenCore.efi", errors, ref checkedEntries);
        CheckFile(configPath, "config.plist", errors, ref checkedEntries);

        if (errors.Count > 0)
            return new(false, checkedEntries, errors, warnings);

        XDocument document;
        try
        {
            document = XDocument.Load(configPath, LoadOptions.None);
        }
        catch (Exception ex)
        {
            errors.Add($"config.plist is not valid XML: {ex.Message}");
            return new(false, checkedEntries, errors, warnings);
        }

        var rootDict = document.Root?
            .Elements()
            .FirstOrDefault(x => x.Name.LocalName == "dict");

        if (rootDict is null)
        {
            errors.Add("config.plist does not contain a root dict.");
            return new(false, checkedEntries, errors, warnings);
        }

        var root = ReadDict(rootDict);

        AuditAcpi(root, ocRoot, errors, warnings, ref checkedEntries);
        AuditKernel(root, ocRoot, errors, warnings, ref checkedEntries);
        AuditDrivers(root, ocRoot, errors, warnings, ref checkedEntries);
        AuditTools(root, ocRoot, errors, warnings, ref checkedEntries);

        return new(errors.Count == 0, checkedEntries, errors, warnings);
    }

    private static void AuditAcpi(
        IReadOnlyDictionary<string, XElement> root,
        string ocRoot,
        ICollection<string> errors,
        ICollection<string> warnings,
        ref int checkedEntries)
    {
        foreach (var entry in GetArray(root, "ACPI", "Add"))
        {
            if (entry.Name.LocalName != "dict")
                continue;

            var values = ReadDict(entry);
            if (!IsEnabled(values))
                continue;

            var path = GetString(values, "Path");
            if (string.IsNullOrWhiteSpace(path))
            {
                errors.Add("ACPI/Add contains an enabled entry without Path.");
                continue;
            }

            CheckReferencedFile(
                Path.Combine(ocRoot, "ACPI"),
                path,
                $"ACPI/Add {path}",
                errors,
                ref checkedEntries);
        }
    }

    private static void AuditKernel(
        IReadOnlyDictionary<string, XElement> root,
        string ocRoot,
        ICollection<string> errors,
        ICollection<string> warnings,
        ref int checkedEntries)
    {
        var seenBundles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in GetArray(root, "Kernel", "Add"))
        {
            if (entry.Name.LocalName != "dict")
                continue;

            var values = ReadDict(entry);
            if (!IsEnabled(values))
                continue;

            var bundlePath = GetString(values, "BundlePath");
            if (string.IsNullOrWhiteSpace(bundlePath))
            {
                errors.Add("Kernel/Add contains an enabled entry without BundlePath.");
                continue;
            }

            if (!seenBundles.Add(bundlePath))
                warnings.Add($"Kernel/Add contains duplicate enabled BundlePath: {bundlePath}");

            var bundleFull = ResolveSafePath(
                Path.Combine(ocRoot, "Kexts"),
                bundlePath,
                $"Kernel/Add {bundlePath}",
                errors);

            if (bundleFull is null)
                continue;

            checkedEntries++;
            if (!Directory.Exists(bundleFull))
            {
                errors.Add($"Kernel/Add bundle is missing: {bundlePath}");
                continue;
            }

            var plistPath = GetString(values, "PlistPath");
            if (!string.IsNullOrWhiteSpace(plistPath))
                CheckReferencedFile(
                    bundleFull,
                    plistPath,
                    $"Kernel/Add {bundlePath} PlistPath",
                    errors,
                    ref checkedEntries);

            var executablePath = GetString(values, "ExecutablePath");
            if (!string.IsNullOrWhiteSpace(executablePath))
                CheckReferencedFile(
                    bundleFull,
                    executablePath,
                    $"Kernel/Add {bundlePath} ExecutablePath",
                    errors,
                    ref checkedEntries);
        }
    }

    private static void AuditDrivers(
        IReadOnlyDictionary<string, XElement> root,
        string ocRoot,
        ICollection<string> errors,
        ICollection<string> warnings,
        ref int checkedEntries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in GetArray(root, "UEFI", "Drivers"))
        {
            string path;

            if (entry.Name.LocalName == "string")
            {
                path = entry.Value.Trim();
            }
            else if (entry.Name.LocalName == "dict")
            {
                var values = ReadDict(entry);
                if (!IsEnabled(values))
                    continue;

                path = GetString(values, "Path");
            }
            else
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                errors.Add("UEFI/Drivers contains an enabled entry without Path.");
                continue;
            }

            if (!seen.Add(path))
                warnings.Add($"UEFI/Drivers contains duplicate enabled driver: {path}");

            CheckReferencedFile(
                Path.Combine(ocRoot, "Drivers"),
                path,
                $"UEFI/Drivers {path}",
                errors,
                ref checkedEntries);
        }
    }

    private static void AuditTools(
        IReadOnlyDictionary<string, XElement> root,
        string ocRoot,
        ICollection<string> errors,
        ICollection<string> warnings,
        ref int checkedEntries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in GetArray(root, "Misc", "Tools"))
        {
            if (entry.Name.LocalName != "dict")
                continue;

            var values = ReadDict(entry);
            if (!IsEnabled(values))
                continue;

            var path = GetString(values, "Path");
            if (string.IsNullOrWhiteSpace(path))
            {
                errors.Add("Misc/Tools contains an enabled entry without Path.");
                continue;
            }

            if (!seen.Add(path))
                warnings.Add($"Misc/Tools contains duplicate enabled tool: {path}");

            CheckReferencedFile(
                Path.Combine(ocRoot, "Tools"),
                path,
                $"Misc/Tools {path}",
                errors,
                ref checkedEntries);
        }
    }

    private static IEnumerable<XElement> GetArray(
        IReadOnlyDictionary<string, XElement> root,
        string section,
        string key)
    {
        if (!root.TryGetValue(section, out var sectionElement) ||
            sectionElement.Name.LocalName != "dict")
            return [];

        var sectionDict = ReadDict(sectionElement);
        if (!sectionDict.TryGetValue(key, out var array) ||
            array.Name.LocalName != "array")
            return [];

        return array.Elements();
    }

    private static Dictionary<string, XElement> ReadDict(XElement dict)
    {
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var elements = dict.Elements().ToList();

        for (var i = 0; i < elements.Count - 1; i++)
        {
            if (elements[i].Name.LocalName != "key")
                continue;

            var key = elements[i].Value;
            result[key] = elements[i + 1];
            i++;
        }

        return result;
    }

    private static bool IsEnabled(IReadOnlyDictionary<string, XElement> values)
    {
        if (!values.TryGetValue("Enabled", out var enabled))
            return true;

        return enabled.Name.LocalName switch
        {
            "true" => true,
            "false" => false,
            "integer" => enabled.Value.Trim() != "0",
            _ => bool.TryParse(enabled.Value.Trim(), out var value) && value
        };
    }

    private static string GetString(
        IReadOnlyDictionary<string, XElement> values,
        string key)
    {
        if (!values.TryGetValue(key, out var element))
            return "";

        return element.Value.Trim();
    }

    private static void CheckFile(
        string path,
        string label,
        ICollection<string> errors,
        ref int checkedEntries)
    {
        checkedEntries++;
        if (!File.Exists(path))
            errors.Add($"Required EFI file is missing: {label}");
    }

    private static void CheckReferencedFile(
        string root,
        string relativePath,
        string label,
        ICollection<string> errors,
        ref int checkedEntries)
    {
        var full = ResolveSafePath(root, relativePath, label, errors);
        if (full is null)
            return;

        checkedEntries++;
        if (!File.Exists(full))
            errors.Add($"{label} points to a missing file: {relativePath}");
    }

    private static string? ResolveSafePath(
        string root,
        string relativePath,
        string label,
        ICollection<string> errors)
    {
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var normalizedRelative = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        var full = Path.GetFullPath(Path.Combine(rootFull, normalizedRelative));

        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{label} contains an unsafe path: {relativePath}");
            return null;
        }

        return full;
    }
}
