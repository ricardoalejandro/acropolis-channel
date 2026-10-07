using Acropolis.Catalog.Application;

namespace Acropolis.Catalog.UnitTests;

public sealed class WorkRulesTests
{
    private static CreateContentRequest Reading => new("lectura-restringida", "Una lectura", "Resumen público", "Sinopsis pública", "lecturas");
    [Theory]
    [InlineData("dQw4w9WgXcQ", true)]
    [InlineData("a_B-0123456", true)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", false)]
    [InlineData("dQw4w9WgXcQ?autoplay=1", false)]
    [InlineData("<iframe>", false)]
    [InlineData("1234567890", false)]
    [InlineData("123456789012", false)]
    [InlineData("12345678901\n", false)]
    [InlineData(null, false)]
    public void YouTubeAcceptsOnlyAProviderIdentifier(string? value, bool valid) => Assert.Equal(valid, CatalogRules.ValidYouTubeId(value));
    [Fact]
    public void WorkPayloadsAreCategorySpecificAndPlainTextIsBounded()
    {
        Assert.Empty(CatalogRules.Validate(Reading with { WorkText = "Dos < tres.\nTexto\tseguro." }));
        Assert.Empty(CatalogRules.Validate(Reading with { WorkText = new string('a', 500000) }));
        foreach (var invalid in new[] { "", " ", "Texto\0", new string('a', 500001) })
            Assert.Contains("workText", CatalogRules.Validate(Reading with { WorkText = invalid }).Keys);
        Assert.Contains("workText", CatalogRules.Validate(Reading with { Category = "videos", WorkText = "No es una lectura." }).Keys);
        Assert.Contains("youTubeId", CatalogRules.Validate(Reading with { YouTubeId = "dQw4w9WgXcQ" }).Keys);
        foreach (var category in new[] { "videos", "podcast", "documentales", "charlas-online" })
        {
            Assert.True(CatalogRules.VideoCategory(category));
            Assert.Empty(CatalogRules.Validate(Reading with { Category = category, YouTubeId = "dQw4w9WgXcQ" }));
            Assert.Contains("youTubeId", CatalogRules.Validate(Reading with { Category = category, YouTubeId = "https://youtu.be/dQw4w9WgXcQ" }).Keys);
        }
        Assert.False(CatalogRules.VideoCategory("lecturas"));
        Assert.False(CatalogRules.VideoCategory(null));
        Assert.Empty(CatalogRules.Validate(Reading, "published")); // Legacy metadata remains publishable.
    }
    [Fact]
    public void PublicMetadataIsBoundedAndTagsCannotRepeatAfterNormalization()
    {
        Assert.Empty(CatalogRules.Validate(Reading with { Author = new string('a', 180), Tags = Enumerable.Range(0, 12).Select(x => "etiqueta-" + x).ToArray() }));
        foreach (var invalid in new[] { "", "Autor\n", new string('a', 181) })
            Assert.Contains("author", CatalogRules.Validate(Reading with { Author = invalid }).Keys);
        Assert.Contains("tags", CatalogRules.Validate(Reading with { Tags = ["Filosofía", " filosofía "] }).Keys);
        foreach (var tags in new[] { new[] { "" }, new[] { "Tag\n" }, new string[] { null! }, new[] { new string('a', 41) }, Enumerable.Range(0, 13).Select(x => "t" + x).ToArray() })
            Assert.Contains("tags", CatalogRules.Validate(Reading with { Tags = tags }).Keys);
    }
    [Fact]
    public void CollectionsKeepSixCategoriesAndBoundedDistinctOrderedReferences()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var course = Reading with { Category = "cursos", CollectionKind = "course", ItemIds = ids };
        Assert.Empty(CatalogRules.Validate(course, "published"));
        Assert.Empty(CatalogRules.Validate(course with { CollectionKind = "program" }, "published"));
        Assert.Empty(CatalogRules.Validate(course with { ItemIds = [] }));
        Assert.Contains("itemIds", CatalogRules.Validate(course with { ItemIds = [] }, "published").Keys);
        Assert.Contains("itemIds", CatalogRules.Validate(course with { ItemIds = [ids[0], ids[0]] }).Keys);
        Assert.Contains("itemIds", CatalogRules.Validate(course with { ItemIds = [Guid.Empty] }).Keys);
        Assert.Contains("itemIds", CatalogRules.Validate(course with { ItemIds = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray() }).Keys);
        Assert.Contains("itemIds", CatalogRules.Validate(Reading with { ItemIds = ids }).Keys);
        Assert.Contains("collectionKind", CatalogRules.Validate(course with { CollectionKind = "other" }).Keys);
        Assert.Contains("collectionKind", CatalogRules.Validate(course with { Category = "lecturas" }).Keys);
        Assert.Contains("workText", CatalogRules.Validate(course with { WorkText = "Restricción de colección" }).Keys);
        Assert.Contains("youTubeId", CatalogRules.Validate(course with { YouTubeId = "dQw4w9WgXcQ" }).Keys);
        var update = new UpdateContentRequest(new string('a', 32), "published", course.Slug, course.Title, course.Summary, course.Body, course.Category, CollectionKind: course.CollectionKind, ItemIds: ids);
        Assert.Empty(CatalogRules.Validate(update));
        Assert.Contains("itemIds", CatalogRules.Validate(update with { ItemIds = [] }).Keys);
        Assert.Equal(6, CatalogRules.Categories.Count);
        Assert.False(CatalogRules.ValidCategory("programas"));
    }
    [Fact]
    public void AuditQueryIsReadOnlyBoundedAndUsesExactUtcActionAndObjectFilters()
    {
        var from = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        Assert.Empty(CatalogRules.ValidateAuditQuery(from, from.AddDays(1), "content.published", Guid.NewGuid(), 1, 100));
        Assert.Empty(CatalogRules.ValidateAuditQuery(null, null, null, null, 1, 20));
        Assert.Contains("toUtc", CatalogRules.ValidateAuditQuery(from, from.AddDays(-1), null, null, 1, 20).Keys);
        Assert.Contains("fromUtc", CatalogRules.ValidateAuditQuery(from.ToOffset(TimeSpan.FromHours(5)), null, null, null, 1, 20).Keys);
        Assert.Contains("toUtc", CatalogRules.ValidateAuditQuery(null, from.ToOffset(TimeSpan.FromHours(5)), null, null, 1, 20).Keys);
        Assert.Equal(4, CatalogRules.ValidateAuditQuery(null, null, "unknown", Guid.Empty, 0, 101).Count);
        foreach (var action in CatalogRules.AuditActions)
            Assert.Empty(CatalogRules.ValidateAuditQuery(null, null, action, null, 1, 20));
    }

}
