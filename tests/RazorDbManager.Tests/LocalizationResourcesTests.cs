using System.Collections;
using System.Globalization;
using System.Resources;
using RazorDbManager.Core;
using RazorDbManager.Resources;

namespace RazorDbManager.Tests;

public sealed class LocalizationResourcesTests
{
    [Fact]
    public void NeutralAndSimplifiedChineseResourcesHaveMatchingKeys()
    {
        ResourceManager manager = new(typeof(RazorDbManagerResources));
        HashSet<string> neutral = Keys(manager.GetResourceSet(CultureInfo.InvariantCulture, true, true));
        HashSet<string> simplifiedChinese = Keys(manager.GetResourceSet(CultureInfo.GetCultureInfo("zh-CN"), true, false));

        Assert.Equal(neutral.Order(StringComparer.Ordinal), simplifiedChinese.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DynamicallyRenderedEnumsHaveLocalizedResources()
    {
        ResourceManager manager = new(typeof(RazorDbManagerResources));
        HashSet<string> neutral = Keys(manager.GetResourceSet(CultureInfo.InvariantCulture, true, true));

        IEnumerable<string> expected = Enum.GetNames<RazorDbJobKind>()
            .Concat(Enum.GetNames<RazorDbJobStatus>())
            .Concat(Enum.GetNames<RazorDbAuditStatus>())
            .Concat(Enum.GetNames<RazorDbOperation>())
            .Concat(Enum.GetNames<DbObjectKind>())
            .Concat([nameof(DbValueKind.Binary), nameof(DbValueKind.Geometry)]);

        Assert.DoesNotContain(expected.Distinct(StringComparer.Ordinal), key => !neutral.Contains(key));
    }

    [Fact]
    public void SimplifiedChineseStartedStatusUsesOutcomeWording()
    {
        ResourceManager manager = new(typeof(RazorDbManagerResources));

        Assert.Equal("已开始", manager.GetString("Started", CultureInfo.GetCultureInfo("zh-CN")));
    }

    private static HashSet<string> Keys(ResourceSet? resources)
    {
        Assert.NotNull(resources);
        return resources.Cast<DictionaryEntry>()
            .Select(entry => Assert.IsType<string>(entry.Key))
            .ToHashSet(StringComparer.Ordinal);
    }
}
