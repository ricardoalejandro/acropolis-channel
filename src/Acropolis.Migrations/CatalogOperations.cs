using System.Text.RegularExpressions;
using Acropolis.Catalog.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Acropolis.Migrations;

public sealed class CatalogOperations(CatalogDbContext database)
{
    public async Task SeedQaAsync(int count, CancellationToken token)
    {
        var settings = new NpgsqlConnectionStringBuilder(database.Database.GetConnectionString());
        if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") != "Testing" || settings.Database is null || !Regex.IsMatch(settings.Database, "^acropolis_test_[a-z0-9_]+$") || count is < 1 or > 100000)
            throw new InvalidOperationException("Catalog seed is restricted to guarded test databases.");
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO catalog."Contents" ("Id","Slug","Title","Summary","Body","Category","CoverAsset","DurationSeconds","Status","CreatedUtc","UpdatedUtc","PublishedUtc","Version")
            SELECT gen_random_uuid(), 'qa-catalog-' || lpad(n::text,6,'0'), 'QA Catálogo ' || lpad(n::text,6,'0'),
                'Resumen editorial QA ' || lpad(n::text,6,'0'), 'Sinopsis editorial sintética para QA; no es una obra ni material real.',
                (ARRAY['lecturas','documentales','videos','podcast','charlas-online','cursos'])[n % 6 + 1],
                NULL, 600 + n % 3600, CASE WHEN n % 10 < 8 THEN 'published' WHEN n % 10 = 8 THEN 'draft' ELSE 'archived' END,
                now(), now(), CASE WHEN n % 10 = 8 THEN NULL ELSE now() END, md5(gen_random_uuid()::text)
            FROM generate_series(0,{count} - 1) n ON CONFLICT ("Slug") DO NOTHING
            """, token);
    }
}
