using CtiInstaller;

internal static class UiAcceptance
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
#if UI_PREVIEW
        args = ["--preview"];
#endif
        if (args.Length == 1 && args[0] == "--preview")
        {
            Application.Run(new Preview());
            return 0;
        }
        if (args.Length != 0) throw new ArgumentException("Use no arguments for structural checks, or --preview for local UI review.");
        foreach (var scale in new[] { 1f, 1.5f, 2f })
        foreach (var kind in new[] { "AI", "Telegram", "Check setup" })
        {
            using var dialog = Create(kind);
            ScalePreview(dialog, scale);
            dialog.CreateControl();
            dialog.PerformLayout();
            Check(dialog.AcceptButton is Button && dialog.CancelButton is Button, "Enter/Escape actions");
            foreach (var input in Descendants(dialog).OfType<TextBox>())
                Check(!string.IsNullOrEmpty(input.AccessibleName) && input.TabStop, "named keyboard input");
            foreach (var input in Descendants(dialog).OfType<TextBox>().Where(x => x.AccessibleName!.Contains("key") || x.AccessibleName!.Contains("token")))
                Check(input.UseSystemPasswordChar, "secret input masking");
            foreach (var label in Descendants(dialog).OfType<Label>())
                Check(label.Height >= label.GetPreferredSize(new Size(label.Width, 0)).Height, "wrapped label height");
            var actions = Descendants(dialog).OfType<FlowLayoutPanel>().Single();
            Check(actions.Controls.Cast<Control>().All(c => c.Bottom <= actions.ClientSize.Height && c.Right <= actions.ClientSize.Width), "action buttons fit their row");
            Check(actions.Controls.Cast<Control>().Select(c => c.TabIndex).SequenceEqual([0, 1]), "action keyboard order");
            var mnemonicKeys = Descendants(dialog).Where(c => c is Button or Label or CheckBox)
                .Select(c => c.Text.IndexOf('&') is var n && n >= 0 && n + 1 < c.Text.Length ? char.ToUpperInvariant(c.Text[n + 1]) : '\0').Where(c => c != '\0').ToArray();
            Check(mnemonicKeys.Distinct().Count() == mnemonicKeys.Length, "unique keyboard mnemonics");
            Console.WriteLine($"PASS: {kind}, {scale:P0} component scale, keyboard actions and input accessibility");
        }
        using (var audit = new ReadinessDialog())
            Check(!audit.IncludeAi && !audit.IncludeTelegram, "optional connections require explicit selection");
        using (var ai = new AiKeyDialog())
        {
            var field = Descendants(ai).OfType<TextBox>().Single();
            field.Text = "SYNTHETIC_TEST_KEY_NOT_A_CREDENTIAL";
            Check(ai.TakeKey() == "SYNTHETIC_TEST_KEY_NOT_A_CREDENTIAL" && field.Text == "", "AI take clears the input");
        }
        using (var telegram = new TelegramDialog())
        {
            var field = Descendants(telegram).OfType<TextBox>().Single(c => c.Name == "TelegramBotToken");
            field.Text = "SYNTHETIC_TEST_TOKEN_NOT_A_CREDENTIAL";
            Check(telegram.TakeToken() == "SYNTHETIC_TEST_TOKEN_NOT_A_CREDENTIAL" && field.Text == "", "Telegram take clears the input");
        }
        Console.WriteLine("PASS: component checks only; preview/real monitor DPI and installer integration are separate.");
        return 0;
    }
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); }
    internal static Form Create(string kind) => kind switch { "AI" => new AiKeyDialog(), "Telegram" => new TelegramDialog(), "Check setup" => new ReadinessDialog(), _ => throw new ArgumentException("Unknown preview") };
    private static void ScalePreview(Form dialog, float factor)
    {
        dialog.Scale(new SizeF(factor, factor));
        dialog.Font = new Font(dialog.Font.FontFamily, 9.5f * factor);
    }
    internal static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private sealed class Preview : Form
    {
        internal Preview()
        {
            Text = "CTI dialog preview — no installation or provider calls";
            Font = new Font("Segoe UI", 10f); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(620, 225);
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), FlowDirection = FlowDirection.TopDown, WrapContents = false };
            panel.Controls.Add(new Label { AutoSize = true, Text = "Synthetic UI preview. No Docker, credentials, messages or installation." });
            var scale = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, AccessibleName = "Component preview scale" };
            scale.Items.AddRange(["100% component scale", "150% component scale", "200% component scale"]); scale.SelectedIndex = 0;
            panel.Controls.Add(scale);
            foreach (var kind in new[] { "AI", "Telegram", "Check setup" })
            {
                var button = new Button { Text = "Preview " + kind, AutoSize = true };
                button.Click += (_, _) => { using var dialog = Create(kind); var factor = new[] {1f,1.5f,2f}[scale.SelectedIndex]; ScalePreview(dialog, factor); dialog.ShowInTaskbar = true; dialog.ShowDialog(this); };
                panel.Controls.Add(button);
            }
            Controls.Add(panel);
        }
    }
}
