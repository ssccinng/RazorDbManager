using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using RazorDbManager.Core;

namespace RazorDbManager;

internal sealed class LocalArtifactStore(LocalStorePath paths) : IRazorDbArtifactStore
{
    public async ValueTask<RazorDbArtifactWriteSession> CreateWriteAsync(RazorDbArtifactCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Directory.CreateDirectory(paths.ArtifactRoot);
        string id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        RazorDbArtifactDescriptor descriptor = new(
            id,
            request.DatabaseId,
            request.ActorId,
            SafeFileName(request.FileName),
            request.ContentType,
            null,
            DateTimeOffset.UtcNow,
            request.ExpiresAt,
            SourceResources: request.SourceResources?.ToArray());
        await WriteDescriptorAsync(descriptor, cancellationToken);
        FileStream stream = new(ContentPath(id), FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new RazorDbArtifactWriteSession(descriptor, stream);
    }

    public async ValueTask<RazorDbArtifactDescriptor> CompleteWriteAsync(string artifactId, long length, string sha256, CancellationToken cancellationToken = default)
    {
        RazorDbArtifactDescriptor descriptor = await ReadDescriptorAsync(artifactId, cancellationToken)
            ?? throw new RazorDbException(RazorDbErrorCode.NotFound, "The artifact was not found.");
        if (!File.Exists(ContentPath(artifactId))) throw new RazorDbException(RazorDbErrorCode.NotFound, "The artifact content was not found.");
        RazorDbArtifactDescriptor completed = descriptor with { Length = length, Sha256 = sha256 };
        await WriteDescriptorAsync(completed, cancellationToken);
        return completed;
    }

    public async ValueTask<RazorDbArtifactReadSession?> OpenReadAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        RazorDbArtifactDescriptor? descriptor = await ReadDescriptorAsync(artifactId, cancellationToken);
        if (descriptor?.Length is null || descriptor.ExpiresAt <= DateTimeOffset.UtcNow || !File.Exists(ContentPath(artifactId))) return null;
        FileStream stream = new(ContentPath(artifactId), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new RazorDbArtifactReadSession(descriptor, stream);
    }

    public ValueTask DeleteAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateId(artifactId);
        TryDelete(ContentPath(artifactId));
        TryDelete(DescriptorPath(artifactId));
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.ArtifactRoot)) return 0;
        int removed = 0;
        foreach (string metadataPath in Directory.EnumerateFiles(paths.ArtifactRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RazorDbArtifactDescriptor? descriptor;
            try { descriptor = JsonSerializer.Deserialize<RazorDbArtifactDescriptor>(await File.ReadAllTextAsync(metadataPath, cancellationToken)); }
            catch (JsonException) { descriptor = null; }
            if (descriptor is null || descriptor.ExpiresAt <= now)
            {
                string id = Path.GetFileNameWithoutExtension(metadataPath);
                if (IsValidId(id)) { await DeleteAsync(id, cancellationToken); removed++; }
            }
        }
        return removed;
    }

    private async ValueTask<RazorDbArtifactDescriptor?> ReadDescriptorAsync(string id, CancellationToken cancellationToken)
    {
        ValidateId(id);
        string path = DescriptorPath(id);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<RazorDbArtifactDescriptor>(await File.ReadAllTextAsync(path, cancellationToken));
    }

    private async ValueTask WriteDescriptorAsync(RazorDbArtifactDescriptor descriptor, CancellationToken cancellationToken)
    {
        ValidateId(descriptor.Id);
        Directory.CreateDirectory(paths.ArtifactRoot);
        string target = DescriptorPath(descriptor.Id);
        string temporary = target + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(descriptor), cancellationToken);
        File.Move(temporary, target, true);
    }

    private string ContentPath(string id) => Path.Combine(paths.ArtifactRoot, id + ".bin");
    private string DescriptorPath(string id) => Path.Combine(paths.ArtifactRoot, id + ".json");
    private static void ValidateId(string id) { if (!IsValidId(id)) throw new RazorDbException(RazorDbErrorCode.Validation, "The artifact id is invalid."); }
    private static bool IsValidId(string id) => id.Length == 48 && id.All(Uri.IsHexDigit);
    private static string SafeFileName(string name)
    {
        string input = name ?? string.Empty;
        int separator = input.LastIndexOfAny(['/', '\\']);
        string value = input[(separator + 1)..].Trim();
        if (value is "" or "." or "..") return "artifact.bin";

        string sanitized = string.Concat(value.Where(character =>
            !char.IsControl(character) && character is not '<' and not '>' and not ':' and not '"'
                and not '/' and not '\\' and not '|' and not '?' and not '*'));
        return string.IsNullOrWhiteSpace(sanitized) ? "artifact.bin" : sanitized;
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (FileNotFoundException) { } }
}
