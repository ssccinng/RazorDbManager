using Microsoft.Extensions.Hosting;
using RazorDbManager.Core;
using RazorDbManager.MySql.Configuration;

namespace RazorDbManager.MySql.Infrastructure;

internal sealed class MySqlStartupValidator(
    IEnumerable<MySqlRegistrationDescriptor> descriptors,
    IRazorDbCredentialProvider credentialProvider,
    IHostEnvironment environment) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        foreach (var descriptor in descriptors)
        {
            new MySqlProviderOptionsValidator().Validate(descriptor.Registration.Id, descriptor.Options);
            var validator = new MySqlCredentialValidator(descriptor.Registration, descriptor.Options, environment);
            var source = new MySqlCredentialSource(descriptor.Registration, credentialProvider, validator);
            foreach (var slot in RequiredSlots(descriptor.Options))
            {
                _ = await source.GetConnectionStringAsync(slot, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static IReadOnlyCollection<MySqlCredentialSlot> RequiredSlots(MySqlProviderOptions options)
    {
        var slots = new HashSet<MySqlCredentialSlot> { MySqlCredentialSlot.Reader };
        var capabilities = options.EnabledCapabilities;
        if ((capabilities & (RazorDbCapability.InsertRows
            | RazorDbCapability.UpdateRows
            | RazorDbCapability.DeleteRows
            | RazorDbCapability.Import)) != 0)
            slots.Add(MySqlCredentialSlot.Writer);
        if ((capabilities & (RazorDbCapability.ModifySchema | RazorDbCapability.DestructiveSchema)) != 0)
            slots.Add(MySqlCredentialSlot.Schema);
        if (capabilities.Includes(RazorDbCapability.ExecuteSql))
            slots.Add(MySqlCredentialSlot.SqlConsole);
        return slots;
    }
}
