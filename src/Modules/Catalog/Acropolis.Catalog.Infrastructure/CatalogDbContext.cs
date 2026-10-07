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
    public string? Author { get; set; }
    public string[] Tags { get; set; } = [];
    public string? WorkText { get; set; }
    public string? YouTubeId { get; set; }
    public string? CollectionKind { get; set; }
    public Guid[] ItemIds { get; set; } = [];
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
    public DbSet<ConsumptionSession> ConsumptionSessions => Set<ConsumptionSession>();
    public DbSet<ConsumptionPulse> ConsumptionPulses => Set<ConsumptionPulse>();
    public DbSet<ConsumptionDaily> ConsumptionDaily => Set<ConsumptionDaily>();
    public DbSet<ConsumptionAccountDaily> ConsumptionAccountDaily => Set<ConsumptionAccountDaily>();
    public DbSet<CatalogTopic> Topics => Set<CatalogTopic>();
    public DbSet<ContentTopic> ContentTopics => Set<ContentTopic>();
    public DbSet<TopicDirectoryRevision> TopicDirectory => Set<TopicDirectoryRevision>();
    public DbSet<TopicAudit> TopicAudit => Set<TopicAudit>();
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAudit();
        GuardConsumption();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardAudit();
        GuardConsumption();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
    private void GuardConsumption()
    {
        if (ChangeTracker.Entries<ConsumptionPulse>().Any(x => x.State == EntityState.Modified))
            throw new InvalidOperationException("Consumption observations are immutable.");
    }
    private void GuardAudit()
    {
        if (ChangeTracker.Entries<ContentAudit>().Any(x => x.State is EntityState.Modified or EntityState.Deleted) ||
            ChangeTracker.Entries<TopicAudit>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Catalog audit is append-only.");
    }
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(Schema);
        TopicsModel.Configure(builder);
        ConsumptionActivityModel.Configure(builder);
        builder.Entity<EditorialContent>(entity =>
        {
            entity.ToTable("Contents", table =>
            {
                table.HasCheckConstraint("CK_Contents_WorkText", "\"WorkText\" IS NULL OR (\"Category\"='lecturas' AND length(btrim(\"WorkText\")) > 0 AND \"CollectionKind\" IS NULL)");
                table.HasCheckConstraint("CK_Contents_YouTube", "\"YouTubeId\" IS NULL OR (\"YouTubeId\" ~ '^[A-Za-z0-9_-]{11}$' AND \"Category\" IN ('documentales','videos','podcast','charlas-online') AND \"CollectionKind\" IS NULL)");
                table.HasCheckConstraint("CK_Contents_Collection", "(\"CollectionKind\" IS NULL AND cardinality(\"ItemIds\")=0) OR (\"CollectionKind\" IS NOT NULL AND \"CollectionKind\" IN ('course','program') AND \"Category\"='cursos' AND \"WorkText\" IS NULL AND \"YouTubeId\" IS NULL)");
                table.HasCheckConstraint("CK_Contents_CollectionSize", "array_position(\"ItemIds\", NULL) IS NULL AND cardinality(\"ItemIds\") BETWEEN 0 AND 100 AND (\"Status\" <> 'published' OR \"CollectionKind\" IS NULL OR cardinality(\"ItemIds\") > 0)");
                table.HasCheckConstraint("CK_Contents_Tags", "array_position(\"Tags\", NULL) IS NULL AND cardinality(\"Tags\") BETWEEN 0 AND 12");
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
            entity.Property(x => x.Author).HasMaxLength(180);
            entity.Property(x => x.Tags).HasColumnType("character varying(40)[]").HasDefaultValue(Array.Empty<string>());
            entity.Property(x => x.WorkText).HasMaxLength(500000);
            entity.Property(x => x.YouTubeId).HasMaxLength(11);
            entity.Property(x => x.CollectionKind).HasMaxLength(16);
            entity.Property(x => x.ItemIds).HasDefaultValue(Array.Empty<Guid>());
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
            entity.HasIndex(x => new { x.ContentId, x.CreatedUtc, x.Id }).IsDescending(false, true, false);
            entity.HasIndex(x => new { x.CreatedUtc, x.Id }).IsDescending(true, false);
            entity.HasIndex(x => new { x.Action, x.CreatedUtc, x.Id }).IsDescending(false, true, false);
        });
    }
}
