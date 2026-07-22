using System.Collections.Concurrent;

namespace RegNeps.Web.Export;

/// <summary>
/// Almacén efímero de archivos para descarga HTTP (evita enviar PDF/PNG grandes por JSInterop).
/// </summary>
public sealed class TempExportStore
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public Guid Put(byte[] bytes, string contentType, string fileName)
    {
        PurgeExpired();
        var id = Guid.NewGuid();
        _entries[id] = new Entry(bytes, contentType, fileName, DateTime.UtcNow.Add(Ttl));
        return id;
    }

    public bool TryTake(Guid id, out Entry? entry)
    {
        if (_entries.TryRemove(id, out var found) && found.ExpiresUtc > DateTime.UtcNow)
        {
            entry = found;
            return true;
        }

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

    public sealed record Entry(byte[] Bytes, string ContentType, string FileName, DateTime ExpiresUtc);
}
