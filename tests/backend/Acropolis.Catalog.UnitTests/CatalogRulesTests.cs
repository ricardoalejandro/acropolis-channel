using Acropolis.Catalog.Application;

namespace Acropolis.Catalog.UnitTests;

public sealed class CatalogRulesTests
{
    private static CreateContentRequest Valid => new("filosofia-viva", "Filosofía viva", "Resumen", "Sinopsis pública", "lecturas");
    [Fact]
    public void CategoriesAndCoverAssetsAreExplicitAndCannotDescribePrivateMedia()
    {
        Assert.Equal(["lecturas", "documentales", "videos", "podcast", "charlas-online", "cursos"], CatalogRules.Categories.Select(x => x.Id));
        Assert.Equal(5, CatalogRules.CoverAssets.Count);
        foreach (var category in CatalogRules.Categories) Assert.True(CatalogRules.ValidCategory(category.Id));
        Assert.False(CatalogRules.ValidCategory(null));
        Assert.False(CatalogRules.ValidCategory("programas"));
        foreach (var cover in CatalogRules.CoverAssets) Assert.Empty(CatalogRules.Validate(Valid with { CoverAsset = cover }));
        Assert.Contains("coverAsset", CatalogRules.Validate(Valid with { CoverAsset = "https://private.example/key" }).Keys);
    }
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("a", false)]
    [InlineData("dos", true)]
    [InlineData("filosofia-2", true)]
    [InlineData("Filosofia", false)]
    [InlineData("-dos", false)]
    [InlineData("dos-", false)]
    [InlineData("dos--tres", false)]
    [InlineData("dos/tres", false)]
    [InlineData("ñandu", false)]
    [InlineData("dos\n", false)]
    [InlineData("dos\r\n", false)]
    public void SlugsUseBoundedStableAsciiLinks(string? slug, bool valid) => Assert.Equal(valid, CatalogRules.ValidSlug(slug));
    [Fact]
    public void SlugAndTextBoundariesAreEnforced()
    {
        Assert.True(CatalogRules.ValidSlug(new string('a', 160)));
        Assert.False(CatalogRules.ValidSlug(new string('a', 161)));
        Assert.Empty(CatalogRules.Validate(Valid with { Title = new string('a', 180), Summary = new string('a', 600), Body = new string('a', 50000), DurationSeconds = 86400 }));
        var errors = CatalogRules.Validate(Valid with { Title = new string('a', 181), Summary = new string('a', 601), Body = new string('a', 50001), DurationSeconds = 86401 });
        Assert.Equal(4, errors.Count);
    }
    [Fact]
    public void DraftAllowsIncompleteSynopsisButPublicationRequiresUsefulPlainText()
    {
        Assert.Empty(CatalogRules.Validate(Valid with { Summary = "", Body = "" }));
        Assert.Equal(2, CatalogRules.Validate(Valid with { Summary = " ", Body = "\n" }, "published").Count);
        Assert.Empty(CatalogRules.Validate(Valid with { Body = "Dos < tres.\nUna sinopsis\tcon espacios." }, "published"));
        var errors = CatalogRules.Validate(Valid with { Slug = null!, Title = null!, Summary = null!, Body = null!, Category = null!, DurationSeconds = 0 });
        Assert.Equal(6, errors.Count);
        Assert.Contains("title", CatalogRules.Validate(Valid with { Title = "x" }).Keys);
        Assert.Contains("title", CatalogRules.Validate(Valid with { Title = "Título\n" }).Keys);
        Assert.Contains("summary", CatalogRules.Validate(Valid with { Summary = "Resumen\t" }).Keys);
        Assert.Contains("body", CatalogRules.Validate(Valid with { Body = "Texto\0" }).Keys);
        Assert.Empty(CatalogRules.Validate(Valid with { DurationSeconds = 1 }));
        Assert.Contains("durationSeconds", CatalogRules.Validate(Valid with { DurationSeconds = -1 }).Keys);
    }
    [Fact]
    public void EveryLifecycleTransitionIsExplicit()
    {
        var allowed = new HashSet<(string, string)> { ("draft", "draft"), ("draft", "published"), ("published", "published"), ("published", "draft"), ("published", "archived"), ("archived", "archived"), ("archived", "draft") };
        foreach (var from in new[] { "draft", "published", "archived" })
            foreach (var to in new[] { "draft", "published", "archived" })
                Assert.Equal(allowed.Contains((from, to)), CatalogRules.CanTransition(from, to));
        Assert.False(CatalogRules.CanTransition("invalid", "draft"));
        Assert.False(CatalogRules.CanTransition("draft", "invalid"));
        Assert.False(CatalogRules.ValidStatus(null));
    }
    [Fact]
    public void UpdateAndQueryValidationRejectUnsupportedChangesAndUnboundedWork()
    {
        var valid = new UpdateContentRequest(new string('a', 32), "published", Valid.Slug, Valid.Title, Valid.Summary, Valid.Body, Valid.Category);
        Assert.Empty(CatalogRules.Validate(valid));
        Assert.Equal(2, CatalogRules.Validate(valid with { Version = null!, Status = "unknown" }).Count);
        Assert.Contains("version", CatalogRules.Validate(valid with { Version = "INVALID" }).Keys);
        Assert.Contains("version", CatalogRules.Validate(valid with { Version = new string('a', 32) + "\n" }).Keys);
        Assert.Empty(CatalogRules.ValidateQuery(null, null, null, 1, 20));
        foreach (var control in new[] { "\0", "\n", "\t" }) Assert.Contains("search", CatalogRules.ValidateQuery("tema" + control, null, null, 1, 20).Keys);
        Assert.Empty(CatalogRules.ValidateQuery(new string('a', 100), "cursos", "archived", 1000000, 100));
        Assert.Equal(5, CatalogRules.ValidateQuery(new string('a', 101), "invalid", "invalid", 0, 0).Count);
        Assert.Equal(2, CatalogRules.ValidateQuery("", null, null, 1000001, 101).Count);
    }
    [Fact]
    public void SearchTreatsSqlWildcardCharactersAsLiteralText()
    {
        Assert.Equal("50\\%\\_\\\\", CatalogRules.EscapeSearch("50%_\\"));
        Assert.Equal("Filosofía", CatalogRules.EscapeSearch("Filosofía"));
    }
    [Fact]
    public void ResultPreservesPublicFailureWithoutConfusingItWithSuccess()
    {
        Assert.True(new CatalogResult<bool>(true).Succeeded);
        var fields = new Dictionary<string, string[]> { ["title"] = ["Título requerido"] };
        var result = CatalogResult<bool>.Fail("validation_error", 400, fields);
        Assert.False(result.Succeeded); Assert.Equal(400, result.Status); Assert.Equal("validation_error", result.Error); Assert.Same(fields, result.FieldErrors);
    }
}
