# api-ogabudge

API ASP.NET Core d'**OGABudget**, l'application mobile de gestion de budget personnel
d'OGALIX GROUP : comptes, dépenses et revenus, budgets, objectifs d'épargne, opérations
récurrentes et statistiques.

- **Stack** : .NET 10 / ASP.NET Core Web API, PostgreSQL (Npgsql en ADO.NET direct), JWT Bearer, Swagger
- **Namespace racine** : `OGABudget.Api` — assembly `api-ogabudge`
- **Devise par défaut** : XOF · **Locale** : `fr-BF` · **Fuseau** : `Africa/Ouagadougou`

---

## 1. Mise en route

### Base de données

```bash
psql -U postgres -p 5433 -f api-ogabudge/Data/Scripts/01_CreateDatabase.sql
```

```bash
psql -U postgres -p 5433 -d dbogabudge -f api-ogabudge/Data/Scripts/02_Schema.sql
```

Le script de schéma est **idempotent** : il peut être rejoué sur une base existante sans
rien détruire. Il crée les types énumérés, les 9 tables, la vue des soldes et le modèle
des catégories par défaut.

Il **ne dépend d'aucune extension** : `gen_random_uuid()` est natif depuis PostgreSQL 13,
l'unicité des e-mails passe par un index sur `lower(email)` plutôt que par `citext`, et
l'index trigramme de recherche n'est créé que si `pg_trgm` s'active (sinon la recherche
fonctionne quand même, par balayage). C'est ce qui permet de l'appliquer tel quel sur
Azure Database for PostgreSQL sans toucher au paramètre serveur `azure.extensions`.

### Sur Azure Database for PostgreSQL

La base `dbogabudge` existe déjà sur `ogalix-db.postgres.database.azure.com`. Pour y
appliquer le schéma depuis un poste, son IP publique doit figurer dans les règles de
pare-feu du serveur (portail Azure → le serveur → *Réseau* → *Règles de pare-feu*) :

```bash
psql "host=ogalix-db.postgres.database.azure.com port=5432 dbname=dbogabudge user=user_ogalix sslmode=require" -f api-ogabudge/Data/Scripts/02_Schema.sql
```

Rôle applicatif recommandé (plutôt que `postgres`) :

```bash
psql -U postgres -p 5433 -d dbogabudge -c "CREATE ROLE ogabudget_api LOGIN PASSWORD 'a-changer'; GRANT ALL ON ALL TABLES IN SCHEMA public TO ogabudget_api; GRANT ALL ON ALL SEQUENCES IN SCHEMA public TO ogabudget_api;"
```

### Configuration

Copier `api-ogabudge/appsettings.Local.example.json` en `appsettings.Local.json` (ignoré par git)
et y renseigner la chaîne PostgreSQL et le secret JWT. Ce fichier surcharge `appsettings.json`.

En production, tout se pilote aussi par variables d'environnement :
`ConnectionStrings__DefaultConnection`, `Jwt__Secret`, `Jwt__Issuer`, `Jwt__Audience`.

Le secret JWT doit faire **au moins 32 caractères**. Sans lui, l'API démarre mais tous les
endpoints `[Authorize]` répondent 401.

### Lancer

```bash
dotnet run --project api-ogabudge/api-ogabudge.csproj
```

Swagger : `http://localhost:5224/swagger`

---

## 2. Déploiement Azure

L'API tourne sur **Azure App Service (Linux)** — `api-ogabudge` dans le groupe de ressources
`ogalix` — et s'expose sur https://api-ogabudge.azurewebsites.net. La publication se fait
depuis Visual Studio avec le profil *Zip Deploy*.

### Les secrets ne sont pas dans le paquet

`appsettings.Local.json`, `appsettings.Local.example.json` et `appsettings.Development.json`
sont exclus de la publication (`CopyToPublishDirectory="Never"` dans le `.csproj`). Sur
App Service, la chaîne PostgreSQL et le secret JWT viennent des **paramètres d'application**.

À faire **avant** la prochaine publication, sinon l'API démarrera sans base :

```bash
az webapp config appsettings set --resource-group ogalix --name api-ogabudge --settings "ConnectionStrings__DefaultConnection=$(python -c "import json;print(json.load(open('api-ogabudge/appsettings.Local.json'))['ConnectionStrings']['DefaultConnection'])")" "Jwt__Secret=$(python -c "import json;print(json.load(open('api-ogabudge/appsettings.Local.json'))['Jwt']['Secret'])")"
```

Cette commande recopie les valeurs du fichier local vers Azure sans les afficher. Par le
portail : *App Service → Paramètres → Variables d'environnement*, deux entrées nommées
`ConnectionStrings__DefaultConnection` et `Jwt__Secret` (double tiret bas).

Une chaîne de connexion absente ne fait plus planter le démarrage : l'API se lance,
journalise l'erreur et `GET /api/sante` répond 503. Mieux vaut un diagnostic lisible
qu'un conteneur qui redémarre en boucle.

### API Management

