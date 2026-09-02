using Npgsql;
using OGABudget.Api.Infrastructure;
using OGABudget.Api.Models;

namespace OGABudget.Api.Services;

/// <summary>Inscription, connexion, rotation des sessions et profil utilisateur.</summary>
public class AuthService
{
    private readonly NpgsqlDataSource _db;
    private readonly TokenService _tokens;
    private readonly ILogger<AuthService> _logger;

    private const string ColonnesUtilisateur =
        "id, email::text, nom_complet, telephone, devise::text, locale, fuseau_horaire, " +
        "avatar_url, jour_debut_mois, email_verifie, date_creation";

    public AuthService(NpgsqlDataSource db, TokenService tokens, ILogger<AuthService> logger)
    {
        _db = db;
        _tokens = tokens;
        _logger = logger;
    }

    // ─── Inscription ────────────────────────────────────────────────────────

    /// <returns>La session créée, ou <c>null</c> si l'e-mail est déjà pris.</returns>
    public async Task<SessionDto?> InscrireAsync(InscriptionRequest req, string? ip, CancellationToken ct)
    {
        var devise = string.IsNullOrWhiteSpace(req.Devise) ? "XOF" : req.Devise.Trim().ToUpperInvariant();

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        Guid id;
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO utilisateurs (email, mot_de_passe_hash, nom_complet, telephone, devise)
            VALUES (@email, @hash, @nom, @tel, @devise)
            ON CONFLICT (email) DO NOTHING
            RETURNING id
            """, conn, tx))
        {
            cmd.Ajouter("email", req.Email.Trim());
            cmd.Ajouter("hash", PasswordHasher.Hacher(req.MotDePasse));
            cmd.Ajouter("nom", req.NomComplet.Trim());
            cmd.Ajouter("tel", string.IsNullOrWhiteSpace(req.Telephone) ? null : req.Telephone.Trim());
            cmd.Ajouter("devise", devise);

            var resultat = await cmd.ExecuteScalarAsync(ct);
            if (resultat is not Guid nouvelId) return null;   // e-mail déjà inscrit
            id = nouvelId;
        }

        // Un compte vierge n'est pas exploitable : on copie les catégories du modèle
        // et on crée un portefeuille « Espèces » pour que la première dépense soit saisissable.
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO categories (utilisateur_id, nom, type, icone, couleur, systeme, ordre)
            SELECT @uid, nom, type, icone, couleur, true, ordre FROM categories_modele
            """, conn, tx))
        {
            cmd.Ajouter("uid", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO comptes (utilisateur_id, nom, type, devise, icone, couleur)
            VALUES (@uid, 'Espèces', 'especes', @devise, 'wallet', '#1a2b5a')
            """, conn, tx))
        {
            cmd.Ajouter("uid", id);
            cmd.Ajouter("devise", devise);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var utilisateur = await LireUtilisateurAsync(conn, tx, id, ct)
            ?? throw new InvalidOperationException("Utilisateur introuvable après création.");

        var session = await OuvrirSessionAsync(conn, tx, utilisateur, req.Appareil, ip, ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation("Nouvel utilisateur inscrit : {Id}", id);
        return session;
    }

    // ─── Connexion ──────────────────────────────────────────────────────────

    public async Task<SessionDto?> ConnecterAsync(ConnexionRequest req, string? ip, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);

        Guid id;
        string hash;
        bool actif;
        await using (var cmd = new NpgsqlCommand(
            "SELECT id, mot_de_passe_hash, actif FROM utilisateurs WHERE email = @email LIMIT 1", conn))
        {
            cmd.Ajouter("email", req.Email.Trim());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                // Coût de hachage payé même sans compte : la durée de réponse ne révèle pas
                // si l'adresse existe.
                PasswordHasher.Verifier(req.MotDePasse, PasswordHasher.Hacher("factice"));
                return null;
            }
            id = reader.GetGuid(0);
            hash = reader.GetString(1);
            actif = reader.GetBoolean(2);
        }

        if (!actif || !PasswordHasher.Verifier(req.MotDePasse, hash)) return null;

        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var cmd = new NpgsqlCommand(
            "UPDATE utilisateurs SET derniere_connexion = now() WHERE id = @id", conn, tx))
        {
            cmd.Ajouter("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var utilisateur = await LireUtilisateurAsync(conn, tx, id, ct);
        if (utilisateur == null) return null;

        var session = await OuvrirSessionAsync(conn, tx, utilisateur, req.Appareil, ip, ct);
        await tx.CommitAsync(ct);
        return session;
    }

    // ─── Rotation du refresh token ──────────────────────────────────────────

    /// <summary>
    /// Échange un refresh token contre une nouvelle paire. Le jeton présenté est révoqué :
    /// s'il est rejoué (vol de jeton), la tentative échoue.
    /// </summary>
    public async Task<SessionDto?> RafraichirAsync(string refreshToken, string? ip, CancellationToken ct)
    {
        var empreinte = TokenService.Empreinte(refreshToken);

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        Guid tokenId, utilisateurId;
        string? appareil;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT id, utilisateur_id, appareil
            FROM refresh_tokens
            WHERE token_hash = @hash AND revoque_le IS NULL AND expire_le > now()
            FOR UPDATE
            """, conn, tx))
        {
            cmd.Ajouter("hash", empreinte);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            tokenId = reader.GetGuid(0);
            utilisateurId = reader.GetGuid(1);
            appareil = reader.Texte(2);
        }

        var utilisateur = await LireUtilisateurAsync(conn, tx, utilisateurId, ct);
        if (utilisateur == null) return null;

        var session = await OuvrirSessionAsync(conn, tx, utilisateur, appareil, ip, ct, remplace: tokenId);
        await tx.CommitAsync(ct);
        return session;
    }

    /// <summary>Déconnecte l'appareil courant (un seul jeton) ou tous les appareils.</summary>
    public async Task DeconnecterAsync(Guid utilisateurId, string? refreshToken, bool tousLesAppareils,
                                       CancellationToken ct)
    {
        var sql = tousLesAppareils
            ? "UPDATE refresh_tokens SET revoque_le = now() WHERE utilisateur_id = @uid AND revoque_le IS NULL"
            : "UPDATE refresh_tokens SET revoque_le = now() WHERE utilisateur_id = @uid AND token_hash = @hash AND revoque_le IS NULL";

        await using var cmd = _db.CreateCommand(sql);
        cmd.Ajouter("uid", utilisateurId);
        if (!tousLesAppareils) cmd.Ajouter("hash", TokenService.Empreinte(refreshToken ?? ""));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ─── Profil ─────────────────────────────────────────────────────────────

    public async Task<UtilisateurDto?> ObtenirProfilAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await LireUtilisateurAsync(conn, null, id, ct);
    }

    public async Task<UtilisateurDto?> MettreAJourProfilAsync(Guid id, MajProfilRequest req, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE utilisateurs SET
                nom_complet     = COALESCE(@nom, nom_complet),
                telephone       = COALESCE(@tel, telephone),
                devise          = COALESCE(@devise, devise),
                locale          = COALESCE(@locale, locale),
                fuseau_horaire  = COALESCE(@fuseau, fuseau_horaire),
                avatar_url      = COALESCE(@avatar, avatar_url),
                jour_debut_mois = COALESCE(@jour, jour_debut_mois)
            WHERE id = @id
            """, conn))
        {
            cmd.Ajouter("id", id);
            cmd.Ajouter("nom", Vide(req.NomComplet));
            cmd.Ajouter("tel", Vide(req.Telephone));
            cmd.Ajouter("devise", Vide(req.Devise)?.ToUpperInvariant());
            cmd.Ajouter("locale", Vide(req.Locale));
            cmd.Ajouter("fuseau", Vide(req.FuseauHoraire));
            cmd.Ajouter("avatar", Vide(req.AvatarUrl));
            cmd.Ajouter("jour", req.JourDebutMois);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return null;
        }

        return await LireUtilisateurAsync(conn, null, id, ct);
    }

    /// <returns><c>false</c> si l'ancien mot de passe est incorrect.</returns>
    public async Task<bool> ChangerMotDePasseAsync(Guid id, ChangerMotDePasseRequest req, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);

        string hash;
        await using (var cmd = new NpgsqlCommand(
            "SELECT mot_de_passe_hash FROM utilisateurs WHERE id = @id", conn))
        {
            cmd.Ajouter("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string h) return false;
            hash = h;
        }

        if (!PasswordHasher.Verifier(req.AncienMotDePasse, hash)) return false;

        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            "UPDATE utilisateurs SET mot_de_passe_hash = @hash WHERE id = @id", conn, tx))
        {
            cmd.Ajouter("id", id);
            cmd.Ajouter("hash", PasswordHasher.Hacher(req.NouveauMotDePasse));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Changer de mot de passe doit déconnecter les appareils volés.
        await using (var cmd = new NpgsqlCommand(
            "UPDATE refresh_tokens SET revoque_le = now() WHERE utilisateur_id = @uid AND revoque_le IS NULL",
            conn, tx))
        {
            cmd.Ajouter("uid", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>Suppression définitive du compte et, en cascade, de toutes ses données.</summary>
    public async Task<bool> SupprimerCompteAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM utilisateurs WHERE id = @id");
        cmd.Ajouter("id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ─── Interne ────────────────────────────────────────────────────────────

    private async Task<SessionDto> OuvrirSessionAsync(NpgsqlConnection conn, NpgsqlTransaction? tx,
                                                      UtilisateurDto utilisateur, string? appareil, string? ip,
                                                      CancellationToken ct, Guid? remplace = null)
    {
        var refresh = TokenService.CreerRefreshToken();

        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO refresh_tokens (utilisateur_id, token_hash, appareil, adresse_ip, expire_le)
            VALUES (@uid, @hash, @appareil, @ip, now() + make_interval(days => @jours))
            RETURNING id
            """, conn, tx))
        {
            cmd.Ajouter("uid", utilisateur.Id);
            cmd.Ajouter("hash", TokenService.Empreinte(refresh));
            cmd.Ajouter("appareil", Vide(appareil));
            cmd.Ajouter("ip", Vide(ip));
            cmd.Ajouter("jours", _tokens.DureeRefreshJours);
            var nouveauId = (Guid)(await cmd.ExecuteScalarAsync(ct))!;

            if (remplace is Guid ancien)
            {
                await using var revoke = new NpgsqlCommand(
                    "UPDATE refresh_tokens SET revoque_le = now(), remplace_par = @nouveau WHERE id = @ancien",
                    conn, tx);
                revoke.Ajouter("nouveau", nouveauId);
                revoke.Ajouter("ancien", ancien);
                await revoke.ExecuteNonQueryAsync(ct);
            }
        }

        var (access, expiration) = _tokens.CreerAccessToken(utilisateur);
        return new SessionDto
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpireLe = expiration,
            Utilisateur = utilisateur
        };
    }

    private static async Task<UtilisateurDto?> LireUtilisateurAsync(NpgsqlConnection conn, NpgsqlTransaction? tx,
                                                                   Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT {ColonnesUtilisateur} FROM utilisateurs WHERE id = @id AND actif = true LIMIT 1", conn, tx);
        cmd.Ajouter("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new UtilisateurDto
        {
            Id = reader.GetGuid(0),
            Email = reader.GetString(1),
            NomComplet = reader.GetString(2),
            Telephone = reader.Texte(3),
            Devise = reader.GetString(4).Trim(),
            Locale = reader.GetString(5),
            FuseauHoraire = reader.GetString(6),
            AvatarUrl = reader.Texte(7),
            JourDebutMois = reader.Entier(8),
            EmailVerifie = reader.GetBoolean(9),
            DateCreation = reader.Horodatage(10)
        };
    }

    private static string? Vide(string? valeur) => string.IsNullOrWhiteSpace(valeur) ? null : valeur.Trim();
}
