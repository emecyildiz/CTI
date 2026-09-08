using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace CtiInstaller;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0].Equals("--verify-payload", StringComparison.OrdinalIgnoreCase))
            return InstallerForm.VerifyEmbeddedPayload();

        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerForm());
        return 0;
    }
}

internal sealed class InstallerForm : Form
{
    private const string PayloadResourceName = "CtiInstaller.Payload.zip";
    private string dashboardUrl = "http://127.0.0.1:8080";
    private string n8nUrl = "http://127.0.0.1:5678";

    private readonly TextBox installPath = new();
    private readonly RadioButton managedN8n = new();
    private readonly RadioButton existingN8n = new();
    private readonly Label requirementStatus = new();
    private readonly RichTextBox log = new();
    private readonly ProgressBar progress = new();
    private readonly Button installButton = new();
    private readonly Button checkButton = new();
    private readonly Button browseButton = new();
    private readonly Button openDashboardButton = new();
    private readonly Button openN8nButton = new();
    private readonly NumericUpDown dashboardPort = new() { Minimum = 1, Maximum = 65535, Value = 8080, Width = 85 };
    private readonly NumericUpDown n8nPort = new() { Minimum = 1, Maximum = 65535, Value = 5678, Width = 85 };
    private readonly ComboBox managementAction = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
    private readonly Button manageButton = new() { Text = "Manage installation", AutoSize = true };
    private readonly Label installationStatus = new() { AutoSize = true, MaximumSize = new Size(730, 0) };
    private readonly ComboBox recentInstallations = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private bool dockerReady;
    private bool operationRunning;
    private static string InstallerVersion => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+')[0];

    public InstallerForm()
    {
        Text = "CTI Self-Hosted Setup";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(800, 800);
        Size = new Size(900, 860);
        BackColor = Color.FromArgb(246, 247, 249);
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 6,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var title = new Label
        {
            AutoSize = true,
            Text = "CTI Self-Hosted",
            Font = new Font("Segoe UI Semibold", 22f, FontStyle.Bold),
            ForeColor = Color.FromArgb(22, 27, 34),
            Margin = new Padding(0, 0, 0, 3),
        };
        root.Controls.Add(title);

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(790, 0),
            Text = "Installs the private dashboard, PostgreSQL database, and optionally a managed n8n instance. Workflows are imported disabled and are never activated automatically.",
            ForeColor = Color.FromArgb(82, 89, 99),
            Margin = new Padding(0, 0, 0, 18),
        };
        root.Controls.Add(intro);

        root.Controls.Add(BuildLocationPanel());
        root.Controls.Add(BuildOptionsPanel());
        root.Controls.Add(BuildStatusPanel());
        root.Controls.Add(BuildActionPanel());

