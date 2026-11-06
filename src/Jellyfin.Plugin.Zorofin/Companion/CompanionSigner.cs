using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Zorofin.Companion;

/// <summary>Signs bridge requests as described in docs/COMPANION_BRIDGE.md (scheme zorofin-bridge-v1).</summary>
public static class CompanionSigner
{
    /// <summary>The scheme name that starts every canonical string.</summary>
    public const string Scheme = "zorofin-bridge-v1";

    /// <summary>Builds the canonical string that is signed.</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="pathAndQuery">Path and query exactly as sent.</param>
    /// <param name="timestamp">Unix time in seconds.</param>
    /// <param name="nonce">32 lowercase hex characters.</param>
    /// <param name="userId">Viewer id in "N" format, or empty.</param>
    /// <param name="admin">Whether the viewer is an administrator.</param>
    /// <param name="body">Raw request body.</param>
    /// <returns>The canonical string.</returns>
    public static string Canonical(string method, string pathAndQuery, long timestamp, string nonce, string userId, bool admin, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(method);
        return string.Join(
            '\n',
            Scheme,
            method.ToUpperInvariant(),
            pathAndQuery,
            timestamp.ToString(CultureInfo.InvariantCulture),
            nonce,
            userId,
            admin ? "1" : "0",
            Convert.ToHexStringLower(SHA256.HashData(body)));
    }

    /// <summary>Signs a request.</summary>
    /// <param name="secret">Shared secret.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="pathAndQuery">Path and query exactly as sent.</param>
    /// <param name="timestamp">Unix time in seconds.</param>
    /// <param name="nonce">32 lowercase hex characters.</param>
    /// <param name="userId">Viewer id in "N" format, or empty.</param>
    /// <param name="admin">Whether the viewer is an administrator.</param>
    /// <param name="body">Raw request body.</param>
    /// <returns>Lowercase hex HMAC-SHA256.</returns>
    public static string Sign(string secret, string method, string pathAndQuery, long timestamp, string nonce, string userId, bool admin, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var canonical = Canonical(method, pathAndQuery, timestamp, nonce, userId, admin, body);
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical)));
    }
}
