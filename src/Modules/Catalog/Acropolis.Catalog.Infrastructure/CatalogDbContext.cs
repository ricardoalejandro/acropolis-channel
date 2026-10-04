using Microsoft.EntityFrameworkCore;

namespace Acropolis.Catalog.Infrastructure;

public sealed class EditorialContent
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Body { get; set; } = "";
    public string Category { get; set; } = "";
    public string? CoverAsset { get; set; }
    public int? DurationSeconds { get; set; }
    public string Status { get; set; } = "draft";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? PublishedUtc { get; set; }
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
}
public sealed class ContentAudit
{
    public Guid Id { get; set; }
    public Guid ActorId { get; set; }
    public Guid ContentId { get; set; }
    public string Action { get; set; } = "";
    public string Changes { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
}
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public const string Schema = "catalog";
    public const string HistoryTable = "__EFMigrationsHistory";
    public DbSet<EditorialContent> Contents => Set<EditorialContent>();
    public DbSet<ContentAudit> Audit => Set<ContentAudit>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(Schema);
        builder.Entity<EditorialContent>(entity =>
        {
            entity.ToTable("Contents", table =>
            {
                table.HasCheckConstraint("CK_Contents_Status", "\"Status\" IN ('draft','published','archived')");
                table.HasCheckConstraint("CK_Contents_Category", "\"Category\" IN ('lecturas','documentales','videos','podcast','charlas-online','cursos')");
                table.HasCheckConstraint("CK_Contents_Slug", "length(\"Slug\") BETWEEN 2 AND 160 AND \"Slug\" ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                table.HasCheckConstraint("CK_Contents_Title", "length(btrim(\"Title\")) BETWEEN 2 AND 180");
                table.HasCheckConstraint("CK_Contents_Duration", "\"DurationSeconds\" IS NULL OR \"DurationSeconds\" BETWEEN 1 AND 86400");
                table.HasCheckConstraint("CK_Contents_Cover", "\"CoverAsset\" IS NULL OR \"CoverAsset\" IN ('hero-acropolis','editorial-reading','editorial-podcast','editorial-dialogue','editorial-nature')");
                table.HasCheckConstraint("CK_Contents_Publication", "\"Status\" <> 'published' OR (\"PublishedUtc\" IS NOT NULL AND length(btrim(\"Summary\")) > 0 AND length(btrim(\"Body\")) > 0)");
            });
            entity.Property(x => x.Slug).HasMaxLength(160);
            entity.Property(x => x.Title).HasMaxLength(180);
            entity.Property(x => x.Summary).HasMaxLength(600);
            entity.Property(x => x.Body).HasMaxLength(50000);
            entity.Property(x => x.Category).HasMaxLength(32);
            entity.Property(x => x.CoverAsset).HasMaxLength(32);
            entity.Property(x => x.Status).HasMaxLength(16);
            entity.Property(x => x.Version).HasMaxLength(32).IsConcurrencyToken();
            entity.HasIndex(x => x.Slug).IsUnique();
            entity.HasIndex(x => new { x.Status, x.PublishedUtc, x.Id }).IsDescending(false, true, false);
            entity.HasIndex(x => new { x.Category, x.Status, x.PublishedUtc, x.Id }).IsDescending(false, false, true, false);
            entity.HasIndex(x => new { x.UpdatedUtc, x.Id }).IsDescending(true, false);
            entity.HasIndex(x => x.Title).HasMethod("gin").HasOperators("identity.gin_trgm_ops");
            entity.HasIndex(x => x.Summary).HasMethod("gin").HasOperators("identity.gin_trgm_ops");
        });
        builder.Entity<ContentAudit>(entity =>
        {
            entity.Property(x => x.Action).HasMaxLength(64);
            entity.Property(x => x.Changes).HasMaxLength(1024);
            entity.HasIndex(x => new { x.ContentId, x.CreatedUtc });
        });
    }
}