        Shown += async (_, _) => { LoadSavedOptions(); await CheckRequirementsAsync(); };
        installPath.Leave += (_, _) => LoadSavedOptions();
        installPath.TextChanged += (_, _) => RefreshInstallAction();
        FormClosing += (_, e) =>
        {
            if (!operationRunning) return;
            e.Cancel = true;
            MessageBox.Show(this, "Setup is still running. Wait for it to finish before closing this window.", "CTI Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
    }

    private Control BuildLocationPanel()
    {
        var panel = new GroupBox
        {
            Text = "Installation location",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 12),
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        installPath.Dock = DockStyle.Fill;
        installPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Emecworks", "CTI");
        browseButton.Text = "Browse…";
        browseButton.AutoSize = true;
        browseButton.Click += (_, _) => BrowseForDirectory();
        layout.Controls.Add(installPath, 0, 0);
        layout.Controls.Add(browseButton, 1, 0);
        var known = RecentInstallations.Read(RecentInstallations.DefaultStore);
        recentInstallations.Items.Add("Previously installed folders…");
        recentInstallations.Items.AddRange(known.Cast<object>().ToArray());
        recentInstallations.SelectedIndex = 0;
        recentInstallations.AccessibleName = "Previously installed CTI folders";
        recentInstallations.Visible = known.Count > 0;
        recentInstallations.SelectionChangeCommitted += (_, _) =>
        {
            if (recentInstallations.SelectedIndex > 0 && recentInstallations.SelectedItem is string path) { installPath.Text = path; LoadSavedOptions(); }
        };
        layout.Controls.Add(recentInstallations, 0, 1);
        layout.SetColumnSpan(recentInstallations, 2);
        layout.Controls.Add(installationStatus, 0, 2);
        layout.SetColumnSpan(installationStatus, 2);
        installPath.AccessibleName = "CTI installation folder";
        panel.Controls.Add(layout);
        return panel;
    }

    private Control BuildOptionsPanel()
    {
        var panel = new GroupBox
        {
            Text = "n8n option",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 12),
        };
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        managedN8n.Text = "Install and manage a local n8n container (recommended)";
        managedN8n.AutoSize = true;
        managedN8n.Checked = true;
        existingN8n.Text = "Use an existing n8n instance (manual workflow import)";
        existingN8n.AutoSize = true;
        layout.Controls.Add(managedN8n);
        layout.Controls.Add(existingN8n);
        var ports = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        ports.Controls.Add(new Label { Text = "Dashboard port", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        ports.Controls.Add(dashboardPort);
        ports.Controls.Add(new Label { Text = "Managed n8n port", AutoSize = true, Margin = new Padding(12, 6, 6, 0) });
        ports.Controls.Add(n8nPort);
        layout.Controls.Add(ports);
        layout.Controls.Add(new Label { Text = "Local access only (127.0.0.1). Existing settings are loaded; external n8n is not changed.", AutoSize = true });
        managedN8n.CheckedChanged += (_, _) => n8nPort.Enabled = managedN8n.Checked && !operationRunning;
        panel.Controls.Add(layout);
        return panel;
    }

    private Control BuildStatusPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0, 0, 0, 12),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var statusRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        requirementStatus.AutoSize = true;
        requirementStatus.Text = "Checking Docker…";
        requirementStatus.ForeColor = Color.FromArgb(82, 89, 99);
        requirementStatus.Margin = new Padding(0, 7, 12, 0);
        checkButton.Text = "Check again";
        checkButton.AutoSize = true;
        checkButton.Click += async (_, _) => await CheckRequirementsAsync();
        var dockerLink = new LinkLabel
        {
            Text = "Get Docker Desktop",
            AutoSize = true,
            Margin = new Padding(12, 8, 0, 0),
        };
        dockerLink.LinkClicked += (_, _) => OpenUrl("https://docs.docker.com/desktop/setup/install/windows-install/");
        var sourceLink = new LinkLabel
        {
            Text = "Source and license",
            AutoSize = true,
            Margin = new Padding(12, 8, 0, 0),
        };
        sourceLink.LinkClicked += (_, _) => OpenUrl("https://github.com/emecyildiz/CTI");
        statusRow.Controls.Add(requirementStatus);
        statusRow.Controls.Add(checkButton);
        statusRow.Controls.Add(dockerLink);
        statusRow.Controls.Add(sourceLink);
        panel.Controls.Add(statusRow, 0, 0);

        progress.Dock = DockStyle.Top;
        progress.Style = ProgressBarStyle.Marquee;
        progress.MarqueeAnimationSpeed = 25;
        progress.Visible = false;
        progress.Margin = new Padding(0, 8, 0, 8);
        panel.Controls.Add(progress, 0, 1);

        log.Dock = DockStyle.Fill;
        log.ReadOnly = true;
        log.BackColor = Color.FromArgb(18, 22, 28);
        log.ForeColor = Color.FromArgb(221, 226, 232);
        log.BorderStyle = BorderStyle.FixedSingle;
        log.Font = new Font("Cascadia Mono", 9f);
        log.WordWrap = false;
        log.Text = "Ready.\n";
        panel.Controls.Add(log, 0, 2);
        return panel;
    }

