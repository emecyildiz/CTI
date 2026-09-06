using Microsoft.AspNetCore.Http;
using System.Text.Json.Nodes;

var allowed = new[]
{
    "localhost", "LOCALHOST", "localhost:8080", "localhost:18080",
    "127.0.0.1", "127.0.0.1:8080", "127.0.0.1:18080",
    "[::1]", "[::1]:8080", "[::1]:18080"
};
var rejected = new[]
{
    "", "attacker.example", "attacker.example:8080",
    "localhost.attacker.example:8080", "127.0.0.1.attacker.example:8080",
    "localhost@attacker.example:8080", "0.0.0.0:8080", "192.168.1.20:8080",
    "[::]:8080", "[2001:db8::1]:8080", "dashboard.example:8080"
};

foreach (var host in allowed)
{
    if (!LocalRequestBoundary.IsAllowedHost(new HostString(host)))
        throw new InvalidOperationException($"Expected loopback host to be allowed: {host}");
}
foreach (var host in rejected)
{
    if (LocalRequestBoundary.IsAllowedHost(new HostString(host)))
        throw new InvalidOperationException($"Expected non-loopback host to be rejected: {host}");
}

// DNS rebinding keeps the attacker's Host/Origin while its address changes to
// loopback. Neither a matching Origin nor a forwarded loopback host is trusted.
var reboundRequest = new DefaultHttpContext().Request;
reboundRequest.Host = new HostString("attacker.example", 8080);
reboundRequest.Headers.Origin = "http://attacker.example:8080";
reboundRequest.Headers["X-Forwarded-Host"] = "localhost:8080";
if (LocalRequestBoundary.IsAllowedHost(reboundRequest.Host))
    throw new InvalidOperationException("DNS-rebound Host was accepted.");

Console.WriteLine($"PASS: {allowed.Length + rejected.Length + 1} local dashboard Host boundary checks.");

var validUrls = new[] { "https://hooks.example.com/", "https://Hooks.Example.com:8443/n8n/v1/", "https://hooks.example.com/a%20b/" };
var invalidUrls = new[] { "https://10.0.0.5/", "https://192.168.1.20/", "https://[::]/", "https://[::1]/", "https://127.0.0.1/",
    "https://0.0.0.0/", "https://169.254.169.254/", "https://hooks.localhost/", "https://hooks.local/", "https://hooks.internal/",
    "http://hooks.example.com/", "https://user@hooks.example.com/", "https://hooks.example.com/?x=1", "https://hooks.example.com/#x",
    "https://hooks.example.com/\nOTHER=value", "https://hooks.example.com/\\nOTHER=value", "https://hooks.example.com/$SECRET",
    "https://hooks.example.com:0/", "https://hooks.example.com:65536/", "https://hooks.example.com/bad%2x" };
foreach (var url in validUrls)
    if (!TelegramSetupBoundary.IsPublicWebhookUrl(url)) throw new Exception($"Valid webhook rejected: {url}");
foreach (var url in invalidUrls)
    if (TelegramSetupBoundary.IsPublicWebhookUrl(url)) throw new Exception($"Invalid webhook accepted: {url}");
Console.WriteLine($"PASS: {validUrls.Length + invalidUrls.Length} webhook boundary checks.");

var template = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cti-telegram-query.json")))!.AsObject();
JsonObject Guard(JsonObject workflow) => workflow["nodes"]!.AsArray().OfType<JsonObject>().Single(n => n["name"]!.ToString() == "Authorize and Parse Request");
if (!TelegramSetupBoundary.IsReviewedQuery(template, false, out _) || TelegramSetupBoundary.IsReviewedQuery(template, true, out _))
    throw new Exception("Unconfigured template contract failed.");
Guard(template)["parameters"]!["jsCode"] = Guard(template)["parameters"]!["jsCode"]!.ToString().Replace("__CTI_TELEGRAM_ALLOWED_ID__", "123456789");
if (!TelegramSetupBoundary.IsReviewedQuery(template, true, out var id) || id != "123456789") throw new Exception("Configured template rejected.");
var mutationCount = 0;
void RejectMutation(Action<JsonObject> mutate)
{
    var changed = template.DeepClone().AsObject();
    mutate(changed);
    if (TelegramSetupBoundary.IsReviewedQuery(changed, true, out _)) throw new Exception($"Unsafe workflow mutation accepted: {mutationCount}");
    mutationCount++;
}
RejectMutation(w => Guard(w)["parameters"]!["jsCode"] = Guard(w)["parameters"]!["jsCode"]!.ToString().Replace("if (fromId", "// if (fromId"));
RejectMutation(w => Guard(w)["parameters"]!["jsCode"] = "return $input.all();\n" + Guard(w)["parameters"]!["jsCode"]!.ToString());
RejectMutation(w => Guard(w)["disabled"] = true);
RejectMutation(w => Guard(w)["alwaysOutputData"] = true);
RejectMutation(w => Guard(w)["continueOnFail"] = true);
RejectMutation(w => Guard(w)["onError"] = "continueRegularOutput");
RejectMutation(w => Guard(w)["parameters"]!["mode"] = "runOnceForEachItem");
RejectMutation(w => w["connections"]!["Private CTI Telegram Trigger"]!["main"]![0]![0]!["node"] = "Lookup Recent CTI Articles");
RejectMutation(w => w["connections"]!["Private CTI Telegram Trigger"]!["main"]![0]!.AsArray().Add(new JsonObject { ["node"] = "Lookup Recent CTI Articles", ["type"] = "main", ["index"] = 0 }));
RejectMutation(w => w["nodes"]!.AsArray().Add(new JsonObject { ["name"] = "Unreviewed Trigger", ["type"] = "n8n-nodes-base.webhook" }));
RejectMutation(w => Guard(w)["type"] = "n8n-nodes-base.noOp");
RejectMutation(w => w["nodes"]!.AsArray().Remove(Guard(w)));
Guard(template)["parameters"]!["jsCode"] = Guard(template)["parameters"]!["jsCode"]!.ToString().Replace("\n", "\r\n");
Guard(template)["disabled"] = false;
if (!TelegramSetupBoundary.IsReviewedQuery(template, true, out _)) throw new Exception("CRLF or explicit disabled=false was rejected.");
Console.WriteLine($"PASS: reviewed Telegram template, configured ID, CRLF and {mutationCount} unsafe workflow mutations.");
