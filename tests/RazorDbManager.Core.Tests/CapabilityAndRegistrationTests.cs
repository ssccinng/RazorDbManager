using RazorDbManager.Core;

namespace RazorDbManager.Core.Tests;

public sealed class CapabilityAndRegistrationTests
{
    [Fact]
    public void DataEditor_ContainsOnlyExpectedRowCapabilities()
    {
        RazorDbCapability capabilities = RazorDbCapabilitySets.DataEditor;

        Assert.True(capabilities.Includes(RazorDbCapability.BrowseMetadata));
        Assert.True(capabilities.Includes(RazorDbCapability.ReadRows));
        Assert.True(capabilities.Includes(RazorDbCapability.InsertRows));
        Assert.True(capabilities.Includes(RazorDbCapability.UpdateRows));
        Assert.True(capabilities.Includes(RazorDbCapability.DeleteRows));
        Assert.False(capabilities.Includes(RazorDbCapability.ModifySchema));
        Assert.False(capabilities.Includes(RazorDbCapability.ExecuteSql));
        Assert.False(capabilities.Includes(RazorDbCapability.Import));
        Assert.False(capabilities.Includes(RazorDbCapability.Export));
    }

    [Fact]
    public void Registration_RequiresExplicitHighRiskCredentialByDefault()
    {
        DatabaseRegistration registration = CreateRegistration() with
        {
            EnabledCapabilities = RazorDbCapabilitySets.DataEditor | RazorDbCapability.ModifySchema,
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(registration.Validate);

        Assert.Contains("schema credential", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registration_AllowsExplicitSharedHighRiskCredential()
    {
        DatabaseRegistration registration = CreateRegistration() with
        {
            EnabledCapabilities = RazorDbCapabilitySets.DataEditor
                | RazorDbCapability.ModifySchema
                | RazorDbCapability.ExecuteSql,
            AllowSharedHighRiskCredential = true,
        };

        Assert.Same(registration, registration.Validate());
    }

    [Fact]
    public void Registration_RejectsDestructiveSchemaWithoutSchemaCapability()
    {
        DatabaseRegistration registration = CreateRegistration() with
        {
            EnabledCapabilities = RazorDbCapability.DestructiveSchema,
        };

        Assert.Throws<InvalidOperationException>(registration.Validate);
    }

    [Fact]
    public void Registration_RejectsCaseInsensitiveDuplicateSchemas()
    {
        DatabaseRegistration registration = CreateRegistration() with
        {
            AllowedSchemas = ["sales", "SALES"],
        };

        Assert.Throws<InvalidOperationException>(registration.Validate);
    }

    [Fact]
    public void RuntimeOptions_RejectsNegativeCacheDuration()
    {
        RazorDbRuntimeOptions options = new()
        {
            MetadataCacheDuration = TimeSpan.FromSeconds(-1),
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void ResourceLimits_RejectsDefaultPageLargerThanMaximum()
    {
        RazorDbResourceLimits limits = new()
        {
            DefaultPageSize = 501,
            MaximumPageSize = 500,
        };

        Assert.Throws<InvalidOperationException>(limits.Validate);
    }

    private static DatabaseRegistration CreateRegistration() => new()
    {
        Id = "Main",
        ProviderName = "mysql",
        ConnectionStringName = "MainDatabase",
        EnabledCapabilities = RazorDbCapabilitySets.DataEditor,
        AllowedSchemas = ["app"],
    };
}
