using Microsoft.AspNetCore.Http;
using System.Text.Json.Nodes;

if (SetupGuide.Next(false, true, true, 6).Target != "#n8n-check" ||
    SetupGuide.Next(true, false, true, 6).Target != "#ai-setup" ||
    !SetupGuide.Next(true, true, false, 6).Title.Contains("adapter") ||
    SetupGuide.Next(true, true, true, 0).Target != "#source-setup" ||
    SetupGuide.Next(true, true, true, -1).Target != "#source-setup" ||
    SetupGuide.Next(true, true, true, 6).Target != "#database-setup" ||
    !SetupGuide.Next(true, true, true, 6).Detail.Contains("still need verification"))
    throw new Exception("Setup guide navigation must not imply completed credential checks.");
Console.WriteLine("PASS: 7 setup guide decisions and incomplete-state wording checks.");

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

foreach (var path in new[] { "/setup", "/setup/sources", "/setup/ai-profile" })
    if (SetupRequestBoundary.ReferrerPolicy(path) != "same-origin")
        throw new Exception("Setup forms must preserve a non-null Origin.");
foreach (var path in new[] { "/", "/setup-other", "/health/ready" })
    if (SetupRequestBoundary.ReferrerPolicy(path) != "no-referrer")
        throw new Exception("Non-setup referrer policy unexpectedly changed.");
var setupRequest = new DefaultHttpContext().Request;
setupRequest.Host = new HostString("127.0.0.1", 8085);
setupRequest.Headers.Origin = "http://127.0.0.1:8085";
if (!SetupRequestBoundary.IsSameOrigin(setupRequest)) throw new Exception("Valid setup Origin rejected.");
foreach (var origin in new[] { "", "null", "http://attacker.example:8085", "http://127.0.0.1:8086", "file://127.0.0.1:8085" })
{
    setupRequest.Headers.Origin = origin;
    if (SetupRequestBoundary.IsSameOrigin(setupRequest)) throw new Exception("Invalid setup Origin accepted.");
}
setupRequest.Headers.Origin = new Microsoft.Extensions.Primitives.StringValues(new[] { "http://127.0.0.1:8085", "http://attacker.example" });
if (SetupRequestBoundary.IsSameOrigin(setupRequest)) throw new Exception("Multiple Origins accepted.");
Console.WriteLine("PASS: 13 setup referrer/Origin checks.");
var setupRejection = (Microsoft.AspNetCore.Http.HttpResults.ContentHttpResult)SetupRequestBoundary.Rejected();
if (setupRejection.StatusCode != 403 || setupRejection.ContentType != "text/html; charset=utf-8" ||
    setupRejection.ResponseContent is null || !setupRejection.ResponseContent.Contains("href=\"/setup\"") ||
    !setupRejection.ResponseContent.Contains("setup_request_rejected"))
    throw new Exception("Setup rejection must remain 403 with safe recovery guidance.");
Console.WriteLine("PASS: setup rejection status/content/recovery contract.");

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
