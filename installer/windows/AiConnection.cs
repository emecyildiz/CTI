namespace CtiInstaller;

internal static class AiConnection
{
    internal static bool ValidKey(string key) => key.Length is >= 20 and <= 512 && key.All(c => c is >= '!' and <= '~');
    internal static bool Prepared(int exitCode, string output) => exitCode == 0 && output.Trim() == "CTI_MANAGED_AI_RESULT:v1:prepared";

    // Presentation gate only. The host bridge independently checks real ownership/receipt.
    internal static bool CanOffer(string contents)
    {
        if (contents.Length > 65536 || contents.Contains('\0')) return false;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in contents.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var separator = line.IndexOf('=');
            if (separator < 1) continue;
            var name = line[..separator];
            if (name is not ("CTI_MANAGED_N8N" or "CTI_DATABASE_BOOTSTRAP_RECORDED")) continue;
            if (!fields.TryAdd(name, line[(separator + 1)..])) return false;
        }
        return fields.GetValueOrDefault("CTI_MANAGED_N8N") == "true" && fields.GetValueOrDefault("CTI_DATABASE_BOOTSTRAP_RECORDED") == "true";
    }
}
