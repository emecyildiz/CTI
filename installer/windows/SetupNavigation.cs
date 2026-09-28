namespace CtiInstaller;

internal static class SetupNavigation
{
    internal static string Url(int port, bool setup)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return $"http://127.0.0.1:{port}" + (setup ? "/setup" : "");
    }

    // Package presence is not proof that n8n credentials/workflows are ready.
    // Fresh installs guide users into setup; repair/update keep a separate setup link.
    internal static string PrimaryLabel(bool fresh) => fresh ? "Continue setup" : "Open dashboard";
}
