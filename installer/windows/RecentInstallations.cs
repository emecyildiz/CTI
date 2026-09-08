using System.Text.Json;

namespace CtiInstaller;

internal static class RecentInstallations
{
    internal static string DefaultStore => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Emecworks", "CTI-Installer", "installations.json");

    internal static IReadOnlyList<string> Read(string store)
    {
        try
        {
            PackageUpdate.AssertSafePath(store);
            if (!File.Exists(store) || new FileInfo(store).Length > 32768) return [];
            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(store)) ?? [];
            return paths.Where(p => Path.IsPathFullyQualified(p) && Directory.Exists(p))
                .Where(p => new[] { "VERSION", "compose.yml", "setup.ps1" }.All(f => File.Exists(Path.Combine(p, f))))
                .Where(p => { try { PackageUpdate.AssertSafePath(p); return true; } catch { return false; } })
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        }
        catch { return []; } // A stale registry never authorizes filesystem or Docker mutations.
    }

    internal static void Remember(string store, string destination)
    {
        PackageUpdate.AssertSafePath(store);
        PackageUpdate.AssertSafePath(destination);
        var paths = new[] { Path.GetFullPath(destination) }.Concat(Read(store)).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        var temporary = store + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(paths)); File.Move(temporary, store, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
