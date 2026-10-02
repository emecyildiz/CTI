namespace CtiInstaller;

internal sealed class ReadinessDialog : Form
{
    private readonly CheckBox ai = new() { Name = "IncludeAi", Text = "Include &AI connection (optional)", TabIndex = 1, AutoSize = true };
    private readonly CheckBox telegram = new() { Name = "IncludeTelegram", Text = "Include &Telegram connection (optional)", TabIndex = 2, AutoSize = true };
    internal bool IncludeAi => ai.Checked;
    internal bool IncludeTelegram => telegram.Checked;
    internal ReadinessDialog()
    {
        Text = "Check setup — no API key needed"; StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(550, 285); AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(20), ColumnCount = 1, TabStop = false };
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(505, 0), Margin = new Padding(0, 0, 0, 12), Text = "Check whether the reviewed, disabled tasks have the expected saved connections. Select AI and Telegram only if you prepared those optional connections. Leave both unchecked for database-only setup. Do not edit n8n while this check runs." });
        layout.Controls.Add(ai); layout.Controls.Add(telegram);
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(505, 0), Margin = new Padding(0, 12, 0, 12), Text = "This reads local configuration only. It does not test external services, verify a public address, start collection, or send messages." });
        var actions = new FlowLayoutPanel { AccessibleName = "Preparation check actions", TabIndex = 4, TabStop = false, AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var check = new Button { Text = "&Check preparation", TabIndex = 0, AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Ca&ncel", TabIndex = 1, AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.Add(check); actions.Controls.Add(cancel); layout.Controls.Add(actions); Controls.Add(layout);
        AcceptButton = check; CancelButton = cancel;
        Shown += (_, _) => ai.Focus();
    }
}
