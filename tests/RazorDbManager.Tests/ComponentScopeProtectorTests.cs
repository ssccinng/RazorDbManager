using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace RazorDbManager.Tests;

public sealed class ComponentScopeProtectorTests
{
    [Fact]
    public void Scope_RoundTripsAndPreservesReadOnlyRestriction()
    {
        RazorDbComponentScopeProtector protector = CreateProtector();

        string writable = protector.Protect("alice", "Main", readOnly: false);
        string readOnly = protector.Protect("alice", "Main", readOnly: true);

        RazorDbComponentScopeValidation writableResult = protector.Validate(writable, "alice", "main");
        RazorDbComponentScopeValidation readOnlyResult = protector.Validate(readOnly, "alice", "Main");
        Assert.True(writableResult.IsValid);
        Assert.False(writableResult.IsReadOnly);
        Assert.True(readOnlyResult.IsValid);
        Assert.True(readOnlyResult.IsReadOnly);
    }

    [Fact]
    public void Scope_RejectsMissingTamperedAndMismatchedBindings()
    {
        RazorDbComponentScopeProtector protector = CreateProtector();
        string token = protector.Protect("alice", "Main", readOnly: false);

        Assert.Equal("component-scope-missing", protector.Validate(null, "alice", "Main").ReasonCode);
        Assert.Equal("component-scope-invalid", protector.Validate(token + "x", "alice", "Main").ReasonCode);
        Assert.Equal("component-scope-actor", protector.Validate(token, "bob", "Main").ReasonCode);
        Assert.Equal("component-scope-database", protector.Validate(token, "alice", "Archive").ReasonCode);
    }

    [Fact]
    public async Task Scope_RejectsExpiredTokenAndAcceptsRefreshedToken()
    {
        RazorDbComponentScopeProtector protector = CreateProtector();
        string expiring = protector.Protect("alice", "Main", readOnly: false, TimeSpan.FromMilliseconds(20));
        await Task.Delay(100);

        RazorDbComponentScopeValidation expired = protector.Validate(expiring, "alice", "Main");
        string refreshed = protector.Protect("alice", "Main", readOnly: false);
        RazorDbComponentScopeValidation current = protector.Validate(refreshed, "alice", "Main");

        Assert.False(expired.IsValid);
        Assert.Equal("component-scope-invalid", expired.ReasonCode);
        Assert.True(current.IsValid);
        Assert.False(current.IsReadOnly);
        Assert.NotEqual(expiring, refreshed);
        Assert.True(RazorDbComponentScopeProtector.RefreshInterval < RazorDbComponentScopeProtector.TokenLifetime);
    }

    private static RazorDbComponentScopeProtector CreateProtector()
    {
        ServiceProvider services = new ServiceCollection()
            .AddDataProtection()
            .Services
            .BuildServiceProvider();
        return new RazorDbComponentScopeProtector(services.GetRequiredService<IDataProtectionProvider>());
    }
}
