using Microsoft.AspNetCore.Http;

internal static class LocalRequestBoundary
{
    // A loopback socket binding alone does not prevent browser DNS rebinding.
    // Validate the actual Host header, not client-controlled forwarding headers.
    internal static bool IsAllowedHost(HostString authority)
    {
        var host = authority.Host;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
               string.Equals(host, "[::1]", StringComparison.Ordinal);
    }
}
