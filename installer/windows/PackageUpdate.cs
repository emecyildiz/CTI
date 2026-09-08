using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CtiInstaller;

// Shared by the GUI and headless regression tests. Planning never writes files.
internal static class PackageUpdate
{
    internal const string ManifestName = ".cti-package-files.json";
    internal const string StateName = ".cti-update-state.json";
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { ".env", ManifestName, StateName, ".cti-owner.json", ".cti-installation.json" };

    internal sealed record Entry(string path, string sha256);
    internal sealed record Item(string Relative, string SourceHash, string? ExistingHash);
    internal sealed record Plan(string Source, string Destination, string TargetVersion, string? InstalledVersion,
        string Action, List<Item> Items, List<string> Conflicts, Dictionary<string, string> Previous,
        Dictionary<string, string?> Metadata);
    internal sealed record Result(string? BackupDirectory);

    internal static int CompareVersions(string left, string right)
    {
        static (int Major, int Minor, int Patch, int? Rc) Parse(string value)
        {
            var m = Regex.Match(value, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-rc\.([1-9][0-9]*))?\z");
            if (!m.Success) throw new InvalidDataException("Unsupported CTI version. Refusing to guess upgrade order.");
            return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : null);
        }
        var a = Parse(left); var b = Parse(right);
        var core = a.Major.CompareTo(b.Major);
        if (core == 0) core = a.Minor.CompareTo(b.Minor);
        if (core == 0) core = a.Patch.CompareTo(b.Patch);
        if (core != 0) return core;
        if (a.Rc is null) return b.Rc is null ? 0 : 1;
        return b.Rc is null ? -1 : a.Rc.Value.CompareTo(b.Rc.Value);
    }

    internal static void AssertSafePath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Installation paths must not traverse a junction or symbolic link.");
        }
    }

    private static string Within(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split('/', '\\').Any(p => p is ".." or "." or ""))
            throw new InvalidDataException("Unsafe package or manifest path.");
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package path escaped its root.");
        AssertSafePath(full);
        return full;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string? FileHash(string path)
    {
        AssertSafePath(path);
        if (Directory.Exists(path)) throw new InvalidDataException("A directory conflicts with a package file.");
        return File.Exists(path) ? Hash(path) : null;
    }

    internal static string? InstalledVersion(string destination)
    {
        AssertSafePath(destination);
        string? highest = null;
        void Include(string? value)
        {
            if (value is null) throw new InvalidDataException("Missing installed version metadata.");
            _ = CompareVersions(value, value);
            if (highest is null || CompareVersions(value, highest) > 0) highest = value;
        }
        var version = Within(destination, "VERSION");
        if (File.Exists(version)) Include(File.ReadAllText(version).Trim());
        foreach (var name in new[] { ".cti-installation.json", StateName })
        {
            var path = Within(destination, name);
            if (!File.Exists(path)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.GetProperty("product").GetString() != "CTI Self-Hosted")
                throw new InvalidDataException("Unrecognized installation metadata.");
            Include(doc.RootElement.GetProperty("version").GetString());
        }
        return highest;
    }

    internal static string Describe(string destination, string targetVersion)
    {
        _ = CompareVersions(targetVersion, targetVersion);
        var installed = InstalledVersion(destination);
        if (installed is null) return "Install";
        var order = CompareVersions(targetVersion, installed);
        if (order < 0) throw new InvalidOperationException($"Downgrade blocked: installed {installed}, installer {targetVersion}. Use an equal or newer installer.");
        return order == 0 ? "Repair" : "Update";
    }

    internal static Plan Prepare(string source, string destination, string targetVersion)
    {
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        AssertSafePath(source); AssertSafePath(destination);
        if (Path.TrimEndingDirectorySeparator(destination) == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(destination)!))
            throw new InvalidOperationException("A drive root cannot be an installation directory.");
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(Path.TrimEndingDirectorySeparator(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Package source and destination must be separate.");
        var action = Describe(destination, targetVersion); // Before any file or Docker mutation.
        if (File.ReadAllText(Within(source, "VERSION")).Trim() != targetVersion)
            throw new InvalidDataException("Installer and embedded package versions differ.");
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any() &&
            !File.Exists(Within(destination, StateName)) && !File.Exists(Within(destination, ".cti-installation.json")) &&
            !new[] { "VERSION", "setup.ps1", "compose.yml" }.All(f => File.Exists(Within(destination, f))))
            throw new InvalidOperationException("Non-empty destination is not a recognized CTI installation.");

        var metadata = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { ManifestName, StateName, ".cti-installation.json", ".cti-owner.json", ".env" })
            metadata[name] = FileHash(Within(destination, name));
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (metadata[ManifestName] is not null)
        {
            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(Within(destination, ManifestName)))
                ?? throw new InvalidDataException("Invalid package manifest.");
            foreach (var entry in entries)
            {
                _ = Within(destination, entry.path);
                if (entry.sha256 is null || Reserved.Contains(entry.path) || !Regex.IsMatch(entry.sha256, @"\A[0-9A-Fa-f]{64}\z") || !previous.TryAdd(entry.path, entry.sha256))
                    throw new InvalidDataException("Invalid or duplicate package manifest entry.");
            }
        }
        var items = new List<Item>(); var conflicts = new List<string>();
        void Walk(string directory)
        {
            AssertSafePath(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                AssertSafePath(path);
                if (Directory.Exists(path)) { Walk(path); continue; }
                var relative = Path.GetRelativePath(source, path);
                if (Reserved.Contains(relative)) throw new InvalidDataException("Package contains private installation metadata.");
                var target = Within(destination, relative);
                var incoming = Hash(path); var current = FileHash(target);
                if (current is not null && current != incoming &&
                    (!previous.TryGetValue(relative, out var expected) || !current.Equals(expected, StringComparison.OrdinalIgnoreCase)))
                    conflicts.Add(relative);
                items.Add(new Item(relative, incoming, current));
            }
        }
        Walk(source);
        // Check every parent and backup path before beginning any overwrite.
        foreach (var item in items)
            for (var parent = Path.GetDirectoryName(Within(destination, item.Relative)); parent != destination && parent is not null; parent = Path.GetDirectoryName(parent))
                if (File.Exists(parent)) throw new InvalidDataException("A file conflicts with a package directory.");
        var backups = Within(destination, "backups");
        if (File.Exists(backups)) throw new InvalidDataException("Backup directory is blocked by a file.");
        return new Plan(source, destination, targetVersion, InstalledVersion(destination), action, items, conflicts, previous, metadata);
    }

    internal static Result Apply(Plan plan, bool allowConflictBackup)
    {
        // Fresh complete preflight catches edits while the confirmation dialog was open.
        var fresh = Prepare(plan.Source, plan.Destination, plan.TargetVersion);
        if (fresh.Action != plan.Action || fresh.InstalledVersion != plan.InstalledVersion ||
            !fresh.Items.OrderBy(i => i.Relative).SequenceEqual(plan.Items.OrderBy(i => i.Relative)) ||
            fresh.Metadata.Any(kv => !plan.Metadata.TryGetValue(kv.Key, out var hash) || hash != kv.Value))
            throw new InvalidOperationException("Installation files changed after review. Review the operation again.");
        if (fresh.Conflicts.Count > 0 && !allowConflictBackup)
            throw new InvalidOperationException("Modified or untracked package files found. No files were overwritten.");

        string? backup = null;
        if (fresh.InstalledVersion is not null)
        {
            backup = Within(fresh.Destination, $"backups/installer-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(backup);
            // Save all files that will be replaced, not just detected local edits.
            foreach (var item in fresh.Items.Where(i => i.ExistingHash is not null))
            {
                var target = Within(backup, item.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Within(fresh.Destination, item.Relative), target, false);
                if (Hash(target) != item.ExistingHash) throw new IOException("Backup verification failed; installation was not overwritten.");
            }
            foreach (var name in new[] { ManifestName, ".cti-installation.json", StateName })
                if (fresh.Metadata[name] is not null) File.Copy(Within(fresh.Destination, name), Within(backup, name), false);
        }
        Directory.CreateDirectory(fresh.Destination);
        WriteJson(Within(fresh.Destination, StateName), new { product = "CTI Self-Hosted", version = fresh.TargetVersion,
            phase = "files-and-setup-pending", backup, started_at_utc = DateTimeOffset.UtcNow });
        // Keep the high-water version receipt until setup finishes. A failed update is not a rollback.
        foreach (var item in fresh.Items.OrderBy(i => i.Relative == "VERSION" ? 1 : 0))
        {
            var source = Within(fresh.Source, item.Relative); var target = Within(fresh.Destination, item.Relative);
            if (Hash(source) != item.SourceHash || FileHash(target) != item.ExistingHash)
                throw new IOException($"File changed during update: {item.Relative}. Retained backup: {backup}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, true);
        }
        var manifest = new Dictionary<string, string>(fresh.Previous, StringComparer.OrdinalIgnoreCase);
        foreach (var item in fresh.Items) manifest[item.Relative] = item.SourceHash;
        WriteJson(Within(fresh.Destination, ManifestName), manifest.Select(kv => new Entry(kv.Key, kv.Value)).ToList());
        return new Result(backup);
    }

    internal static void Complete(string destination, string version)
    {
        if (InstalledVersion(destination) != version) throw new InvalidDataException("Installation version changed during setup.");
        WriteJson(Within(destination, ".cti-installation.json"), new { product = "CTI Self-Hosted", version,
            installed_at_utc = DateTimeOffset.UtcNow, installer = "windows-gui" });
        File.Delete(Within(destination, StateName));
    }

    private static void WriteJson<T>(string path, T value)
    {
        AssertSafePath(path);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
