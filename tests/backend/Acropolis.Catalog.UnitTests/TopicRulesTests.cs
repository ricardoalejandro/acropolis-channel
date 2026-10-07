using Acropolis.Catalog.Application;

namespace Acropolis.Catalog.UnitTests;

public sealed class TopicRulesTests
{
    [Theory]
    [InlineData(null, false)] [InlineData("", false)] [InlineData(" ", false)] [InlineData("A", false)]
    [InlineData("Ideas", true)] [InlineData("  Ideas  ", true)] [InlineData("Ideas\n", false)]
    [InlineData("Ideas\t", false)] [InlineData("Ideas\0", false)]
    public void NamesAreBoundedSingleLinePlainText(string? value, bool valid) => Assert.Equal(valid, TopicRules.ValidName(value));
    [Fact]
    public void BoundariesAndStableSlugRulesMatchCatalog()
    {
        Assert.Empty(TopicRules.Validate(new CreateTopicRequest(new string('a', 160), new string('b', 180))));
        Assert.Equal(2, TopicRules.Validate(new CreateTopicRequest(new string('a', 161), new string('b', 181))).Count);
        Assert.Contains("slug", TopicRules.Validate(new CreateTopicRequest("Mayusculas", "Tema")).Keys);
        Assert.Contains("slug", TopicRules.Validate(new CreateTopicRequest("tema\n", "Tema")).Keys);
        Assert.Equal(6, CatalogRules.Categories.Count); Assert.False(CatalogRules.ValidCategory("temas"));
    }
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("INVALID")] [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")]
    public void VersionsAreExactTokens(string? value) => Assert.False(TopicRules.ValidVersion(value));
    [Fact]
    public void UpdateStateAndOrderingValidateOnlyTheirOwnFields()
    {
        var version = new string('a', 32); var id = Guid.NewGuid(); var other = Guid.NewGuid();
        Assert.Empty(TopicRules.Validate(new UpdateTopicRequest(version, "Nombre")));
        Assert.Equal(2, TopicRules.Validate(new UpdateTopicRequest("", "")).Count);
        foreach (var status in new[] { "active", "archived" }) Assert.Empty(TopicRules.Validate(new TopicStateRequest(version, status)));
        Assert.Equal(2, TopicRules.Validate(new TopicStateRequest("", "draft")).Count);
        Assert.Empty(TopicRules.Validate(new MoveTopicRequest(version, id, other)));
        Assert.Empty(TopicRules.Validate(new MoveTopicRequest(version, id, null)));
        Assert.Contains("id", TopicRules.Validate(new MoveTopicRequest(version, id, id)).Keys);
        Assert.Contains("id", TopicRules.Validate(new MoveTopicRequest(version, Guid.Empty, other)).Keys);
        Assert.Contains("id", TopicRules.Validate(new MoveTopicRequest(version, id, Guid.Empty)).Keys);
        Assert.Contains("directoryVersion", TopicRules.Validate(new MoveTopicRequest("", id, other)).Keys);
    }
    [Fact]
    public void AssociationIsAnOptionalDistinctSetOfAtMostTwelveIds()
    {
        var version = new string('a', 32); var twelve = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        Assert.Empty(TopicRules.Validate(new AssignContentTopicsRequest(version, [])));
        Assert.Empty(TopicRules.Validate(new AssignContentTopicsRequest(version, twelve)));
        Assert.Contains("topicIds", TopicRules.Validate(new AssignContentTopicsRequest(version, [.. twelve, Guid.NewGuid()])).Keys);
        Assert.Contains("topicIds", TopicRules.Validate(new AssignContentTopicsRequest(version, [twelve[0], twelve[0]])).Keys);
        Assert.Contains("topicIds", TopicRules.Validate(new AssignContentTopicsRequest(version, [Guid.Empty])).Keys);
        Assert.Contains("topicIds", TopicRules.Validate(new AssignContentTopicsRequest(version, null!)).Keys);
        Assert.Contains("contentVersion", TopicRules.Validate(new AssignContentTopicsRequest("", twelve)).Keys);
    }
    [Fact]
    public void DirectoryQueryIsBoundedAndCannotTreatFormatsAsTopicStates()
    {
        Assert.Empty(TopicRules.ValidateQuery(null, null, 1, 20));
        Assert.Empty(TopicRules.ValidateQuery(new string('x', 100), "archived", 1000000, 100));
        Assert.Equal(4, TopicRules.ValidateQuery(new string('x', 101), "published", 0, 101).Count);
        Assert.Contains("search", TopicRules.ValidateQuery("x\0", null, 1, 20).Keys);
    }
}
