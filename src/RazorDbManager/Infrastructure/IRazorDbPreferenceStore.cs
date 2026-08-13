namespace RazorDbManager;

internal interface IRazorDbPreferenceStore
{
    ValueTask<string?> GetAsync(
        string actorId,
        string databaseId,
        string key,
        CancellationToken cancellationToken = default);

    ValueTask SetAsync(
        string actorId,
        string databaseId,
        string key,
        string value,
        CancellationToken cancellationToken = default);
}
