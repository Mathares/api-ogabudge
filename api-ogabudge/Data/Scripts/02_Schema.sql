-- ═══════════════════════════════════════════════════════════════════════════
--  OGABudget — Schéma PostgreSQL
--  psql -U postgres -d dbogabudge -f 02_Schema.sql
--  Idempotent : peut être rejoué sans casser une base existante.
-- ═══════════════════════════════════════════════════════════════════════════

-- Aucune extension n'est requise : gen_random_uuid() est natif depuis PostgreSQL 13,
-- l'unicité des e-mails passe par un index sur lower(email) plutôt que par citext, et
-- l'index trigramme de recherche n'est créé que si pg_trgm est disponible (voir § 6).
-- Sur Azure Database for PostgreSQL, activer une extension suppose de l'ajouter au
-- paramètre serveur azure.extensions : ce schéma s'applique sans cette démarche.

-- ─── Types énumérés ────────────────────────────────────────────────────────
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'type_compte') THEN
        CREATE TYPE type_compte AS ENUM
            ('especes', 'banque', 'mobile_money', 'epargne', 'carte', 'credit', 'autre');
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'type_flux') THEN
        CREATE TYPE type_flux AS ENUM ('depense', 'revenu', 'transfert');
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'periode_budget') THEN
        CREATE TYPE periode_budget AS ENUM
            ('hebdomadaire', 'mensuelle', 'trimestrielle', 'annuelle');
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'frequence_recurrence') THEN
        CREATE TYPE frequence_recurrence AS ENUM
            ('quotidienne', 'hebdomadaire', 'mensuelle', 'trimestrielle', 'annuelle');
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'statut_objectif') THEN
        CREATE TYPE statut_objectif AS ENUM ('en_cours', 'atteint', 'abandonne');
    END IF;
END $$;

-- ─── Trigger générique de mise à jour de date_maj ──────────────────────────
CREATE OR REPLACE FUNCTION touch_date_maj() RETURNS trigger AS $$
BEGIN
    NEW.date_maj := now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- ═══════════════════════════════════════════════════════════════════════════
--  1. UTILISATEURS
-- ═══════════════════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS utilisateurs (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    email               text        NOT NULL,
    mot_de_passe_hash   text,                          -- NULL si compte Google pur
    nom_complet         text        NOT NULL,
    telephone           text,
    devise              char(3)     NOT NULL DEFAULT 'XOF',
    locale              text        NOT NULL DEFAULT 'fr-BF',
    fuseau_horaire      text        NOT NULL DEFAULT 'Africa/Ouagadougou',
    avatar_url          text,
    jour_debut_mois     smallint    NOT NULL DEFAULT 1,      -- mois budgétaire décalé (ex. paie le 25)
    email_verifie       boolean     NOT NULL DEFAULT false,
    actif               boolean     NOT NULL DEFAULT true,
    auth_provider       text        NOT NULL DEFAULT 'local', -- local | google | local+google
    google_sub          text,                              -- subject Google (unique)
    derniere_connexion  timestamptz,
    date_creation       timestamptz NOT NULL DEFAULT now(),
    date_maj            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_utilisateurs_jour_debut CHECK (jour_debut_mois BETWEEN 1 AND 28)
);

-- Unicité insensible à la casse : « Mathieu@x.bf » et « mathieu@x.bf » sont le même compte.
CREATE UNIQUE INDEX IF NOT EXISTS uq_utilisateurs_email ON utilisateurs (lower(email));
CREATE UNIQUE INDEX IF NOT EXISTS uq_utilisateurs_google_sub
    ON utilisateurs (google_sub) WHERE google_sub IS NOT NULL;

DROP TRIGGER IF EXISTS trg_utilisateurs_maj ON utilisateurs;
CREATE TRIGGER trg_utilisateurs_maj BEFORE UPDATE ON utilisateurs
    FOR EACH ROW EXECUTE FUNCTION touch_date_maj();

