using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Produces the identifier used to attribute library database rows to a provider.
/// </summary>
/// <remarks>
/// <para>
/// The identifier is derived from the base URL alone, deliberately leaving out the position of the
/// provider in the configuration. Rows survive reordering, disabling and removal of other
/// providers; only changing the URL of a provider detaches its rows, which is a rare and
/// deliberate act. Including the index would have made a simple drag of a row in the settings page
/// orphan an entire library.
/// </para>
/// <para>
/// The hash is a naming device, not a security primitive, but SHA256 is used anyway so that the
/// file needs no analyzer suppression.
/// </para>
/// </remarks>
public static class ProviderIdentity
{
    /// <summary>
    /// Computes the identifier of a provider.
    /// </summary>
    /// <param name="baseUrl">The provider base URL.</param>
    /// <returns>The identifier, stable for a given base URL.</returns>
    public static string Compute(string? baseUrl)
    {
        string normalized = StrmUrlParser.NormalizeBaseUrl(baseUrl);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }
}
