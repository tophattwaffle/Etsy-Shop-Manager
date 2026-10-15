using System.Security.Cryptography;
using System.Text;

namespace EtsyShopManager.Etsy;

/// <summary>PKCE (RFC 7636) helpers. Etsy requires S256.</summary>
public static class Pkce
{
    /// <summary>A 43–128 character verifier from the unreserved character set.</summary>
    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(48)); // 64 chars

    public static string CreateChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(24));

    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