Le profil de publication porte `UpdateApiOnPublish=true` : à chaque publication, Visual
Studio génère le document Swagger et l'importe dans l'instance APIM `api-ogabudgeapi`.
Cette étape est **postérieure** au déploiement — quand elle échoue, l'application est
déjà en ligne, seule la façade APIM n'est pas à jour.

Elle exige du document trois choses que Swashbuckle ne fournit pas d'office, et dont
l'absence se solde par un `BadRequest` opaque :

| Exigence | Comment elle est satisfaite |
|---|---|
| Un `operationId` unique par opération | `c.CustomOperationIds(...)` → `Comptes_Obtenir`, `Auth_Connecter`… |
| Un `host` (adresse du service dorsal) | `c.AddServer(...)` alimenté par `Swagger:UrlPublique` |
| Un schéma de sécurité valide en Swagger 2.0 | Type `apiKey` en en-tête, et non `http`/`bearer` |

Visual Studio demande le document au format **Swagger 2.0**, qui ne connaît pas le type
`http`. Déclaré en `Http`/`bearer`, le schéma était rétrogradé en objet vide `"Bearer": {}`
— invalide, donc refusé. La contrepartie du type `apiKey` : dans Swagger UI, saisir
« `Bearer <token>` » et non le seul jeton.

`Swagger:UrlPublique` est renseigné dans `appsettings.json` et **vidé** dans
`appsettings.Development.json`, pour que Swagger UI vise `localhost` en développement.
La génération du document à la publication tourne hors environnement de développement :
elle prend donc bien l'URL Azure.

Pour vérifier le document avant de publier :

```bash
dotnet swagger tofile --serializeasv2 --output api-ogabudge/bin/Release/net10.0/swagger.json api-ogabudge/bin/Release/net10.0/api-ogabudge.dll v1
```

Pour se passer entièrement d'API Management, passer `UpdateApiOnPublish` à `false` dans
`Properties/PublishProfiles/api-ogabudge - Zip Deploy.pubxml`.

---

## 3. Modèle de données

| Table | Rôle |
|---|---|
| `utilisateurs` | Compte, devise, locale, jour de début du mois budgétaire |
| `refresh_tokens` | Sessions mobiles longues, avec rotation et révocation |
| `comptes` | Portefeuille, banque, Orange/Moov Money, épargne, carte, crédit |
| `categories` | Hiérarchiques, par utilisateur, typées dépense ou revenu |
| `categories_modele` | Modèle copié dans le compte à l'inscription (22 catégories) |
| `transactions` | Dépenses, revenus et transferts entre comptes |
| `budgets` | Enveloppe par catégorie ou globale, sur une période reconductible |
| `objectifs` + `objectif_versements` | Objectifs d'épargne et leur alimentation |
| `recurrences` | Salaire, loyer, abonnements — matérialisés en transactions |
| `v_soldes_comptes` *(vue)* | Solde courant de chaque compte |

**Choix structurants**

- Le **solde d'un compte n'est jamais stocké** : la vue `v_soldes_comptes` le recalcule
  (`solde_initial + revenus + transferts entrants − dépenses − transferts sortants`).
  Aucune colonne dénormalisée à resynchroniser après une correction de saisie.
- Un **transfert** est une seule ligne portant `compte_id` et `compte_destination_id`, sans
  catégorie. Une contrainte `CHECK` interdit toute autre combinaison. Les transferts sont
  exclus de toutes les statistiques et de la consommation des budgets : déplacer de l'argent
  entre ses propres comptes n'appauvrit personne.
- La **consommation d'un budget** est recalculée à la lecture sur la fenêtre courante ;
  les sous-catégories comptent dans le budget de leur parent.
- Les **catégories système** (issues du modèle) sont modifiables et archivables par
  l'utilisateur, mais marquées `systeme = true`.

---

## 4. Endpoints

Toutes les routes sauf `/api/sante` et les trois premières d'`/api/auth` exigent
`Authorization: Bearer <accessToken>`. Le cloisonnement se fait **toujours** sur
l'identifiant du jeton, jamais sur un identifiant reçu du client.

### Authentification — `/api/auth`

| Méthode | Route | Rôle |
|---|---|---|
| POST | `/inscription` | Crée le compte, ses 22 catégories et un portefeuille « Espèces ». 5/h/IP |
| POST | `/connexion` | E-mail + mot de passe. 10/15 min/IP |
| POST | `/rafraichir` | Échange le refresh token ; l'ancien est révoqué (rotation) |
| POST | `/deconnexion` | `?tous=true` pour révoquer tous les appareils |
| GET | `/moi` | Profil |
| PUT | `/moi` | Met à jour le profil (champs omis inchangés) |
| POST | `/mot-de-passe` | Change le mot de passe et révoque toutes les sessions |
| DELETE | `/moi` | Suppression définitive du compte et de ses données |

### Comptes — `/api/comptes`

`GET /` · `GET /solde-total` · `GET /{id}` · `POST /` · `PUT /{id}` · `DELETE /{id}?forcer=`

La suppression est refusée (409) tant que le compte porte des opérations ; `forcer=true`
détruit aussi l'historique. L'archivage (`archive = true`) est presque toujours préférable.

