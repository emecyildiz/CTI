using System.Text.Json;
using CtiInstaller;

var root = Path.Combine(Path.GetTempPath(), "cti-update-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
void Deny(Action action, string name)
{
    try { action(); } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
    { Check(true, name); return; }
    throw new Exception("FAIL: expected refusal: " + name);
}
void Write(string folder, string name, string text)
{
    var path = Path.Combine(folder, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text);
}
string Package(string name, string version, string content)
{
    var path = Path.Combine(root, name); Directory.CreateDirectory(path);
    Write(path, "VERSION", version); Write(path, "setup.ps1", "# fixture"); Write(path, "compose.yml", "# fixture");
    Write(path, "app/main.txt", content); Write(path, ".env.example", "EXAMPLE=true"); return path;
}
try
{
    Check(PackageUpdate.CompareVersions("0.1.0-rc.10", "0.1.0-rc.9") > 0, "numeric RC ordering");
    Check(PackageUpdate.CompareVersions("0.1.0", "0.1.0-rc.99") > 0, "stable follows prerelease");
    Check(PackageUpdate.CompareVersions("0.2.0-rc.1", "0.1.9") > 0, "core version ordering");
    Deny(() => PackageUpdate.CompareVersions("garbage", "0.1.0"), "unknown version fails closed");
    var old = Package("old", "0.1.0-rc.8", "old");
    var newer = Package("new", "0.1.0-rc.9", "new");
    var dest = Path.Combine(root, "installed");
    var plan = PackageUpdate.Prepare(old, dest, "0.1.0-rc.8");
    Check(plan.Action == "Install" && !Directory.Exists(dest), "fresh planning is read-only");
    PackageUpdate.Apply(plan, false); PackageUpdate.Complete(dest, "0.1.0-rc.8");
    Write(dest, ".env", "SYNTHETIC_KEY=preserved"); Write(dest, "notes.txt", "unrelated");
    Check(PackageUpdate.Describe(dest, "0.1.0-rc.8") == "Repair", "same version selects repair");
    Check(PackageUpdate.Describe(dest, "0.1.0-rc.9") == "Update", "newer selects update");
    Deny(() => PackageUpdate.Prepare(old, dest, "0.1.0-rc.7"), "downgrade rejected before copying");
    Check(File.ReadAllText(Path.Combine(dest, "VERSION")) == "0.1.0-rc.8" && !Directory.Exists(Path.Combine(dest, "backups")), "refusal creates no backup or mutation");
    Write(dest, "app/main.txt", "user-edit");
    plan = PackageUpdate.Prepare(newer, dest, "0.1.0-rc.9");
    Check(plan.Conflicts.SequenceEqual(new[] { Path.Combine("app", "main.txt") }), "local edit detected");
    Deny(() => PackageUpdate.Apply(plan, false), "local edit requires explicit backup-replace consent");
    Check(File.ReadAllText(Path.Combine(dest, "app/main.txt")) == "user-edit", "cancel preserves edit");
    var result = PackageUpdate.Apply(plan, true);
    Check(result.BackupDirectory is not null && File.ReadAllText(Path.Combine(result.BackupDirectory, "app/main.txt")) == "user-edit", "original edit backed up before replacement");
    Check(File.ReadAllText(Path.Combine(dest, "app/main.txt")) == "new", "approved replacement applied");
    Check(File.ReadAllText(Path.Combine(dest, ".env")) == "SYNTHETIC_KEY=preserved" && File.ReadAllText(Path.Combine(dest, "notes.txt")) == "unrelated", "config and unknown files preserved");
    Check(File.Exists(Path.Combine(dest, PackageUpdate.StateName)), "pending setup receipt retained");
    Deny(() => PackageUpdate.Prepare(old, dest, "0.1.0-rc.8"), "pending higher version blocks downgrade");
    PackageUpdate.Complete(dest, "0.1.0-rc.9");
    Check(!File.Exists(Path.Combine(dest, PackageUpdate.StateName)), "successful setup clears pending receipt");
    plan = PackageUpdate.Prepare(newer, dest, "0.1.0-rc.9");
    Check(plan.Conflicts.Count == 0, "repeat repair has no false conflicts");
    Write(dest, "app/main.txt", "changed-after-dialog");
    Deny(() => PackageUpdate.Apply(plan, true), "post-review edit invalidates plan");
    Check(File.ReadAllText(Path.Combine(dest, "app/main.txt")) == "changed-after-dialog", "post-review edit stays untouched");

    var legacy = Package("legacy", "0.1.0-rc.8", "manual-changes");
    Check(PackageUpdate.Prepare(newer, legacy, "0.1.0-rc.9").Conflicts.Count > 0, "missing manifest is not trusted for overwrite");
    Write(legacy, PackageUpdate.ManifestName, JsonSerializer.Serialize(new[] { new { path = "../escape", sha256 = new string('a', 64) } }));
    Deny(() => PackageUpdate.Prepare(newer, legacy, "0.1.0-rc.9"), "manifest traversal rejected before writes");
    Write(legacy, PackageUpdate.ManifestName, "broken json");
    try { PackageUpdate.Prepare(newer, legacy, "0.1.0-rc.9"); throw new Exception("Malformed JSON accepted"); }
    catch (JsonException) { Check(true, "malformed manifest fails closed"); }

    var watermark = Package("watermark", "0.1.0-rc.8", "old");
    Write(watermark, ".cti-installation.json", "{\"product\":\"CTI Self-Hosted\",\"version\":\"0.1.0\"}");
    Deny(() => PackageUpdate.Prepare(newer, watermark, "0.1.0-rc.9"), "metadata high-water version defeats stale VERSION");
    var blocked = Package("blocked", "0.1.0-rc.8", "old"); Write(blocked, "backups", "do-not-change");
    Deny(() => PackageUpdate.Prepare(newer, blocked, "0.1.0-rc.9"), "blocked backup path refused before overwrite");
    var partial = Path.Combine(root, "partial");
    Write(partial, PackageUpdate.StateName, "{\"product\":\"CTI Self-Hosted\",\"version\":\"0.1.0-rc.9\"}");
    Check(PackageUpdate.Prepare(newer, partial, "0.1.0-rc.9").Action == "Repair", "interrupted fresh copy can resume same version");
    var store = Path.Combine(root, "registry", "installations.json");
    RecentInstallations.Remember(store, dest); RecentInstallations.Remember(store, dest);
    Check(RecentInstallations.Read(store).Count == 1 && RecentInstallations.Read(store)[0] == dest, "custom path remembered without duplicates");
    Write(Path.GetDirectoryName(store)!, Path.GetFileName(store), "[\"relative/path\",\"Z:/missing-cti-fixture\"]");
    Check(RecentInstallations.Read(store).Count == 0, "stale and relative registry paths ignored");
    var foreign = Path.Combine(root, "foreign"); Write(foreign, "notes.txt", "not-cti");
    Deny(() => PackageUpdate.Prepare(newer, foreign, "0.1.0-rc.9"), "foreign nonempty directory refused");
    Deny(() => PackageUpdate.Prepare(old, Path.Combine(root, "mismatch"), "0.1.0-rc.9"), "payload and binary mismatch refused");
    Check(!Directory.Exists(Path.Combine(root, "mismatch")), "mismatched package does not create destination");
    var privatePackage = Package("private-package", "0.1.0-rc.9", "new"); Write(privatePackage, ".env", "must-not-copy");
    Deny(() => PackageUpdate.Prepare(privatePackage, Path.Combine(root, "private-dest"), "0.1.0-rc.9"), "payload may not ship private config");
    var changedSource = Package("mutable-source", "0.1.0-rc.9", "original");
    var mutableDest = Path.Combine(root, "mutable-dest"); plan = PackageUpdate.Prepare(changedSource, mutableDest, "0.1.0-rc.9");
    Write(changedSource, "app/main.txt", "after-review");
    Deny(() => PackageUpdate.Apply(plan, true), "changed source invalidates review");
    Check(!Directory.Exists(mutableDest), "changed source refused without destination mutation");
    var repairDest = Path.Combine(root, "repair-dest");
    PackageUpdate.Apply(PackageUpdate.Prepare(newer, repairDest, "0.1.0-rc.9"), false);
    PackageUpdate.Complete(repairDest, "0.1.0-rc.9");
    File.Delete(Path.Combine(repairDest, "app/main.txt"));
    PackageUpdate.Apply(PackageUpdate.Prepare(newer, repairDest, "0.1.0-rc.9"), false);
    Check(File.ReadAllText(Path.Combine(repairDest, "app/main.txt")) == "new", "repair restores missing package file");
    PackageUpdate.Complete(repairDest, "0.1.0-rc.9");
    File.Delete(Path.Combine(repairDest, "VERSION"));
    PackageUpdate.Apply(PackageUpdate.Prepare(newer, repairDest, "0.1.0-rc.9"), false);
    Check(File.ReadAllText(Path.Combine(repairDest, "VERSION")) == "0.1.0-rc.9", "successful metadata permits repairing missing VERSION");
    var collisionPackage = Package("collision-package", "0.1.0-rc.9", "new"); Write(collisionPackage, "new-file.txt", "package");
    Write(repairDest, "new-file.txt", "user-file");
    var collision = PackageUpdate.Prepare(collisionPackage, repairDest, "0.1.0-rc.9");
    Check(collision.Conflicts.Contains("new-file.txt"), "new package file colliding with user file is a conflict");
    var originalManifest = File.ReadAllText(Path.Combine(repairDest, PackageUpdate.ManifestName));
    Write(repairDest, PackageUpdate.ManifestName, "[]");
    Deny(() => PackageUpdate.Apply(collision, true), "manifest changed after confirmation invalidates plan");
    Write(repairDest, PackageUpdate.ManifestName, originalManifest);
    var pending = Path.Combine(root, "pending");
    Write(pending, PackageUpdate.StateName, "{\"product\":\"CTI Self-Hosted\",\"version\":\"0.1.0-rc.10\"}");
    Deny(() => PackageUpdate.Prepare(newer, pending, "0.1.0-rc.9"), "partial newer update cannot be downgraded");
    var metadataDir = Package("metadata-directory", "0.1.0-rc.8", "old");
    Directory.CreateDirectory(Path.Combine(metadataDir, PackageUpdate.ManifestName));
    Deny(() => PackageUpdate.Prepare(newer, metadataDir, "0.1.0-rc.9"), "directory in place of manifest refused");
    var link = Path.Combine(root, "linked-destination");
    try
    {
        Directory.CreateSymbolicLink(link, dest);
        Deny(() => PackageUpdate.Prepare(newer, link, "0.1.0-rc.9"), "linked destination refused");
        Directory.Delete(link); // Remove only the link, never its target.
    }
    catch (UnauthorizedAccessException) { Console.WriteLine("SKIP: symlink creation requires OS privileges."); }
    catch (IOException ex) when ((ex.HResult & 0xffff) == 1314) { Console.WriteLine("SKIP: symlink creation requires OS privileges."); }
    Console.WriteLine($"PASS: {passed} installer update assertions; no Docker or real installation used.");
}
finally
{
    var full = Path.GetFullPath(root);
    if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(full).StartsWith("cti-update-tests-", StringComparison.Ordinal)) throw new Exception("Unsafe test cleanup");
    Directory.Delete(full, true);
}
