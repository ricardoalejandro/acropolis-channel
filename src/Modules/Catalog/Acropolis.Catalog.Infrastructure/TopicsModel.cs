using Microsoft.EntityFrameworkCore;

namespace Acropolis.Catalog.Infrastructure;

public sealed class CatalogTopic
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "active";
    public int Position { get; set; }
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}
public sealed class ContentTopic
{
    public Guid ContentId { get; set; }
    public Guid TopicId { get; set; }
}
public sealed class TopicDirectoryRevision
{
    public int Id { get; set; }
    public string Version { get; set; } = "";
}
public sealed class TopicAudit
{
    public Guid Id { get; set; }
    public Guid ActorId { get; set; }
    public Guid TopicId { get; set; }
    public string Action { get; set; } = "";
    public string Changes { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
}
internal static class TopicsModel
{
    internal static void Configure(ModelBuilder builder)
    {
        builder.Entity<CatalogTopic>(entity =>
        {
            entity.ToTable("Topics", table =>
            {
                table.HasCheckConstraint("CK_Topics_Slug", "length(\"Slug\") BETWEEN 2 AND 160 AND \"Slug\" ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                table.HasCheckConstraint("CK_Topics_Name", "length(btrim(\"Name\")) BETWEEN 2 AND 180");
                table.HasCheckConstraint("CK_Topics_Status", "\"Status\" IN ('active','archived')");
                table.HasCheckConstraint("CK_Topics_Position", "\"Position\" >= 0");
                table.HasCheckConstraint("CK_Topics_Version", "\"Version\" ~ '^[a-f0-9]{32}$'");
            });
            entity.Property(x => x.Slug).HasMaxLength(160);
            entity.Property(x => x.Name).HasMaxLength(180);
            entity.Property(x => x.Status).HasMaxLength(16);
            entity.Property(x => x.Version).HasMaxLength(32).IsConcurrencyToken();
            entity.HasIndex(x => x.Slug).IsUnique();
            entity.HasIndex(x => new { x.Status, x.Position, x.Id });
            entity.HasIndex(x => new { x.Position, x.Id });
            entity.HasIndex(x => x.Name).HasMethod("gin").HasOperators("identity.gin_trgm_ops");
        });
        builder.Entity<ContentTopic>(entity =>
        {
            entity.ToTable("ContentTopics");
            entity.HasKey(x => new { x.ContentId, x.TopicId });
            entity.HasIndex(x => new { x.TopicId, x.ContentId });
            entity.HasOne<EditorialContent>().WithMany().HasForeignKey(x => x.ContentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CatalogTopic>().WithMany().HasForeignKey(x => x.TopicId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<TopicDirectoryRevision>(entity =>
        {
            entity.ToTable("TopicDirectory", table =>
            {
                table.HasCheckConstraint("CK_TopicDirectory_Id", "\"Id\" = 1");
                table.HasCheckConstraint("CK_TopicDirectory_Version", "\"Version\" ~ '^[a-f0-9]{32}$'");
            });
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Version).HasMaxLength(32).IsConcurrencyToken();
        });
        builder.Entity<TopicAudit>(entity =>
        {
            entity.ToTable("TopicAudit");
            entity.Property(x => x.Action).HasMaxLength(64);
            entity.Property(x => x.Changes).HasMaxLength(1024);
            entity.HasIndex(x => new { x.TopicId, x.CreatedUtc, x.Id }).IsDescending(false, true, false);
        });
    }
}
