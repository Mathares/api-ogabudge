-- ═══════════════════════════════════════════════════════════════════════════
--  OGABudget — Création de la base
--  À exécuter en superutilisateur sur la base « postgres » :
--    psql -U postgres -f 01_CreateDatabase.sql
--  Puis appliquer le schéma :
--    psql -U postgres -d ogabudget -f 02_Schema.sql
-- ═══════════════════════════════════════════════════════════════════════════

CREATE DATABASE ogabudget
    WITH ENCODING = 'UTF8'
         LC_COLLATE = 'fr_FR.UTF-8'
         LC_CTYPE   = 'fr_FR.UTF-8'
         TEMPLATE   = template0;

COMMENT ON DATABASE ogabudget IS 'OGABudget — budget personnel mobile (OGALIX GROUP)';
