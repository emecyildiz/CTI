namespace CtiInstaller;

internal sealed class AiKeyDialog : Form
{
    private readonly TextBox key = new() { UseSystemPasswordChar = true, MaxLength = 512, Dock = DockStyle.Top, AccessibleName = "Google Gemini API key" };
    internal string TakeKey() { var value = key.Text; key.Clear(); return value; }

    internal AiKeyDialog()
    {
        Text = "Connect AI — Google Gemini";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(550, 285);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(20), ColumnCount = 1, RowCount = 4 };
        for (var i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(505, 0), Margin = new Padding(0, 0, 0, 12), Text = "Enter your Google Gemini API key. CTI stores it in its own local automation service, not in the installer or its log. Other providers still require manual configuration." });
        layout.Controls.Add(key);
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(505, 0), Margin = new Padding(0, 12, 0, 12), Text = "This prepares two AI tasks but does not test the provider or start any tasks. Afterwards, use Check setup here and review news sources in your browser." });
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var connect = new Button { Text = "Prepare connection", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        connect.Click += (_, _) =>
        {
            if (!AiConnection.ValidKey(key.Text)) { MessageBox.Show(this, "Enter a key with 20–512 printable characters and no spaces.", "Check API key", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            DialogResult = DialogResult.OK;
        };
        actions.Controls.Add(connect); actions.Controls.Add(cancel);
        layout.Controls.Add(actions); Controls.Add(layout);
        AcceptButton = connect; CancelButton = cancel;
        Shown += (_, _) => key.Focus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) key.Clear();
        base.Dispose(disposing);
    }
}
