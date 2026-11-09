using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AnimeRoll.Server.Api.Data;
using AnimeRoll.Server.Api.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AnimeRoll.Server.Api.Auth;

public sealed class TokenService(IOptions<AnimeRollOptions> options)
{
    public const string Issuer = "animeroll";
    private readonly AnimeRollOptions _o = options.Value;

    public static SymmetricSecurityKey SigningKey(string key) => new(Encoding.UTF8.GetBytes(key));

    public string CreateAccessToken(User user)
    {
        var creds = new SigningCredentials(SigningKey(_o.Jwt.Key), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Issuer,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
            ],
            expires: DateTime.UtcNow.AddMinutes(_o.Jwt.AccessTokenMinutes),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// Short signed token for media URLs (AVPlayer/ExoPlayer/&lt;video&gt; cannot always send headers).
    /// Format: {userId}.{episodeId}.{expiryUnix}.{hmac}
    public string CreateStreamToken(Guid userId, int episodeId)
    {
        var exp = DateTimeOffset.UtcNow.AddHours(_o.Jwt.StreamTokenHours).ToUnixTimeSeconds();
        var payload = $"{userId:N}.{episodeId}.{exp}";
        return $"{payload}.{Sign(payload)}";
    }

    public Guid? ValidateStreamToken(string? token, int episodeId)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var parts = token.Split('.');
        if (parts.Length != 4) return null;
        var payload = $"{parts[0]}.{parts[1]}.{parts[2]}";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(payload)), Encoding.ASCII.GetBytes(parts[3])))
            return null;
        if (parts[1] != episodeId.ToString()) return null;
        if (!long.TryParse(parts[2], out var exp) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return null;
        return Guid.TryParseExact(parts[0], "N", out var id) ? id : null;
    }

    /// Signed opaque state for the OAuth round trip (AniList, MyAnimeList). The optional tag says who is asking ("web").
    public string CreateState(Guid userId, string? tag = null)
    {
        var payload = $"{userId:N}.{DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()}" + (tag is null ? "" : $".{tag}");
        return $"{payload}.{Sign(payload)}";
    }

    public Guid? ValidateState(string? state) => ReadState(state)?.UserId;
    public string? StateTag(string? state) => ReadState(state)?.Tag;

    private (Guid UserId, string? Tag)? ReadState(string? state)
    {
        var parts = state?.Split('.');
        if (parts is not { Length: 3 or 4 }) return null;
        var payload = string.Join('.', parts[..^1]);
        if (Sign(payload) != parts[^1]) return null;
        if (!long.TryParse(parts[1], out var exp) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return null;
        return Guid.TryParseExact(parts[0], "N", out var id) ? (id, parts.Length == 4 ? parts[2] : null) : null;
    }

    /// PKCE code verifier for the MyAnimeList round trip, derived from the signed state so nothing is stored.
    public string PkceVerifier(string state) => Sign("pkce:" + state);

    private string Sign(string payload)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(_o.Jwt.Key));
        return Base64UrlEncoder.Encode(h.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }
}

public static class ClaimsExtensions
{
    public static Guid UserId(this ClaimsPrincipal p) =>
        Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier) ?? p.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
}
