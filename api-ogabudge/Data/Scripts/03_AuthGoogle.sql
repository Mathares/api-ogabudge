-- ═══════════════════════════════════════════════════════════════════════════
--  Auth Google — colonnes provider / google_sub, mot de passe optionnel
--  psql -U postgres -d dbogabudge -f 03_AuthGoogle.sql
--  Idempotent.
-- ═══════════════════════════════════════════════════════════════════════════

ALTER TABLE utilisateurs
    ALTER COLUMN mot_de_passe_hash DROP NOT NULL;

ALTER TABLE utilisateurs
    ADD COLUMN IF NOT EXISTS auth_provider text NOT NULL DEFAULT 'local';

ALTER TABLE utilisateurs
    ADD COLUMN IF NOT EXISTS google_sub text;

-- Un compte Google = un seul utilisateur.
CREATE UNIQUE INDEX IF NOT EXISTS uq_utilisateurs_google_sub
    ON utilisateurs (google_sub)
    WHERE google_sub IS NOT NULL;

COMMENT ON COLUMN utilisateurs.auth_provider IS
    'local | google | local+google (compte e-mail lié à Google)';
COMMENT ON COLUMN utilisateurs.google_sub IS
    'Subject Google (sub du jeton ID), unique si présent.';
