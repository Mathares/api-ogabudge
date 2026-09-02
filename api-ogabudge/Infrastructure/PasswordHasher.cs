using System.Security.Cryptography;
using System.Text;

namespace OGABudget.Api.Infrastructure;

/// <summary>
/// Hachage des mots de passe en PBKDF2-HMAC-SHA256, sans dépendance externe.
/// Format stocké : <c>pbkdf2$sha256$&lt;iterations&gt;$&lt;sel b64&gt;$&lt;empreinte b64&gt;</c>
/// — le nombre d'itérations est embarqué pour pouvoir le relever plus tard
/// sans invalider les mots de passe existants.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000;   // recommandation OWASP 2023 pour PBKDF2-SHA256
    private const int TailleSel = 16;
    private const int TailleCle = 32;

    public static string Hacher(string motDePasse)
    {
        var sel = RandomNumberGenerator.GetBytes(TailleSel);
        var cle = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(motDePasse), sel, Iterations, HashAlgorithmName.SHA256, TailleCle);

        return $"pbkdf2$sha256${Iterations}${Convert.ToBase64String(sel)}${Convert.ToBase64String(cle)}";
    }

    public static bool Verifier(string motDePasse, string empreinteStockee)
    {
        if (string.IsNullOrWhiteSpace(empreinteStockee)) return false;

        var parties = empreinteStockee.Split('$');
        if (parties.Length != 5 || parties[0] != "pbkdf2" || parties[1] != "sha256") return false;
        if (!int.TryParse(parties[2], out var iterations) || iterations <= 0) return false;

        byte[] sel, attendu;
        try
        {
            sel = Convert.FromBase64String(parties[3]);
            attendu = Convert.FromBase64String(parties[4]);
        }
        catch (FormatException) { return false; }

        var calcule = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(motDePasse), sel, iterations, HashAlgorithmName.SHA256, attendu.Length);

        // Comparaison à temps constant : ne divulgue pas le nombre d'octets corrects.
        return CryptographicOperations.FixedTimeEquals(calcule, attendu);
    }
}
