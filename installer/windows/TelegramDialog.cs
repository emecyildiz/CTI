namespace CtiInstaller;

internal sealed class TelegramDialog : Form
{
    private readonly TextBox token = new() { UseSystemPasswordChar = true, MaxLength = 149, Dock = DockStyle.Top, AccessibleName = "Telegram bot token" };
    private readonly TextBox chat = new() { MaxLength = 20, Dock = DockStyle.Top, AccessibleName = "Your private Telegram user ID" };
    internal string TakeToken() { var value = token.Text; token.Clear(); return value; }
    internal string PrivateChatId => chat.Text;

    internal TelegramDialog()
    {
        Text = "Connect Telegram (optional)";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(550, 410); AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(20), ColumnCount = 1 };
        void Label(string text) => layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(505, 0), Margin = new Padding(0, 10, 0, 8), Text = text });
        Label("Telegram is optional. Create a bot with Telegram's official @BotFather and copy its token here. Open a private chat with your bot and press Start before using it later.");
        Label("Bot token (secret)"); layout.Controls.Add(token);
        Label("Your numeric Telegram user ID — not your @username or phone number. Only your private chat will be allowed; group IDs are not accepted."); layout.Controls.Add(chat);
        Label("This connects three tasks but sends no message and starts no tasks. Incoming questions also need a public HTTPS address; local installation alone does not provide one. Continue the final review after preparation.");
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var prepare = new Button { Text = "Prepare connection", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        prepare.Click += (_, _) =>
        {
            if (!TelegramConnection.ValidToken(token.Text) || !TelegramConnection.ValidPrivateChat(chat.Text))
            {
                MessageBox.Show(this, "Enter the full bot token and a numeric private user ID (5–20 digits). Do not enter a username, phone number or group ID.", "Check Telegram details", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
        };
        actions.Controls.Add(prepare); actions.Controls.Add(cancel); layout.Controls.Add(actions); Controls.Add(layout);
        AcceptButton = prepare; CancelButton = cancel; Shown += (_, _) => token.Focus();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { token.Clear(); chat.Clear(); }
        base.Dispose(disposing);
    }
}
