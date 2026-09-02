using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Npgsql;
using OGABudget.Api.Models;
using OGABudget.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ─── Configuration locale (hors dépôt) ───────────────────────────────────────
// appsettings.Local.json porte les vraies credentials (chaîne PostgreSQL, secret JWT).
// Il surcharge appsettings.json et n'est jamais versionné.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Les variables d'environnement sont relues APRÈS ce fichier, donc elles l'emportent.
// Sur App Service, les paramètres d'application font autorité quoi qu'il arrive : si un
// appsettings.Local.json se retrouvait un jour dans le paquet déployé, il ne détournerait
// pas silencieusement l'API vers une autre base.
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddEnvironmentVariables("OGABUDGET_");

// ─── PostgreSQL ──────────────────────────────────────────────────────────────
// Une chaîne absente ne fait pas échouer le démarrage : App Service redémarrerait
// le conteneur en boucle sans jamais dire pourquoi, et les outils de conception
// (génération Swagger à la publication) ne pourraient plus charger l'application.
// L'API démarre, journalise l'anomalie, et GET /api/sante répond 503 en la nommant.
var chaine = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(chaine))
{
    LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Démarrage").LogError(
        "ConnectionStrings:DefaultConnection est absent. Renseigner appsettings.Local.json en local, " +
        "ou le paramètre d'application ConnectionStrings__DefaultConnection sur App Service.");
    chaine = "Host=non-configure;Database=non-configure";
}

var sourceDonnees = new NpgsqlDataSourceBuilder(chaine)
    .EnableParameterLogging(builder.Environment.IsDevelopment())
    .Build();
builder.Services.AddSingleton(sourceDonnees);

// ─── Services métier ─────────────────────────────────────────────────────────
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<CompteService>();
builder.Services.AddScoped<CategorieService>();
builder.Services.AddScoped<TransactionService>();
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<ObjectifService>();
builder.Services.AddScoped<RecurrenceService>();
builder.Services.AddScoped<StatistiqueService>();
builder.Services.AddHostedService<RecurrenceHostedService>();

// ─── MVC / JSON ──────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        // Les libellés saisis par l'utilisateur contiennent des accents : ne pas les échapper
        // en \uXXXX allège la charge utile sur une connexion mobile lente.
        options.JsonSerializerOptions.Encoder =
            System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    });

// Erreurs de validation renvoyées dans le même format que les erreurs métier.
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = contexte =>
    {
        var premier = contexte.ModelState
            .Where(e => e.Value?.Errors.Count > 0)
            .SelectMany(e => e.Value!.Errors)
            .Select(e => e.ErrorMessage)
            .FirstOrDefault() ?? "Requête invalide.";
        return new BadRequestObjectResult(new ErreurApi(premier, "validation"));
    };
});

builder.Services.AddEndpointsApiExplorer();

// ─── Authentification JWT ────────────────────────────────────────────────────
var secretJwt = builder.Configuration["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(secretJwt))
{
    LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Démarrage").LogWarning(
        "Jwt:Secret est vide : tous les endpoints [Authorize] répondront 401. " +
        "Définir Jwt__Secret (au moins 32 caractères) en production.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = !string.IsNullOrWhiteSpace(secretJwt),
            IssuerSigningKey = string.IsNullOrWhiteSpace(secretJwt)
                ? null
                : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretJwt)),
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "api-ogabudge",
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "ogabudget-mobile",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };
    });

builder.Services.AddAuthorization();

