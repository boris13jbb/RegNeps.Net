using System.Collections.Concurrent;

namespace RegNeps.Web.Export;

/// <summary>
/// Almacén efímero de archivos para descarga HTTP (evita enviar PDF/PNG grandes por JSInterop).
/// Cada entrada queda ligada al usuario que la generó.
/// </summary>
public sealed class TempExportStore
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public Guid Put(byte[] bytes, string contentType, string fileName, string? ownerUserId)
    {
        PurgeExpired();
        var id = Guid.NewGuid();
        _entries[id] = new Entry(
            bytes,
            contentType,
            fileName,
            DateTime.UtcNow.Add(Ttl),
            ownerUserId ?? string.Empty);
        return id;
    }

    public bool TryTake(Guid id, string? requesterUserId, out Entry? entry)
    {
        if (_entries.TryRemove(id, out var found) &&
            found.ExpiresUtc > DateTime.UtcNow &&
            string.Equals(found.OwnerUserId, requesterUserId ?? string.Empty, StringComparison.Ordinal))
        {
            entry = found;
            return true;
        }

        // Si existía pero no era del solicitante, no reinsertar (evita reintentos de IDOR).
        entry = null;
        return false;
    }

    private void PurgeExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _entries)
        {
            if (kv.Value.ExpiresUtc <= now)
            {
                _entries.TryRemove(kv.Key, out _);
            }
        }
    }

    public sealed record Entry(
        byte[] Bytes,
        string ContentType,
        string FileName,
        DateTime ExpiresUtc,
        string OwnerUserId);
}
