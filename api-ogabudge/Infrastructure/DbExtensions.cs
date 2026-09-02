using Npgsql;

namespace OGABudget.Api.Infrastructure;

/// <summary>Lectures tolérantes au NULL, pour éviter un <c>IsDBNull</c> à chaque colonne.</summary>
public static class DbExtensions
{
    public static string? Texte(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static Guid? Uuid(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);

    public static DateOnly? DateNullable(this NpgsqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetFieldValue<DateOnly>(i);

    public static decimal Decimal(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? 0m : r.GetDecimal(i);

    public static int Entier(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i));

    public static int? EntierNullable(this NpgsqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i));

    public static DateTimeOffset Horodatage(this NpgsqlDataReader r, int i) =>
        r.IsDBNull(i) ? default : r.GetFieldValue<DateTimeOffset>(i);

    /// <summary>Ajoute un paramètre en convertissant <c>null</c> en <see cref="DBNull"/>.</summary>
    public static NpgsqlParameter Ajouter(this NpgsqlCommand cmd, string nom, object? valeur) =>
        cmd.Parameters.AddWithValue(nom, valeur ?? DBNull.Value);
}
