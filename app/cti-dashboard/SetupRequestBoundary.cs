using Microsoft.AspNetCore.Http;

internal static class SetupRequestBoundary
{
    // no-referrer can serialize Origin as null for native HTML form POSTs.
    // Preserve same-origin form checks without leaking referrers off-site.
    internal static string ReferrerPolicy(PathString path) =>
        path.StartsWithSegments("/setup") ? "same-origin" : "no-referrer";

    internal static bool IsSameOrigin(HttpRequest request)
    {
        if (request.Headers.Origin.Count != 1) return false;
        var origin = request.Headers.Origin[0];
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    internal static IResult Rejected() => Results.Content(
        """
        <!doctype html><html lang="en"><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Setup request not saved - CTI</title>
        <main><h1>Your setup changes were not saved</h1>
        <p>The request could not pass the setup security check. Your setup cookie
        may have expired, or the page was opened from a different address.</p>
        <p>Use your browser's Back button to review non-secret values, then open
        a fresh setup page at the same dashboard address and retry. Allow cookies
        for this local dashboard. Secrets may need to be entered again.</p>
        <p><a href="/setup">Open a fresh setup page</a></p>
        <p>Error: setup_request_rejected (403). No changes from this request were applied.</p>
        </main></html>
        """, "text/html; charset=utf-8", statusCode: StatusCodes.Status403Forbidden);
}