### Catégories — `/api/categories`

`GET /?type=&arborescence=&inclureArchivees=` · `GET /{id}` · `POST /` · `PUT /{id}` · `DELETE /{id}?forcer=`

`arborescence=true` imbrique les sous-catégories dans leur parent. La suppression forcée
conserve les transactions et les bascule en « Sans catégorie ».

### Transactions — `/api/transactions`

| Méthode | Route | Rôle |
|---|---|---|
| GET | `/` | Fil paginé et filtré (voir ci-dessous) |
| GET | `/{id}` | Détail |
| POST | `/` | Dépense, revenu ou transfert |
| POST | `/lot` | Envoi groupé après saisie hors ligne — **tout ou rien** |
| PUT | `/{id}` | Modification |
| PATCH | `/{id}/pointage?pointee=` | Rapprochement du relevé |
| DELETE | `/{id}` | Suppression |

Filtres du `GET /` : `debut`, `fin`, `type`, `compteId`, `categorieId`, `montantMin`,
`montantMax`, `recherche`, `pointee`, `page`, `taillePage` (50 par défaut, 200 max).
Un transfert ressort dans le relevé de **ses deux comptes**.

### Budgets — `/api/budgets`

`GET /?reference=` · `GET /alertes` · `GET /{id}` · `POST /` · `PUT /{id}` · `DELETE /{id}`

Chaque budget renvoie sa fenêtre courante, le montant consommé, le pourcentage, les
drapeaux `alerteAtteinte` / `depasse` et le `rythmeJournalierRestant`. `GET /alertes`
alimente directement les notifications push.

### Objectifs d'épargne — `/api/objectifs`

`GET /?statut=` · `GET /{id}` · `POST /` · `PUT /{id}` · `DELETE /{id}`
`GET|POST /{id}/versements` · `DELETE /{id}/versements/{versementId}`

Un versement avec `genererTransaction = true` crée en plus un **transfert réel** du compte
source vers le compte d'épargne de l'objectif : soldes et avancement restent cohérents.
Atteindre la cible bascule automatiquement le statut en `atteint`.

### Récurrences — `/api/recurrences`

`GET /?inclureInactives=` · `GET /prochaines?jours=` · `GET /{id}` · `POST /` · `PUT /{id}` ·
`POST /generer` · `DELETE /{id}`

`POST /generer` matérialise les échéances échues en transactions réelles. L'opération est
**idempotente** (`prochaine_echeance` avance à chaque ligne créée) : le mobile peut l'appeler
à chaque ouverture. Une absence de trois mois génère bien les trois loyers manquants.
`RecurrenceHostedService` fait le même travail toutes les 6 h pour tout le parc, afin que
les données restent justes même si l'appli n'est jamais ouverte.

### Statistiques — `/api/statistiques`

| Route | Rôle |
|---|---|
| `GET /tableau-de-bord` | Écran d'accueil complet **en un seul appel** |
| `GET /resume?debut=&fin=` | Revenus, dépenses, solde net, taux d'épargne |
| `GET /par-categorie?debut=&fin=&type=` | Répartition, sous-catégories remontées sur leur parent |
| `GET /evolution?debut=&fin=&granularite=` | Courbe `jour` / `semaine` / `mois` / `annee` |

L'évolution renvoie les périodes sans mouvement à zéro : la courbe ne ment pas sur les trous.

### Santé — `GET /api/sante`

Anonyme. 200 si PostgreSQL répond, 503 sinon.

---

## 5. Sécurité

- Mots de passe en **PBKDF2-HMAC-SHA256**, 210 000 itérations, sel de 16 octets, comparaison
  à temps constant. Le nombre d'itérations est embarqué dans l'empreinte pour pouvoir être
  relevé plus tard sans invalider les mots de passe existants.
- Le login paie le coût du hachage même quand l'adresse n'existe pas : la durée de réponse
  ne révèle pas l'existence d'un compte.
- **Refresh tokens** : 512 bits aléatoires, seule leur empreinte SHA-256 est stockée, rotation
  à chaque rafraîchissement. Changer de mot de passe révoque toutes les sessions.
- **Rate limiting** sur les deux portes non authentifiées : inscription (5/h/IP), connexion
  (10/15 min/IP).
- Toutes les requêtes SQL sont **paramétrées**. Chaque lecture et chaque écriture porte
  `utilisateur_id = @uid` : un identifiant d'une autre personne renvoie 404, jamais ses données.
- Format d'erreur unique (`{ message, code }`) pour les erreurs de validation, métier et internes.

---

## 6. Reste à faire

- Vérification de l'adresse e-mail et réinitialisation du mot de passe (envoi de courriels)
- Pièces jointes : `piece_jointe_url` attend un stockage type Azure Blob, comme OGAHub.Api
- Notifications push (les données sont là : `GET /budgets/alertes`, `GET /recurrences/prochaines`)
- Conversion multi-devises si un utilisateur tient des comptes en XOF et en EUR
- Tests d'intégration sur une base éphémère
