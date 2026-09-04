using System.Security.Cryptography;
using Emc.Application.Documents;
using Emc.Domain.Documents;
using Microsoft.Extensions.Options;

namespace Emc.Infrastructure.Documents;

/// <summary>
/// Immutable blob storage on a local filesystem, OUTSIDE the web root.
///
/// Keys are generated: "{category}/{yyyy}/{MM}/{guid}.bin". No segment comes from the uploader;
/// the original filename is never consulted here. Every key is re-validated on read so that a
/// tampered database row cannot point the store outside its root (DOC-006).
///
/// Writes are atomic: the bytes go to "{key}.partial" under an exclusive handle, are flushed to
/// disk, and are then moved into place with no overwrite. A crash leaves a ".partial" file, never
/// a half-written blob under a live key; the orphan sweep removes stale partials. The hash is
/// computed from the bytes as they are written, so what the record says was stored is what the
/// disk holds (AUD-022).
///
/// DEPLOYMENT (docs/architecture.md §9): the root directory is created by the deployer, not the
/// application; the IIS application-pool identity holds Modify on it and nothing else does; it
/// is not under the site's physical path and no static-file provider is mapped to it, so the
/// static-file middleware cannot serve from it; and it is backed up with the database.
/// </summary>
public sealed class FileSystemSourceDocumentStore : ISourceDocumentStore
{
    private readonly string _root;

    public FileSystemSourceDocumentStore(IOptions<SourceDocumentOptions> options)
    {
        var configured = options.Value.RootPath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{SourceDocumentOptions.SectionName}:RootPath is not configured. Source documents cannot be stored.");
        }

        _root = Path.GetFullPath(configured);
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredBlob> WriteAsync(string category, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (category is not ("documents" or "pages" or "ocr-pages"))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown storage category.");
        }

        var now = DateTime.UtcNow;
        var key = $"{category}/{now:yyyy}/{now:MM}/{Guid.NewGuid():N}.bin";
        var final = Resolve(key);
        var partial = final + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);

        long length;
        string hash;

        await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
        using (var sha = SHA256.Create())
        await using (var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write, leaveOpen: true))
        {
            await content.CopyToAsync(hashing, ct);
            await hashing.FlushFinalBlockAsync(ct);
            length = file.Length;
            file.Flush(flushToDisk: true);
            hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }

        File.Move(partial, final, overwrite: false);
        return new StoredBlob(key, hash, length);
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default)
    {
        var path = Resolve(storageKey);
        Stream? stream = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan)
            : null;
        return Task.FromResult(stream);
    }

    public async Task<string?> ComputeSha256Async(string storageKey, CancellationToken ct = default)
    {
        var path = Resolve(storageKey);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
    }

    public Task<bool> TryDeleteAsync(string storageKey, CancellationToken ct = default)
    {
        var path = Resolve(storageKey);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<StoredBlobEntry>> EnumerateAsync(CancellationToken ct = default)
    {
        var entries = new List<StoredBlobEntry>();
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');
            var info = new FileInfo(path);
            if (relative.EndsWith(".bin.partial", StringComparison.Ordinal))
            {
                entries.Add(new StoredBlobEntry(relative[..^".partial".Length], StoredBlobState.Partial, info.LastWriteTimeUtc, info.Length));
            }
            else if (relative.EndsWith(".bin", StringComparison.Ordinal))
            {
                entries.Add(new StoredBlobEntry(relative, StoredBlobState.Committed, info.LastWriteTimeUtc, info.Length));
            }
        }

        return Task.FromResult<IReadOnlyList<StoredBlobEntry>>(entries);
    }

    public Task<bool> TryDeletePartialAsync(string storageKey, CancellationToken ct = default)
    {
        var partial = Resolve(storageKey) + ".partial";
        if (!File.Exists(partial))
        {
            return Task.FromResult(false);
        }

        File.Delete(partial);
        return Task.FromResult(true);
    }

    private string Resolve(string storageKey)
    {
        // Validate the key's shape, then prove the resolved path is inside the root. Both, always:
        // the key may have come from a database row.
        var key = SourceDocument.ValidateStorageKey(storageKey);
        var full = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));

        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Storage key resolves outside the store root.");
        }

        return full;
    }
}
