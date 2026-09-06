using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static class TelegramSetupBoundary
{
    private const string GuardName = "Authorize and Parse Request";
    private const string Placeholder = "__CTI_TELEGRAM_ALLOWED_ID__";
    // Fingerprint of the reviewed bundled guard after replacing only its private ID.
    private const string GuardHash = "4111e8444d2eace31175136ccbf7819a437287dd832fed6c325c3359ba221df0";
    private static readonly Regex IdAssignment = new(
        @"^const allowedId = '(?<id>__CTI_TELEGRAM_ALLOWED_ID__|[0-9]{5,20})';$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex WebhookPattern = new(
        @"\Ahttps://([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(:[0-9]{1,5})?(/[A-Za-z0-9._~!()*+,;=:%/-]*)?\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex InvalidEscape = new(@"%([^0-9A-Fa-f]|[0-9A-Fa-f]([^0-9A-Fa-f]|$)|$)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Dictionary<string, string> NodeTypes = new(StringComparer.Ordinal)
    {
        ["Private CTI Telegram Trigger"] = "n8n-nodes-base.telegramTrigger",
        [GuardName] = "n8n-nodes-base.code",
        ["Is Callback Query"] = "n8n-nodes-base.if",
        ["Answer Callback Query"] = "n8n-nodes-base.telegram",
        ["Lookup Recent CTI Articles"] = "n8n-nodes-base.postgres",
        ["Format Safe CTI Response"] = "n8n-nodes-base.code",
        ["Send Private CTI Response"] = "n8n-nodes-base.telegram"
    };

    internal static bool IsPublicWebhookUrl(string? value)
    {
        if (value is null || value.Length > 2048 || !WebhookPattern.IsMatch(value) || InvalidEscape.IsMatch(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Port < 1 ||
            uri.HostNameType != UriHostNameType.Dns || uri.Host.Length > 253 || uri.IsLoopback)
            return false;
        var host = uri.Host;
        return !host.All(c => char.IsAsciiDigit(c) || c == '.') &&
            !new[] { ".localhost", ".local", ".internal" }.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsReviewedQuery(JsonObject workflow, bool requireConfiguredId, [NotNullWhen(true)] out string? id)
    {
        id = null;
        if (workflow["nodes"] is not JsonArray nodes || nodes.Count != NodeTypes.Count ||
            !JsonNode.DeepEquals(workflow["connections"], ExpectedConnections())) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        JsonObject? guard = null;
        foreach (var item in nodes)
        {
            if (item is not JsonObject node || node["name"] is not JsonValue nameValue ||
                !nameValue.TryGetValue<string>(out var name) || !names.Add(name) ||
                !NodeTypes.TryGetValue(name, out var expectedType) || node["type"]?.ToString() != expectedType ||
                (node["disabled"] is not null && node["disabled"]?.ToString() != "false")) return false;
            if (name == GuardName) guard = node;
        }
        if (guard?["parameters"] is not JsonObject parameters || parameters["jsCode"] is not JsonValue code ||
            !code.TryGetValue<string>(out var source) || source.Length > 10000) return false;
        if (parameters["mode"] is not null && parameters["mode"]?.ToString() != "runOnceForAllItems") return false;
        foreach (var key in new[] { "continueOnFail", "alwaysOutputData", "executeOnce" })
            if (guard[key] is not null && guard[key]?.ToString() != "false") return false;
        if (guard["onError"] is not null && guard["onError"]?.ToString() != "stopWorkflow") return false;
        source = source.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var matches = IdAssignment.Matches(source);
        if (matches.Count != 1) return false;
        var candidate = matches[0].Groups["id"].Value;
        if (requireConfiguredId && candidate == Placeholder) return false;
        var normalized = IdAssignment.Replace(source, $"const allowedId = '{Placeholder}';", 1);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))), GuardHash,
                StringComparison.OrdinalIgnoreCase)) return false;
        id = candidate;
        return true;
    }

    private static JsonObject ExpectedConnections()
    {
        var result = new JsonObject();
        void Add(string from, params string[] branches)
        {
            var outputs = new JsonArray();
            foreach (var target in branches)
                outputs.Add(new JsonArray(new JsonObject { ["node"] = target, ["type"] = "main", ["index"] = 0 }));
            result[from] = new JsonObject { ["main"] = outputs };
        }
        Add("Private CTI Telegram Trigger", GuardName);
        Add(GuardName, "Is Callback Query");
        Add("Is Callback Query", "Answer Callback Query", "Lookup Recent CTI Articles");
        Add("Answer Callback Query", "Lookup Recent CTI Articles");
        Add("Lookup Recent CTI Articles", "Format Safe CTI Response");
        Add("Format Safe CTI Response", "Send Private CTI Response");
        return result;
    }
}
