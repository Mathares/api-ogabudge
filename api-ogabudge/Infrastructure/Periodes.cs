using OGABudget.Api.Models;

namespace OGABudget.Api.Infrastructure;

/// <summary>
/// Calculs de calendrier partagés : fenêtre courante d'un budget, mois budgétaire
/// décalé (paie le 25), et prochaine échéance d'une récurrence.
/// </summary>
public static class Periodes
{
    /// <summary>
    /// Fenêtre en cours d'un budget à la date <paramref name="reference"/>.
    /// Les périodes s'enchaînent à partir de <paramref name="dateDebut"/>, sans trou :
    /// un budget mensuel démarré le 15/03 court du 15 au 14 de chaque mois.
    /// </summary>
    public static (DateOnly debut, DateOnly fin) FenetreBudget(string periode, DateOnly dateDebut, DateOnly reference)
    {
        if (reference < dateDebut)
            return (dateDebut, FinPeriode(periode, dateDebut));

        var debut = dateDebut;
        // Bond direct sur la bonne période plutôt qu'une boucle mois par mois.
        switch (periode)
        {
            case "hebdomadaire":
            {
                var semaines = (reference.DayNumber - dateDebut.DayNumber) / 7;
                debut = dateDebut.AddDays(semaines * 7);
                break;
            }
            case "trimestrielle":
            case "mensuelle":
            case "annuelle":
            {
                var pas = periode switch { "mensuelle" => 1, "trimestrielle" => 3, _ => 12 };
                var moisEcoules = (reference.Year - dateDebut.Year) * 12 + reference.Month - dateDebut.Month;
                var blocs = moisEcoules / pas;
                debut = AjouterMois(dateDebut, blocs * pas);
                if (debut > reference) debut = AjouterMois(dateDebut, (blocs - 1) * pas);
                break;
            }
        }

        return (debut, FinPeriode(periode, debut));
    }

    private static DateOnly FinPeriode(string periode, DateOnly debut) => periode switch
    {
        "hebdomadaire"  => debut.AddDays(6),
        "trimestrielle" => AjouterMois(debut, 3).AddDays(-1),
        "annuelle"      => AjouterMois(debut, 12).AddDays(-1),
        _               => AjouterMois(debut, 1).AddDays(-1),   // mensuelle
    };

    /// <summary>
    /// Ajout de mois. <see cref="DateOnly.AddMonths"/> ramène déjà au dernier jour du mois cible
    /// (31 janvier + 1 mois = 28 ou 29 février) ; l'alias existe pour la lisibilité des appels.
    /// </summary>
    public static DateOnly AjouterMois(DateOnly date, int mois) => date.AddMonths(mois);

    /// <summary>
    /// Mois budgétaire contenant <paramref name="reference"/> pour un utilisateur dont
    /// le mois démarre le <paramref name="jourDebut"/> (1 = mois calendaire).
    /// </summary>
    public static (DateOnly debut, DateOnly fin) MoisBudgetaire(DateOnly reference, int jourDebut)
    {
        if (jourDebut <= 1)
        {
            var d = new DateOnly(reference.Year, reference.Month, 1);
            return (d, d.AddMonths(1).AddDays(-1));
        }

        var debut = reference.Day >= jourDebut
            ? new DateOnly(reference.Year, reference.Month, jourDebut)
            : new DateOnly(reference.Year, reference.Month, jourDebut).AddMonths(-1);

        return (debut, debut.AddMonths(1).AddDays(-1));
    }

    /// <summary>Échéance suivante d'une récurrence, à partir de l'échéance courante.</summary>
    public static DateOnly ProchaineEcheance(DateOnly courante, string frequence, int intervalle, int? jourDuMois)
    {
        if (intervalle < 1) intervalle = 1;

        switch (frequence)
        {
            case "quotidienne":  return courante.AddDays(intervalle);
            case "hebdomadaire": return courante.AddDays(7 * intervalle);
            case "trimestrielle": return CalerJour(courante.AddMonths(3 * intervalle), jourDuMois);
            case "annuelle":     return CalerJour(courante.AddYears(intervalle), jourDuMois);
            default:             return CalerJour(courante.AddMonths(intervalle), jourDuMois);
        }
    }

    /// <summary>
    /// Replace la date sur le jour voulu du mois. Un prélèvement du 31 tombe le 30 en avril
    /// et le 28 en février, sans dériver : le jour cible reste mémorisé sur la récurrence.
    /// </summary>
    private static DateOnly CalerJour(DateOnly date, int? jourDuMois)
    {
        if (jourDuMois is not int jour) return date;
        var joursDansLeMois = DateTime.DaysInMonth(date.Year, date.Month);
        return new DateOnly(date.Year, date.Month, Math.Min(jour, joursDansLeMois));
    }

    /// <summary>Découpe une plage en périodes d'agrégation pour les courbes d'évolution.</summary>
    public static List<(DateOnly debut, DateOnly fin, string libelle)> Decouper(
        DateOnly debut, DateOnly fin, string granularite)
    {
        var tranches = new List<(DateOnly, DateOnly, string)>();
        var curseur = granularite switch
        {
            "jour"    => debut,
            "semaine" => debut.AddDays(-((int)debut.DayOfWeek + 6) % 7),   // recalé sur le lundi
            "annee"   => new DateOnly(debut.Year, 1, 1),
            _         => new DateOnly(debut.Year, debut.Month, 1),
        };

        while (curseur <= fin)
        {
            DateOnly finTranche;
            string libelle;
            switch (granularite)
            {
                case "jour":
                    finTranche = curseur;
                    libelle = curseur.ToString("dd/MM/yyyy");
                    break;
                case "semaine":
                    finTranche = curseur.AddDays(6);
                    libelle = $"Sem. {curseur:dd/MM}";
                    break;
                case "annee":
                    finTranche = curseur.AddYears(1).AddDays(-1);
                    libelle = curseur.Year.ToString();
                    break;
                default:
                    finTranche = curseur.AddMonths(1).AddDays(-1);
                    libelle = curseur.ToString("MM/yyyy");
                    break;
            }

            tranches.Add((curseur, finTranche > fin ? fin : finTranche, libelle));
            curseur = granularite switch
            {
                "jour"    => curseur.AddDays(1),
                "semaine" => curseur.AddDays(7),
                "annee"   => curseur.AddYears(1),
                _         => curseur.AddMonths(1),
            };
        }

        return tranches;
    }
}
