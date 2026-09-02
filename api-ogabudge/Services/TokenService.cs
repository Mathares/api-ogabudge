using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using OGABudget.Api.Models;

namespace OGABudget.Api.Services;

/// <summary>
/// Émission des jetons. Le mobile garde un access token court en mémoire et un
/// refresh token long en stockage sécurisé (Keychain / EncryptedSharedPreferences).
/// </summary>
public class TokenService
{
    private readonly string _secret;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _dureeAccesMinutes;
    private readonly int _dureeRefreshJours;

    public TokenService(IConfiguration configuration)
    {
        _secret = configuration["Jwt:Secret"] ?? "";
        _issuer = configuration["Jwt:Issuer"] ?? "api-ogabudge";
        _audience = configuration["Jwt:Audience"] ?? "ogabudget-mobile";
        _dureeAccesMinutes = configuration.GetValue("Jwt:AccessTokenMinutes", 60);
        _dureeRefreshJours = configuration.GetValue("Jwt:RefreshTokenJours", 60);
    }

    public int DureeRefreshJours => _dureeRefreshJours;

    public (string token, DateTimeOffset expiration) CreerAccessToken(UtilisateurDto utilisateur)
    {
        if (string.IsNullOrWhiteSpace(_secret))
            throw new InvalidOperationException("Jwt:Secret n'est pas configuré.");

        var expiration = DateTimeOffset.UtcNow.AddMinutes(_dureeAccesMinutes);
        var cle = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, utilisateur.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, utilisateur.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("nom", utilisateur.NomComplet),
            new("devise", utilisateur.Devise),
        };

        var jwt = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiration.UtcDateTime,
            signingCredentials: new SigningCredentials(cle, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(jwt), expiration);
    }

    /// <summary>Jeton opaque de 512 bits. Seule son empreinte est conservée en base.</summary>
    public static string CreerRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
               .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Empreinte(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
