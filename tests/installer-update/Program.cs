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
    Check(AiConnection.ValidKey(new string('a', 20)) && AiConnection.ValidKey(new string('a', 512)), "AI key bounds accepted");
    foreach (var key in new[] { "", new string('a', 19), new string('a', 513), "synthetic key contains space", "synthetic-key-with\nnewline", "synthetic-key-with-ş" })
        Check(!AiConnection.ValidKey(key), "invalid AI key format refused");
    Check(AiConnection.Prepared(0, "CTI_MANAGED_AI_RESULT:v1:prepared\r\n"), "AI success accepts PowerShell newline");
    foreach (var output in new[] { "", "prepared", "CTI_MANAGED_AI_RESULT:v1:prepared\nextra output", "CTI_MANAGED_AI_RESULT:v1:failed" })
        Check(!AiConnection.Prepared(0, output), "AI malformed result never claims success");
    Check(!AiConnection.Prepared(1, "CTI_MANAGED_AI_RESULT:v1:prepared"), "AI exit failure overrides marker");
    var eligible = "CTI_MANAGED_N8N=true\r\nCTI_DATABASE_BOOTSTRAP_RECORDED=true\r\n";
    Check(AiConnection.CanOffer(eligible), "managed bootstrap history offers keyless AI action");
    foreach (var config in new[] { "", "CTI_MANAGED_N8N=true", eligible.Replace("=true", "=TRUE"), eligible + "CTI_MANAGED_N8N=true", eligible + "\0", new string('x', 65537) })
        Check(!AiConnection.CanOffer(config), "unknown malformed or legacy history hides keyless AI action");
    Check(SetupOutcome.Read("normal output\r\nCTI_SETUP_RESULT:v1:prepared\r\n") == ConnectionOutcome.Prepared, "structured result supports PowerShell line endings");
    foreach (var output in new[] { "", "Database prepared", "CTI_SETUP_RESULT:v1:prepared extra", "CTI_SETUP_RESULT:v2:prepared", "CTI_SETUP_RESULT:v1:prepared\nCTI_SETUP_RESULT:v1:preserved" })
        Check(SetupOutcome.Read(output) == ConnectionOutcome.Unknown, "missing malformed or conflicting connection result stays unconfirmed");
    Check(SetupOutcome.Read("CTI_SETUP_RESULT:v1:preserved") == ConnectionOutcome.Preserved, "reinstall preserves state without asserting connection readiness");
    Check(SetupOutcome.Description(ConnectionOutcome.Preserved).Contains("not tested"), "preserved state explains verification limitation");
    Check(RequirementGuidance.Failure(RequirementStage.Command).Contains("reopen"), "new Docker install explains installer restart");
    Check(RequirementGuidance.Failure(RequirementStage.Compose).Contains("repair"), "missing Compose offers repair guidance");
    Check(RequirementGuidance.Failure(RequirementStage.Engine).Contains("cannot reach"), "engine failure does not claim Docker is missing");
    Check(RequirementGuidance.Failure(RequirementStage.Engine).Contains("virtualization"), "engine guidance includes VM prerequisite failures");
    Check(RequirementGuidance.NextStep(true).Contains("local n8n account"), "managed setup explains account step");
    Check(RequirementGuidance.NextStep(false).Contains("External n8n was not changed"), "external setup does not promise local n8n account");
    Check(new[] { true, false }.All(managed => RequirementGuidance.NextStep(managed).Contains("does not activate")), "installation never implies collection activated");
    Check(RequirementGuidance.NextStep(true).Contains("keep their current state"), "repair does not claim existing tasks are stopped");
    var testSecret = new string('x', 43);
    var managedConfig = "CTI_MANAGED_N8N=true\nCTI_DASHBOARD_BIND=127.0.0.1\nCTI_DASHBOARD_PORT=8085\nCTI_N8N_API_URL=http://cti-n8n:5678/api/v1\nCTI_APP_PASSWORD=" + testSecret;
    var connection = ManagedConnectionSettings.Parse(managedConfig);
    Check(connection.Dashboard.AbsoluteUri == "http://127.0.0.1:8085/" && connection.ApplicationPassword == testSecret,
        "managed connection selects loopback and application secret");
    Check(!connection.ToString().Contains(testSecret), "connection display hides secret");
    foreach (var unsafeConfig in new[] {
        managedConfig.Replace("CTI_MANAGED_N8N=true", "CTI_MANAGED_N8N=false"),
        managedConfig.Replace("127.0.0.1", "0.0.0.0"),
        managedConfig.Replace("http://cti-n8n:5678/api/v1", "https://example.invalid/api/v1"),
        managedConfig.Replace("PORT=8085", "PORT=65536"),
        managedConfig.Replace("PORT=8085", "PORT=-1"),
        managedConfig + "\nCTI_DASHBOARD_PORT=9000",
        managedConfig.Replace(testSecret, "${OTHER_PASSWORD}"),
        managedConfig.Replace(testSecret, "\"" + testSecret + "\""),
        managedConfig.Replace("CTI_APP_PASSWORD", "POSTGRES_PASSWORD"),
        managedConfig + '\0', new string('x', 65537) })
        Deny(() => ManagedConnectionSettings.Parse(unsafeConfig), "unsafe managed connection settings rejected");
    try { ManagedConnectionSettings.Parse(managedConfig + "\nCTI_APP_PASSWORD=" + testSecret); }
    catch (InvalidDataException error) { Check(!error.Message.Contains(testSecret), "configuration errors hide secrets"); }
    Check(SetupNavigation.PrimaryLabel(true) == "Continue setup", "fresh install guides to setup");
    Check(SetupNavigation.PrimaryLabel(false) == "Open dashboard", "repair/update keeps dashboard action");
    foreach (var port in new[] { 1, 8085, 65535 })
    {
        Check(SetupNavigation.Url(port, true) == $"http://127.0.0.1:{port}/setup", "setup URL uses selected port");
        Check(SetupNavigation.Url(port, false) == $"http://127.0.0.1:{port}", "dashboard URL stays loopback");
    }
    foreach (var port in new[] { 0, -1, 65536 })
    {
        try { SetupNavigation.Url(port, true); throw new Exception("Invalid port accepted"); }
        catch (ArgumentOutOfRangeException) { Check(true, "navigation rejects invalid port"); }
    }
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
