namespace OGABudget.Api.Services;

/// <summary>
/// Matérialise chaque jour les échéances arrivées à terme, pour tout le parc.
/// Le mobile déclenche aussi la génération à l'ouverture (POST /api/recurrences/generer) :
/// ce service de fond garantit que les données restent justes même si l'appli n'est pas ouverte.
/// </summary>
public class RecurrenceHostedService : BackgroundService
{
    private static readonly TimeSpan Intervalle = TimeSpan.FromHours(6);

    private readonly IServiceProvider _services;
    private readonly ILogger<RecurrenceHostedService> _logger;

    public RecurrenceHostedService(IServiceProvider services, ILogger<RecurrenceHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Laisse l'application finir de démarrer avant de toucher la base.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        using var minuterie = new PeriodicTimer(Intervalle);
        do
        {
            try
            {
                using var portee = _services.CreateScope();
                var service = portee.ServiceProvider.GetRequiredService<RecurrenceService>();
                var resultat = await service.GenererAsync(null, stoppingToken);

                if (resultat.TransactionsCreees > 0)
                    _logger.LogInformation("Récurrences : {Nombre} transaction(s) générées.",
                                           resultat.TransactionsCreees);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Une base indisponible ne doit pas tuer la boucle : on réessaiera au tour suivant.
                _logger.LogError(ex, "Échec de la génération automatique des récurrences.");
            }
        }
        while (await minuterie.WaitForNextTickAsync(stoppingToken));
    }
}
