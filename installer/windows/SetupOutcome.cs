namespace CtiInstaller;

internal enum ConnectionOutcome { Unknown, Prepared, Preserved, External, Skipped }

internal static class SetupOutcome
{
    private const string Prefix = "CTI_SETUP_RESULT:v1:";

    // A presentation-only result from setup.ps1, never authorization or readiness proof.
    internal static ConnectionOutcome Read(string output)
    {
        var records = output.Split('\n').Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
        if (records.Length != 1) return ConnectionOutcome.Unknown;
        return records[0][Prefix.Length..] switch
        {
            "prepared" => ConnectionOutcome.Prepared,
            "preserved" => ConnectionOutcome.Preserved,
            "external" => ConnectionOutcome.External,
            "skipped" => ConnectionOutcome.Skipped,
            _ => ConnectionOutcome.Unknown
        };
    }

    internal static string Description(ConnectionOutcome outcome) => outcome switch
    {
        ConnectionOutcome.Prepared => "Database connection prepared automatically. You do not need to copy its password or an n8n API key for this step.",
        ConnectionOutcome.Preserved => "Existing automation settings were kept. Review their connection from setup; they were not tested or replaced during installation.",
        ConnectionOutcome.External => "Your existing automation service still needs to be connected from setup.",
        ConnectionOutcome.Skipped => "Automation setup was skipped. Complete the connection from setup before starting collection.",
        _ => "The installer could not confirm the connection result. Review the connection from setup before starting collection."
    };
}