// ─── Limitation de débit ─────────────────────────────────────────────────────
// Deux portes d'entrée non authentifiées à protéger : la création de comptes et le login.
builder.Services.AddRateLimiter(opts =>
{
    opts.AddPolicy("inscription", contexte => RateLimitPartition.GetFixedWindowLimiter(
        contexte.Connection.RemoteIpAddress?.ToString() ?? "inconnu",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromHours(1) }));

    opts.AddPolicy("connexion", contexte => RateLimitPartition.GetFixedWindowLimiter(
        contexte.Connection.RemoteIpAddress?.ToString() ?? "inconnu",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(15) }));

    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    opts.OnRejected = async (contexte, token) =>
    {
        contexte.HttpContext.Response.ContentType = "application/json";
        var message = contexte.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delai)
            ? $"Trop de tentatives. Réessayez dans {delai.TotalSeconds:0} secondes."
            : "Trop de tentatives. Réessayez plus tard.";
        await contexte.HttpContext.Response.WriteAsJsonAsync(new ErreurApi(message, "trop_de_requetes"), token);
    };
});

// ─── CORS ────────────────────────────────────────────────────────────────────
// Une appli mobile native n'en a pas besoin ; la politique sert au futur portail web
// et aux tests depuis un navigateur.
var originesAutorisees = builder.Configuration.GetSection("Cors:Origines").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
    options.AddPolicy("ogabudget", politique =>
    {
        if (originesAutorisees.Length > 0)
            politique.WithOrigins(originesAutorisees).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        else
            politique.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    }));

// ─── Swagger ─────────────────────────────────────────────────────────────────
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "API OGABudget",
        Version = "v1",
        Description = "Budget personnel mobile : comptes, dépenses, revenus, budgets, objectifs d'épargne. " +
                      "OGALIX GROUP — Burkina Faso."
    });

    // API Management a besoin de connaître l'adresse du service dorsal. En Swagger 2.0,
    // Swashbuckle traduit ce serveur en host + basePath + schemes ; sans lui, l'import
    // est refusé. Vide en développement, pour que Swagger UI vise bien localhost.
    var urlPublique = builder.Configuration["Swagger:UrlPublique"];
    if (!string.IsNullOrWhiteSpace(urlPublique))
        c.AddServer(new OpenApiServer { Url = urlPublique.TrimEnd('/'), Description = "Production" });

    // API Management nomme chaque opération d'après son operationId et refuse le document
    // s'il en manque. Swashbuckle n'en génère aucun par défaut.
    c.CustomOperationIds(api => api.ActionDescriptor is ControllerActionDescriptor descripteur
        ? $"{descripteur.ControllerName}_{descripteur.ActionName}"
        : null);

    // Schéma déclaré en ApiKey plutôt qu'en Http/bearer : Visual Studio demande le
    // document au format Swagger 2.0 pour l'importer dans API Management, or 2.0 ne
    // connaît pas le type « http ». Swashbuckle le rétrogradait alors en objet vide
    // ("Bearer": {}), invalide, et l'import échouait en BadRequest. ApiKey en en-tête
    // se sérialise correctement en 2.0 comme en 3.0.
    // Contrepartie : dans Swagger UI, saisir « Bearer <token> » et non le seul jeton.
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "Saisir « Bearer » suivi du jeton obtenu via POST /api/auth/connexion "
                    + "(champ accessToken). Exemple : Bearer eyJhbGciOi…"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });

    var documentation = Path.Combine(AppContext.BaseDirectory, "api-ogabudge.xml");
    if (File.Exists(documentation)) c.IncludeXmlComments(documentation);
});

var app = builder.Build();

// ─── Pipeline ────────────────────────────────────────────────────────────────
app.UseExceptionHandler(branche => branche.Run(async contexte =>
{
    contexte.Response.StatusCode = StatusCodes.Status500InternalServerError;
    contexte.Response.ContentType = "application/json";
    await contexte.Response.WriteAsJsonAsync(
        new ErreurApi("Une erreur interne est survenue.", "erreur_interne"));
}));

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "API OGABudget v1");
    c.DocumentTitle = "API OGABudget";
});

if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();

app.UseCors("ogabudget");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// La racine renvoyait 404. ExcludeFromDescription est indispensable : une route sans
// operationId dans le document ferait de nouveau échouer l'import API Management.
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.Run();