    private Control BuildActionPanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
        };
        installButton.Text = "Install CTI";
        installButton.AutoSize = true;
        installButton.Padding = new Padding(12, 5, 12, 5);
        installButton.Enabled = false;
        installButton.Click += async (_, _) => await InstallAsync();
        openDashboardButton.Text = "Open dashboard";
        openDashboardButton.AutoSize = true;
        openDashboardButton.Enabled = false;
        openDashboardButton.Click += (_, _) => OpenUrl(dashboardUrl);
        openN8nButton.Text = "Open n8n";
        openN8nButton.AutoSize = true;
        openN8nButton.Enabled = false;
        openN8nButton.Click += (_, _) => OpenUrl(n8nUrl);
        panel.Controls.Add(installButton);
        panel.Controls.Add(openDashboardButton);
        panel.Controls.Add(openN8nButton);
        managementAction.Items.AddRange(["Stop (keep data)", "Remove services (keep data)", "Purge CTI data and package"]);
        managementAction.SelectedIndex = 0;
        manageButton.Click += async (_, _) => await ManageAsync();
        panel.Controls.Add(manageButton);
        panel.Controls.Add(managementAction);
        return panel;
    }

    private void BrowseForDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder for CTI Self-Hosted",
            UseDescriptionForTitle = true,
            SelectedPath = installPath.Text,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) { installPath.Text = dialog.SelectedPath; LoadSavedOptions(); }
    }

    private void LoadSavedOptions()
    {
        if (operationRunning) return;
        RefreshInstallAction();
        try
        {
            var env = Path.Combine(Path.GetFullPath(installPath.Text), ".env");
            if (!File.Exists(env)) return;
            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(env))
            {
                var separator = line.IndexOf('=');
                if (separator > 0) settings[line[..separator]] = line[(separator + 1)..];
            }
            if (settings.TryGetValue("CTI_DASHBOARD_PORT", out var dp) && int.TryParse(dp, out var d) && d is >= 1 and <= 65535) dashboardPort.Value = d;
            if (settings.TryGetValue("N8N_PORT", out var np) && int.TryParse(np, out var n) && n is >= 1 and <= 65535) n8nPort.Value = n;
            managedN8n.Checked = settings.GetValueOrDefault("CTI_MANAGED_N8N") == "true";
            existingN8n.Checked = !managedN8n.Checked;
        }
        catch { AppendLog("Could not load saved settings. Check the installation folder before proceeding."); }
    }

    private void RefreshInstallAction()
    {
        if (operationRunning) return;
        try
        {
            var destination = ValidateDestination(installPath.Text);
            var action = PackageUpdate.Describe(destination, InstallerVersion);
            var installed = PackageUpdate.InstalledVersion(destination);
            installButton.Text = action + " CTI";
            installationStatus.Text = installed is null ? $"New installation — package {InstallerVersion}"
                : $"Installed/attempted: {installed} — package: {InstallerVersion} — {action}. Existing n8n workflows are preserved.";
            installationStatus.ForeColor = Color.FromArgb(82, 89, 99);
            var hasConfig = File.Exists(Path.Combine(destination, ".env"));
            managedN8n.Enabled = existingN8n.Enabled = !hasConfig;
            installButton.Enabled = dockerReady && HasEmbeddedPayload();
        }
        catch (Exception exception)
        {
            installationStatus.Text = exception.Message;
            installationStatus.ForeColor = Color.FromArgb(190, 45, 45);
            installButton.Enabled = false;
        }
    }

    private async Task ManageAsync()
    {
        if (operationRunning) return;
        var temporary = Path.Combine(Path.GetTempPath(), $"cti-installer-{Guid.NewGuid():N}");
        try
        {
            var destination = Path.GetFullPath(installPath.Text);
            _ = PackageUpdate.Describe(destination, InstallerVersion);
            var ownerPath = Path.Combine(destination, ".cti-owner.json");
            if (!File.Exists(ownerPath)) throw new InvalidOperationException("No ownership record. Automatic removal of legacy rc.7 installations is disabled.");
            using var owner = JsonDocument.Parse(File.ReadAllText(ownerPath));
            var project = owner.RootElement.GetProperty("project").GetString() ?? "";
            var action = new[] { "Stop", "Remove", "Purge" }[managementAction.SelectedIndex];
            SetBusy(true);
            SetInputsEnabled(false);
            Directory.CreateDirectory(temporary);
            var package = ExtractPayload(temporary);
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(package, "manage.ps1"), "-InstallationPath", destination, "-Action", action };
            var preview = await RunProcessAsync(powershell, args.Concat(["-Preview"]), package, true, TimeSpan.FromMinutes(2));
            if (preview.ExitCode != 0) throw new InvalidOperationException("Ownership checks failed. No resources were removed; see the log.");
            var warning = action == "Purge"
                ? "Permanently delete this CTI installation's PostgreSQL data, managed n8n data/credentials, .env and unchanged package files? Back up first. Backups, modified/unknown files, Docker/WSL, images and external n8n will remain."
                : action == "Remove" ? "Remove this CTI installation's containers and network? Data and installation files will remain."
                : "Stop this CTI installation? Data and files will remain.";
            if (MessageBox.Show(this, $"{warning}\n\nFolder: {destination}\nProject: {project}\n\nReview the resource counts in the log before confirming.", "Confirm CTI management", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var result = await RunProcessAsync(powershell, args.Concat(["-ConfirmProject", project]), package, true, TimeSpan.FromMinutes(5));
            if (result.ExitCode != 0) throw new InvalidOperationException("Management stopped on an error. Some earlier steps may have completed; review the log and retry after resolving the cause.");
            openDashboardButton.Enabled = false;
            openN8nButton.Enabled = false;
            SetRequirement("Management completed. See the log for retained files/data.", Color.FromArgb(24, 128, 56));
        }
        catch (Exception exception) { AppendLog(exception.Message); MessageBox.Show(this, exception.Message, "CTI management", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { TryDeleteOwnedTemporaryDirectory(temporary); SetBusy(false); SetInputsEnabled(true); RefreshInstallAction(); }
    }

    private async Task CheckRequirementsAsync()
    {
        if (operationRunning) return;
        SetBusy(true);
        dockerReady = false;
        installButton.Enabled = false;
        SetRequirement("Checking Docker CLI…", Color.FromArgb(82, 89, 99));
        AppendLog("Checking Docker prerequisites…");
        try
        {
            var version = await RunProcessAsync("docker", ["--version"], null, false, TimeSpan.FromSeconds(15));
            if (version.ExitCode != 0) throw new InvalidOperationException("Docker CLI was not found.");

            var compose = await RunProcessAsync("docker", ["compose", "version"], null, false, TimeSpan.FromSeconds(15));
            if (compose.ExitCode != 0) throw new InvalidOperationException("Docker Compose v2 is unavailable.");

            var engine = await RunProcessAsync("docker", ["info", "--format", "{{.ServerVersion}}"], null, false, TimeSpan.FromSeconds(20));
            if (engine.ExitCode != 0) throw new InvalidOperationException("Docker Desktop is installed, but its engine is not running.");

            SetRequirement($"Docker is ready (engine {engine.Output.Trim()}).", Color.FromArgb(24, 128, 56));
            dockerReady = true;
            AppendLog("Docker Desktop and Compose are ready.");
            installButton.Enabled = HasEmbeddedPayload();
            if (!installButton.Enabled)
            {
                SetRequirement("This development build does not contain the installation payload.", Color.FromArgb(176, 80, 0));
                AppendLog("The embedded release payload is missing. Use an installer downloaded from GitHub Releases.");
            }
        }
        catch (Exception exception)
        {
            SetRequirement(exception.Message, Color.FromArgb(190, 45, 45));
            AppendLog($"Requirement check failed: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
            RefreshInstallAction();
        }
    }

    private async Task InstallAsync()
    {
        if (operationRunning) return;
        string destination;
        try
        {
            destination = ValidateDestination(installPath.Text);
            _ = PackageUpdate.Describe(destination, InstallerVersion);
            if (managedN8n.Checked && dashboardPort.Value == n8nPort.Value) throw new InvalidOperationException("Dashboard and managed n8n ports must differ.");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Invalid installation location", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true);
        SetInputsEnabled(false);
        openDashboardButton.Enabled = false;
        openN8nButton.Enabled = false;
        AppendLog($"Installing into {destination}");

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"cti-installer-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var packageRoot = ExtractPayload(temporaryDirectory);
            var plan = PackageUpdate.Prepare(packageRoot, destination, InstallerVersion);
            var allowConflicts = false;
            if (plan.InstalledVersion is not null)
            {
                var changes = plan.Conflicts.Count == 0 ? "No local file conflicts detected."
                    : $"{plan.Conflicts.Count} locally modified or untracked package files will be backed up and replaced:\n" + string.Join("\n", plan.Conflicts.Take(12));
                var notice = $"{plan.Action} CTI {plan.InstalledVersion} → {plan.TargetVersion}?\n\n{changes}\n\nReplaced files are backed up under backups/installer-* before overwriting. Backups may contain your edits or secrets; keep them private. The .env and Docker data are retained. Existing n8n workflows and credentials are NOT automatically upgraded or re-imported. Database migrations are not automatically rolled back.\n\nContinue?";
                if (MessageBox.Show(this, notice, "Review CTI " + plan.Action, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                allowConflicts = true;
            }
            var copied = PackageUpdate.Apply(plan, allowConflicts);
            if (copied.BackupDirectory is not null) AppendLog("Previous package backup: " + copied.BackupDirectory);
            AppendLog("Installation files are ready.");

            var setupScript = Path.Combine(destination, "setup.ps1");
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var arguments = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", setupScript };
            arguments.AddRange(["-DashboardPort", dashboardPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            if (managedN8n.Checked) arguments.AddRange(["-N8nPort", n8nPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            if (existingN8n.Checked)
            {
                arguments.Add("-UseExistingN8n");
                arguments.Add("-SkipWorkflowImport");
            }

            AppendLog("Starting the CTI setup script…");
            var result = await RunProcessAsync(powershell, arguments, destination, true, TimeSpan.FromMinutes(20));
            if (result.ExitCode != 0) throw new InvalidOperationException($"Setup stopped with exit code {result.ExitCode}.");

            PackageUpdate.Complete(destination, InstallerVersion);
            try { RecentInstallations.Remember(RecentInstallations.DefaultStore, destination); }
            catch { AppendLog("Installed successfully, but the recent-folder list could not be saved. Keep the installation path."); }
            dashboardUrl = $"http://127.0.0.1:{dashboardPort.Value}";
            n8nUrl = $"http://127.0.0.1:{n8nPort.Value}";
            SetRequirement("CTI Self-Hosted was installed successfully.", Color.FromArgb(24, 128, 56));
            AppendLog("Installation completed successfully.");
            openDashboardButton.Enabled = true;
            openN8nButton.Enabled = managedN8n.Checked;
            MessageBox.Show(this, plan.InstalledVersion is null
                ? "CTI Self-Hosted is ready. Create the first n8n owner account, then continue from the dashboard setup page."
                : "CTI package setup completed. Existing n8n workflows and credentials were preserved, not upgraded. Review release notes before making workflow changes.", "Installation complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            SetRequirement("Installation did not complete.", Color.FromArgb(190, 45, 45));
            AppendLog($"ERROR: {exception.Message}");
            MessageBox.Show(this, $"Installation did not complete.\n\n{exception.Message}\n\nReview the log in this window before retrying.", "CTI Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            TryDeleteOwnedTemporaryDirectory(temporaryDirectory);
            SetBusy(false);
            SetInputsEnabled(true);
            RefreshInstallAction();
        }
    }

    private static string ValidateDestination(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Choose an installation folder.");
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));
        AssertNoReparsePath(fullPath);
        var root = Path.GetPathRoot(fullPath);
        if (string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), root?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A drive root cannot be used as the installation folder.");

        if (!Directory.Exists(fullPath)) return fullPath;
        var entries = Directory.EnumerateFileSystemEntries(fullPath).Take(1).Any();
        if (!entries) return fullPath;

        var recognized = File.Exists(Path.Combine(fullPath, "compose.yml"))
            && File.Exists(Path.Combine(fullPath, "setup.ps1"))
            && File.Exists(Path.Combine(fullPath, "VERSION"));
        recognized |= File.Exists(Path.Combine(fullPath, PackageUpdate.StateName)) && PackageUpdate.InstalledVersion(fullPath) is not null;
        recognized |= File.Exists(Path.Combine(fullPath, ".cti-installation.json")) && PackageUpdate.InstalledVersion(fullPath) is not null;
        if (!recognized)
            throw new InvalidOperationException("The selected folder is not empty and is not an existing CTI Self-Hosted installation.");
        return fullPath;
    }

    private static bool HasEmbeddedPayload() => Assembly.GetExecutingAssembly().GetManifestResourceInfo(PayloadResourceName) is not null;

    internal static int VerifyEmbeddedPayload()
    {
        if (!HasEmbeddedPayload()) return 2;
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"cti-installer-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var packageRoot = ExtractPayload(temporaryDirectory);
            var version = File.ReadAllText(Path.Combine(packageRoot, "VERSION")).Trim();
            var binaryVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion.Split('+')[0];
            if (string.IsNullOrWhiteSpace(version) || version != binaryVersion) return 3;
            var copiedPackage = Path.Combine(temporaryDirectory, "copied-package");
            CopyPackage(packageRoot, copiedPackage);
            foreach (var source in Directory.GetFiles(packageRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(packageRoot, source);
                if (relative.Equals(".env", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(copiedPackage, relative);
                if (!File.Exists(target)) return 5;
                using var sourceStream = File.OpenRead(source);
                using var targetStream = File.OpenRead(target);
                if (!SHA256.HashData(sourceStream).AsSpan().SequenceEqual(SHA256.HashData(targetStream))) return 5;
            }
            return File.Exists(Path.Combine(copiedPackage, "setup.ps1"))
                && File.Exists(Path.Combine(copiedPackage, "compose.yml"))
                && File.ReadAllText(Path.Combine(copiedPackage, "VERSION")).Trim() == version
                ? 0
                : 5;
        }
        catch
        {
            return 4;
        }
        finally
        {
            TryDeleteOwnedTemporaryDirectory(temporaryDirectory);
        }
    }

    private static string ExtractPayload(string temporaryDirectory)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName)
            ?? throw new InvalidOperationException("The installer payload is missing.");
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: false);
        var extractionRoot = Path.GetFullPath(Path.Combine(temporaryDirectory, "payload"));
        Directory.CreateDirectory(extractionRoot);
        var prefix = extractionRoot.EndsWith(Path.DirectorySeparatorChar) ? extractionRoot : extractionRoot + Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(extractionRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The embedded package contains an unsafe path.");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }

        var roots = Directory.GetDirectories(extractionRoot);
        if (roots.Length != 1 || !File.Exists(Path.Combine(roots[0], "setup.ps1")) || !File.Exists(Path.Combine(roots[0], "compose.yml")))
            throw new InvalidDataException("The embedded CTI package layout is invalid.");
        return roots[0];
    }

    private static void CopyPackage(string source, string destination)
    {
        var plan = PackageUpdate.Prepare(source, destination, InstallerVersion);
        PackageUpdate.Apply(plan, false);
    }

    private static void AssertNoReparsePath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Installation paths must not traverse a junction or symbolic link.");
        }
    }

    private async Task<ProcessResult> RunProcessAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory, bool streamToLog, TimeSpan timeout)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);

        var output = new List<string>();
        process.OutputDataReceived += (_, eventArgs) => CaptureLine(eventArgs.Data, output, streamToLog);
        process.ErrorDataReceived += (_, eventArgs) => CaptureLine(eventArgs.Data, output, streamToLog);
        try
        {
            if (!process.Start()) return new ProcessResult(-1, string.Empty);
        }
        catch (Exception exception)
        {
            return new ProcessResult(-1, exception.Message);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{Path.GetFileName(fileName)} did not finish within {timeout.TotalMinutes:0.#} minutes.");
        }
        return new ProcessResult(process.ExitCode, string.Join(Environment.NewLine, output));
    }

    private void CaptureLine(string? line, List<string> output, bool streamToLog)
    {
        if (line is null) return;
        lock (output) output.Add(line);
        if (streamToLog) AppendLog(line);
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message));
            return;
        }
        log.AppendText(message + Environment.NewLine);
        log.SelectionStart = log.TextLength;
        log.ScrollToCaret();
    }

    private void SetRequirement(string message, Color color)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetRequirement(message, color));
            return;
        }
        requirementStatus.Text = message;
        requirementStatus.ForeColor = color;
    }

    private void SetBusy(bool busy)
    {
        operationRunning = busy;
        progress.Visible = busy;
        checkButton.Enabled = !busy;
        manageButton.Enabled = !busy;
        managementAction.Enabled = !busy;
        if (busy) installButton.Enabled = false;
    }

    private void SetInputsEnabled(bool enabled)
    {
        installPath.Enabled = enabled;
        browseButton.Enabled = enabled;
        var hasConfig = File.Exists(Path.Combine(installPath.Text, ".env"));
        managedN8n.Enabled = enabled && !hasConfig;
        existingN8n.Enabled = enabled && !hasConfig;
        dashboardPort.Enabled = enabled;
        n8nPort.Enabled = enabled && managedN8n.Checked;
        recentInstallations.Enabled = enabled;
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static void TryDeleteOwnedTemporaryDirectory(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            if (fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullPath).StartsWith("cti-installer-", StringComparison.Ordinal))
                Directory.Delete(fullPath, recursive: true);
        }
        catch
        {
            // A failed temporary cleanup must not mask the installation result.
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
