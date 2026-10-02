using System.Text.Json;

namespace CtiInstaller;

internal sealed record ReadinessResult(bool Drafts, bool Database, bool ErrorRouting, bool? Ai, bool? Telegram, bool Ready)
{
    internal static bool TryRead(int exitCode, string output, bool includeAi, bool includeTelegram, out ReadinessResult? result)
    {
        result = null;
        const string prefix = "CTI_MANAGED_AUDIT_RESULT:v1:";
        var text = output.Trim();
        if (exitCode != 0 || text.Length > 1024 || !text.StartsWith(prefix, StringComparison.Ordinal)) return false;
        try
        {
            using var doc = JsonDocument.Parse(text[prefix.Length..]);
            var root = doc.RootElement;
            var names = root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
            if (!names.SequenceEqual(new[] { "ai", "database", "drafts", "errorRouting", "ready", "schema", "telegram" }) || root.GetProperty("schema").GetInt32() != 1) return false;
            bool? Optional(string name, bool included)
            {
                var value = root.GetProperty(name);
                if (included) return value.GetBoolean();
                if (value.ValueKind != JsonValueKind.Null) throw new InvalidDataException();
                return null;
            }
            var parsed = new ReadinessResult(root.GetProperty("drafts").GetBoolean(), root.GetProperty("database").GetBoolean(),
                root.GetProperty("errorRouting").GetBoolean(), Optional("ai", includeAi), Optional("telegram", includeTelegram), root.GetProperty("ready").GetBoolean());
            if (parsed.Ready != (parsed.Drafts && parsed.Database && parsed.ErrorRouting && parsed.Ai != false && parsed.Telegram != false)) return false;
            result = parsed; return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or InvalidDataException or OverflowException) { return false; }
    }

    internal string Description()
    {
        static string State(bool? state) => state is null ? "Not included" : state.Value ? "Prepared" : "Needs attention";
        return $"Reviewed tasks (disabled): {State(Drafts)}\nDatabase bindings: {State(Database)}\nError routing: {State(ErrorRouting)}\nAI bindings: {State(Ai)}\nTelegram private-chat bindings: {State(Telegram)}\n\n"
            + (Ready ? "Selected connections are structurally prepared. " : "Some preparation is missing or differs from the reviewed package. ")
            + "This is a snapshot, not a live provider/database connection test. It does not verify incoming HTTPS reachability. No tasks were enabled or messages sent. Review sources and enable tasks one at a time only when you are ready.";
    }
}
