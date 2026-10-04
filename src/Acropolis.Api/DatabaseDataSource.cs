using Npgsql;

namespace Acropolis.Api;

internal static class DatabaseDataSource
{
    public static NpgsqlDataSource Create(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Database configuration is required.");
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Timeout = 3,
                CommandTimeout = 3,
                IncludeErrorDetail = false
            };
            return new NpgsqlDataSourceBuilder(settings.ConnectionString).Build();
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Database configuration is invalid.");
        }
    }
}
