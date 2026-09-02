namespace OGABudget.Api.Models;

/// <summary>Page de résultats renvoyée par les listes paginées (fil des transactions).</summary>
public class PageResultat<T>
{
    public List<T> Elements { get; set; } = new();
    public int Page { get; set; }
    public int TaillePage { get; set; }
    public int TotalElements { get; set; }
    public int TotalPages => TaillePage <= 0 ? 0 : (int)Math.Ceiling(TotalElements / (double)TaillePage);
    public bool PageSuivante => Page < TotalPages;
}

/// <summary>Corps d'erreur uniforme : le mobile n'a qu'un seul format à parser.</summary>
public class ErreurApi
{
    public string Message { get; set; } = "";
    public string? Code { get; set; }

    public ErreurApi() { }
    public ErreurApi(string message, string? code = null) { Message = message; Code = code; }
}
