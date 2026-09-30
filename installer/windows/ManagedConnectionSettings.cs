using System.Globalization;
using System.Text.RegularExpressions;

namespace CtiInstaller;

// Narrow input contract for the forthcoming installer-side connection action.
// Never send credentials based on an arbitrary address from the environment file.
internal sealed class ManagedConnectionSettings
{
    internal Uri Dashboard { get; }
    internal string ApplicationPassword { get; }
    private ManagedConnectionSettings(int port, string password)
    {
        Dashboard = new Uri(SetupNavigation.Url(port, false));
        ApplicationPassword = password;
    }

    public override string ToString() => "Managed CTI connection settings (credentials hidden)";

    internal static ManagedConnectionSettings Parse(string contents)
    {
        if (contents.Length > 65536 || contents.Contains('\0')) throw Invalid();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in contents.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator < 1 || !fields.TryAdd(line[..separator], line[(separator + 1)..])) throw Invalid();
        }
        if (fields.GetValueOrDefault("CTI_MANAGED_N8N") != "true" ||
            fields.GetValueOrDefault("CTI_DASHBOARD_BIND") != "127.0.0.1" ||
            fields.GetValueOrDefault("CTI_N8N_API_URL") != "http://cti-n8n:5678/api/v1") throw Invalid();
        if (!int.TryParse(fields.GetValueOrDefault("CTI_DASHBOARD_PORT"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535) throw Invalid();
        var password = fields.GetValueOrDefault("CTI_APP_PASSWORD") ?? "";
        // Only the installer's generated unquoted base64url secret is supported.
        // Custom environment syntax must not be guessed or expanded.
        if (!Regex.IsMatch(password, "\\A[A-Za-z0-9_-]{43}\\z", RegexOptions.CultureInvariant)) throw Invalid();
        return new ManagedConnectionSettings(port, password);
    }

    private static InvalidDataException Invalid() => new(
        "Automatic connection requires an unchanged local managed CTI configuration. No credentials were sent. Use manual setup or repair the installation settings.");
}
