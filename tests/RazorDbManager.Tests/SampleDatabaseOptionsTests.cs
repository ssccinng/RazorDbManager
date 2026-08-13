using RazorDbManager.Core;
using RazorDbManager.Sample;

namespace RazorDbManager.Tests;

public sealed class SampleDatabaseOptionsTests
{
    [Fact]
    public void Defaults_AreReadOnlyWithProtectedBinaryDownload()
    {
        RazorDbCapability capabilities = new SampleDatabaseOptions().EnabledCapabilities();

        Assert.Equal(RazorDbCapabilitySets.ReadOnly | RazorDbCapability.DownloadBinary, capabilities);
        Assert.False(capabilities.Includes(RazorDbCapability.InsertRows));
        Assert.False(capabilities.Includes(RazorDbCapability.ExecuteSql));
        Assert.False(capabilities.Includes(RazorDbCapability.ModifySchema));
    }

    [Fact]
    public void ExplicitSwitches_GrantOnlySelectedCapabilityGroups()
    {
        RazorDbCapability capabilities = new SampleDatabaseOptions
        {
            EnableDataEditing = true,
            EnableExport = true,
            EnableDestructiveSchemaChanges = true,
        }.EnabledCapabilities();

        Assert.True(capabilities.Includes(RazorDbCapabilitySets.DataEditor));
        Assert.True(capabilities.Includes(RazorDbCapability.Export));
        Assert.True(capabilities.Includes(RazorDbCapability.ModifySchema));
        Assert.True(capabilities.Includes(RazorDbCapability.DestructiveSchema));
        Assert.False(capabilities.Includes(RazorDbCapability.Import));
        Assert.False(capabilities.Includes(RazorDbCapability.ExecuteSql));
    }
}
