-- ═══════════════════════════════════════════════════════════════════════════
--  OGABudget — Création de la base
--
--  Sur PostgreSQL local :
--    psql -U postgres -p 5433 -f 01_CreateDatabase.sql
--    psql -U postgres -p 5433 -d dbogabudge -f 02_Schema.sql
--
--  Sur Azure Database for PostgreSQL, la base est créée depuis le portail :
--  ce script n'y sert pas, appliquer directement 02_Schema.sql.
-- ═══════════════════════════════════════════════════════════════════════════

CREATE DATABASE dbogabudge
    WITH ENCODING = 'UTF8'
         TEMPLATE = template0;

COMMENT ON DATABASE dbogabudge IS 'OGABudget — budget personnel mobile (OGALIX GROUP)';