-- ═══════════════════════════════════════════════════════════════════════════
--  2. REFRESH TOKENS (sessions mobiles longues, rotation à chaque refresh)
-- ═══════════════════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS refresh_tokens (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    utilisateur_id  uuid        NOT NULL REFERENCES utilisateurs(id) ON DELETE CASCADE,
    token_hash      text        NOT NULL UNIQUE,   -- SHA-256 du token, jamais le token en clair
    appareil        text,                          -- « Samsung A14 · Android 14 »
    adresse_ip      text,
    expire_le       timestamptz NOT NULL,
    revoque_le      timestamptz,
    remplace_par    uuid REFERENCES refresh_tokens(id) ON DELETE SET NULL,
    date_creation   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_refresh_tokens_utilisateur ON refresh_tokens(utilisateur_id);
CREATE INDEX IF NOT EXISTS ix_refresh_tokens_expiration ON refresh_tokens(expire_le) WHERE revoque_le IS NULL;

-- ═══════════════════════════════════════════════════════════════════════════
--  3. COMPTES (portefeuille, banque, Orange Money, Moov Money, épargne…)
-- ═══════════════════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS comptes (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    utilisateur_id    uuid          NOT NULL REFERENCES utilisateurs(id) ON DELETE CASCADE,
    nom               text          NOT NULL,
    type              type_compte   NOT NULL DEFAULT 'especes',
    institution       text,                                   -- « Coris Bank », « Orange Money »
    numero_masque     text,                                   -- 4 derniers chiffres uniquement
    solde_initial     numeric(18,2) NOT NULL DEFAULT 0,
    devise            char(3)       NOT NULL DEFAULT 'XOF',
    couleur           text          NOT NULL DEFAULT '#1a2b5a',
    icone             text          NOT NULL DEFAULT 'wallet',
    inclus_dans_total boolean       NOT NULL DEFAULT true,
    archive           boolean       NOT NULL DEFAULT false,
    ordre             integer       NOT NULL DEFAULT 0,
    date_creation     timestamptz   NOT NULL DEFAULT now(),
    date_maj          timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT uq_comptes_nom UNIQUE (utilisateur_id, nom)
);

CREATE INDEX IF NOT EXISTS ix_comptes_utilisateur ON comptes(utilisateur_id) WHERE archive = false;

DROP TRIGGER IF EXISTS trg_comptes_maj ON comptes;
CREATE TRIGGER trg_comptes_maj BEFORE UPDATE ON comptes
    FOR EACH ROW EXECUTE FUNCTION touch_date_maj();

-- ═══════════════════════════════════════════════════════════════════════════
--  4. CATÉGORIES (hiérarchiques, propres à chaque utilisateur)
-- ═══════════════════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS categories (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    utilisateur_id uuid        NOT NULL REFERENCES utilisateurs(id) ON DELETE CASCADE,
    parent_id      uuid        REFERENCES categories(id) ON DELETE CASCADE,
    nom            text        NOT NULL,
    type           type_flux   NOT NULL,                      -- 'depense' ou 'revenu'
    icone          text        NOT NULL DEFAULT 'tag',
    couleur        text        NOT NULL DEFAULT '#085041',
    systeme        boolean     NOT NULL DEFAULT false,        -- issue du modèle, non supprimable
    archive        boolean     NOT NULL DEFAULT false,
    ordre          integer     NOT NULL DEFAULT 0,
    date_creation  timestamptz NOT NULL DEFAULT now(),
    date_maj       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_categories_type CHECK (type <> 'transfert')
);

-- Unicité du nom par type : deux index partiels, car UNIQUE traite NULL comme distinct
CREATE UNIQUE INDEX IF NOT EXISTS uq_categories_racine
    ON categories(utilisateur_id, type, lower(nom)) WHERE parent_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS uq_categories_enfant
    ON categories(utilisateur_id, type, parent_id, lower(nom)) WHERE parent_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_categories_utilisateur ON categories(utilisateur_id, type) WHERE archive = false;

DROP TRIGGER IF EXISTS trg_categories_maj ON categories;
CREATE TRIGGER trg_categories_maj BEFORE UPDATE ON categories
    FOR EACH ROW EXECUTE FUNCTION touch_date_maj();

-- Modèle de catégories copié dans le compte à l'inscription
CREATE TABLE IF NOT EXISTS categories_modele (
    id      serial PRIMARY KEY,
    nom     text      NOT NULL,
    type    type_flux NOT NULL,
    icone   text      NOT NULL DEFAULT 'tag',
    couleur text      NOT NULL DEFAULT '#085041',
    ordre   integer   NOT NULL DEFAULT 0,
    CONSTRAINT uq_categories_modele UNIQUE (type, nom)
);

-- ═══════════════════════════════════════════════════════════════════════════
--  5. RÉCURRENCES (salaire, loyer, abonnements…)
-- ═══════════════════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS recurrences (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    utilisateur_id     uuid                 NOT NULL REFERENCES utilisateurs(id) ON DELETE CASCADE,
    compte_id          uuid                 NOT NULL REFERENCES comptes(id) ON DELETE CASCADE,
    categorie_id       uuid                 REFERENCES categories(id) ON DELETE SET NULL,
    type               type_flux            NOT NULL,
    montant            numeric(18,2)        NOT NULL,
    libelle            text                 NOT NULL,
    note               text,
    frequence          frequence_recurrence NOT NULL DEFAULT 'mensuelle',
    intervalle         smallint             NOT NULL DEFAULT 1,   -- toutes les N périodes
    jour_du_mois       smallint,                                  -- 1-31, fréquences mensuelles et plus
    date_debut         date                 NOT NULL,
    date_fin           date,
    prochaine_echeance date                 NOT NULL,
    auto_generer       boolean              NOT NULL DEFAULT true, -- sinon simple rappel
    actif              boolean              NOT NULL DEFAULT true,
    date_creation      timestamptz          NOT NULL DEFAULT now(),
    date_maj           timestamptz          NOT NULL DEFAULT now(),
    CONSTRAINT ck_recurrences_montant CHECK (montant > 0),
    CONSTRAINT ck_recurrences_type CHECK (type <> 'transfert'),
    CONSTRAINT ck_recurrences_intervalle CHECK (intervalle BETWEEN 1 AND 52),
    CONSTRAINT ck_recurrences_jour CHECK (jour_du_mois IS NULL OR jour_du_mois BETWEEN 1 AND 31)
);

CREATE INDEX IF NOT EXISTS ix_recurrences_echeance
    ON recurrences(prochaine_echeance) WHERE actif = true AND auto_generer = true;
CREATE INDEX IF NOT EXISTS ix_recurrences_utilisateur ON recurrences(utilisateur_id);

DROP TRIGGER IF EXISTS trg_recurrences_maj ON recurrences;
CREATE TRIGGER trg_recurrences_maj BEFORE UPDATE ON recurrences
    FOR EACH ROW EXECUTE FUNCTION touch_date_maj();

-- ═══════════════════════════════════════════════════════════════════════════
--  6. TRANSACTIONS (dépenses, revenus, transferts entre comptes)
-- ═══════════════════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS transactions (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    utilisateur_id        uuid          NOT NULL REFERENCES utilisateurs(id) ON DELETE CASCADE,
    compte_id             uuid          NOT NULL REFERENCES comptes(id) ON DELETE CASCADE,
    compte_destination_id uuid          REFERENCES comptes(id) ON DELETE CASCADE,  -- transferts
    categorie_id          uuid          REFERENCES categories(id) ON DELETE SET NULL,
    recurrence_id         uuid          REFERENCES recurrences(id) ON DELETE SET NULL,
    type                  type_flux     NOT NULL,
    montant               numeric(18,2) NOT NULL,
    devise                char(3)       NOT NULL DEFAULT 'XOF',
    date_operation        date          NOT NULL DEFAULT CURRENT_DATE,
    libelle               text          NOT NULL,
    note                  text,
    tiers                 text,                                   -- commerçant / payeur
    mode_paiement         text,                                   -- especes, mobile_money, carte, cheque…
    piece_jointe_url      text,                                   -- reçu photographié
    pointee               boolean       NOT NULL DEFAULT false,   -- rapprochée avec le relevé
    date_creation         timestamptz   NOT NULL DEFAULT now(),
    date_maj              timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT ck_transactions_montant CHECK (montant > 0),
    CONSTRAINT ck_transactions_transfert CHECK (
        (type = 'transfert' AND compte_destination_id IS NOT NULL
                            AND compte_destination_id <> compte_id
                            AND categorie_id IS NULL)
     OR (type <> 'transfert' AND compte_destination_id IS NULL)
    )
);

CREATE INDEX IF NOT EXISTS ix_transactions_utilisateur_date
    ON transactions(utilisateur_id, date_operation DESC, date_creation DESC);
CREATE INDEX IF NOT EXISTS ix_transactions_compte ON transactions(compte_id);
CREATE INDEX IF NOT EXISTS ix_transactions_destination ON transactions(compte_destination_id);
CREATE INDEX IF NOT EXISTS ix_transactions_categorie ON transactions(categorie_id);
-- Index trigramme : le mobile filtre au fur et à mesure de la frappe (ILIKE '%mot%'),
-- ce qu'un index plein texte classique ne saurait pas servir. Créé seulement si pg_trgm
-- est activable ; sans lui la recherche fonctionne, simplement par balayage.
DO $$
BEGIN
    CREATE EXTENSION IF NOT EXISTS pg_trgm;
    CREATE INDEX IF NOT EXISTS ix_transactions_recherche
        ON transactions USING gin ((libelle || ' ' || COALESCE(tiers, '')) gin_trgm_ops);
EXCEPTION WHEN OTHERS THEN
    RAISE NOTICE 'pg_trgm indisponible : la recherche sur les libellés restera séquentielle (%).', SQLERRM;
END $$;

DROP TRIGGER IF EXISTS trg_transactions_maj ON transactions;
CREATE TRIGGER trg_transactions_maj BEFORE UPDATE ON transactions
    FOR EACH ROW EXECUTE FUNCTION touch_date_maj();

-- ═══════════════════════════════════════════════════════════════════════════
--  7. BUDGETS (enveloppe par catégorie ou globale, sur une période)
-- ═══════════════════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS budgets (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    utilisateur_id  uuid           NOT NULL REFERENCES utilisateurs(id) ON DELETE CASCADE,
    categorie_id    uuid           REFERENCES categories(id) ON DELETE CASCADE,  -- NULL = budget global
    nom             text           NOT NULL,
    montant_plafond numeric(18,2)  NOT NULL,
    periode         periode_budget NOT NULL DEFAULT 'mensuelle',
    date_debut      date           NOT NULL,
    date_fin        date,                                           -- NULL = reconduction sans fin
    seuil_alerte    smallint       NOT NULL DEFAULT 80,             -- % du plafond déclenchant l'alerte
    report_solde    boolean        NOT NULL DEFAULT false,          -- reporter le reste sur la période suivante
    actif           boolean        NOT NULL DEFAULT true,
    date_creation   timestamptz    NOT NULL DEFAULT now(),
    date_maj        timestamptz    NOT NULL DEFAULT now(),
    CONSTRAINT ck_budgets_plafond CHECK (montant_plafond > 0),
    CONSTRAINT ck_budgets_seuil CHECK (seuil_alerte BETWEEN 1 AND 100),
    CONSTRAINT ck_budgets_dates CHECK (date_fin IS NULL OR date_fin >= date_debut)
);

CREATE INDEX IF NOT EXISTS ix_budgets_utilisateur ON budgets(utilisateur_id) WHERE actif = true;
CREATE UNIQUE INDEX IF NOT EXISTS uq_budgets_categorie_periode
    ON budgets(utilisateur_id, categorie_id, periode)
    WHERE actif = true AND categorie_id IS NOT NULL;

DROP TRIGGER IF EXISTS trg_budgets_maj ON budgets;
CREATE TRIGGER trg_budgets_maj BEFORE UPDATE ON budgets
    FOR EACH ROW EXECUTE FUNCTION touch_date_maj();

-- ═══════════════════════════════════════════════════════════════════════════
--  8. OBJECTIFS D'ÉPARGNE
-- ═══════════════════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS objectifs (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    utilisateur_id uuid            NOT NULL REFERENCES utilisateurs(id) ON DELETE CASCADE,
    compte_id      uuid            REFERENCES comptes(id) ON DELETE SET NULL,
    nom            text            NOT NULL,
    description    text,
    montant_cible  numeric(18,2)   NOT NULL,
    date_echeance  date,
    couleur        text            NOT NULL DEFAULT '#ffb400',
    icone          text            NOT NULL DEFAULT 'target',
    statut         statut_objectif NOT NULL DEFAULT 'en_cours',
    date_creation  timestamptz     NOT NULL DEFAULT now(),
    date_maj       timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT ck_objectifs_cible CHECK (montant_cible > 0)
);

CREATE INDEX IF NOT EXISTS ix_objectifs_utilisateur ON objectifs(utilisateur_id, statut);

DROP TRIGGER IF EXISTS trg_objectifs_maj ON objectifs;
CREATE TRIGGER trg_objectifs_maj BEFORE UPDATE ON objectifs
    FOR EACH ROW EXECUTE FUNCTION touch_date_maj();

CREATE TABLE IF NOT EXISTS objectif_versements (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    objectif_id    uuid          NOT NULL REFERENCES objectifs(id) ON DELETE CASCADE,
    transaction_id uuid          REFERENCES transactions(id) ON DELETE SET NULL,
    montant        numeric(18,2) NOT NULL,   -- négatif = retrait sur l'objectif
    date_versement date          NOT NULL DEFAULT CURRENT_DATE,
    note           text,
    date_creation  timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT ck_versements_montant CHECK (montant <> 0)
);

CREATE INDEX IF NOT EXISTS ix_versements_objectif ON objectif_versements(objectif_id);

-- ═══════════════════════════════════════════════════════════════════════════
--  9. VUE : solde courant de chaque compte
-- ═══════════════════════════════════════════════════════════════════════════
CREATE OR REPLACE VIEW v_soldes_comptes AS
SELECT c.id                        AS compte_id,
       c.utilisateur_id,
       c.nom,
       c.type,
       c.devise,
       c.inclus_dans_total,
       c.archive,
       c.solde_initial,
       c.solde_initial
         + COALESCE(entrees.total, 0)
         - COALESCE(sorties.total, 0) AS solde,
       COALESCE(mouvements.nb, 0)     AS nombre_operations
FROM comptes c
LEFT JOIN LATERAL (
    SELECT SUM(t.montant) AS total FROM transactions t
    WHERE (t.compte_id = c.id AND t.type = 'revenu')
       OR (t.compte_destination_id = c.id AND t.type = 'transfert')
) entrees ON true
LEFT JOIN LATERAL (
    SELECT SUM(t.montant) AS total FROM transactions t
    WHERE t.compte_id = c.id AND t.type IN ('depense', 'transfert')
) sorties ON true
LEFT JOIN LATERAL (
    SELECT COUNT(*) AS nb FROM transactions t
    WHERE t.compte_id = c.id OR t.compte_destination_id = c.id
) mouvements ON true;

-- ═══════════════════════════════════════════════════════════════════════════
--  10. MODÈLE DE CATÉGORIES PAR DÉFAUT (contexte burkinabè)
-- ═══════════════════════════════════════════════════════════════════════════
INSERT INTO categories_modele (nom, type, icone, couleur, ordre) VALUES
    ('Alimentation',             'depense', 'restaurant', '#e8590c',  10),
    ('Transport',                'depense', 'bus',        '#1c7ed6',  20),
    ('Logement & Loyer',         'depense', 'home',       '#5f3dc4',  30),
    ('Eau & Électricité',        'depense', 'bolt',       '#f59f00',  40),
    ('Communication & Internet', 'depense', 'phone',      '#0c8599',  50),
    ('Santé',                    'depense', 'heart',      '#c92a2a',  60),
    ('Éducation & Scolarité',    'depense', 'school',     '#2b8a3e',  70),
    ('Habillement',              'depense', 'shirt',      '#ae3ec9',  80),
    ('Loisirs & Sorties',        'depense', 'music',      '#e64980',  90),
    ('Famille & Entraide',       'depense', 'users',      '#495057', 100),
    ('Dons & Cérémonies',        'depense', 'gift',       '#f76707', 110),
    ('Abonnements',              'depense', 'repeat',     '#7048e8', 120),
    ('Frais bancaires',          'depense', 'bank',       '#868e96', 130),
    ('Impôts & Taxes',           'depense', 'receipt',    '#343a40', 140),
    ('Divers',                   'depense', 'tag',        '#adb5bd', 999),
    ('Salaire',                  'revenu',  'briefcase',  '#085041',  10),
    ('Activité commerciale',     'revenu',  'store',      '#2b8a3e',  20),
    ('Freelance & Prestations',  'revenu',  'laptop',     '#0c8599',  30),
    ('Location & Rentes',        'revenu',  'key',        '#5f3dc4',  40),
    ('Aides & Transferts reçus', 'revenu',  'hand',       '#1c7ed6',  50),
    ('Investissements',          'revenu',  'chart',      '#f59f00',  60),
    ('Autres revenus',           'revenu',  'plus',       '#adb5bd', 999)
ON CONFLICT (type, nom) DO NOTHING;
