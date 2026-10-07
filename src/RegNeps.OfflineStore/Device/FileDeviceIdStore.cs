using System.Text.Json;
using RegNeps.OfflineStore.Abstractions;

namespace RegNeps.OfflineStore.Device;

/// <summary>
/// Persiste DeviceId en un archivo de app-data (Guid generado una vez).
/// Desinstalación del APK borra el archivo → nuevo DeviceId.
/// </summary>
public sealed class FileDeviceIdStore : IDeviceIdStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileDeviceIdStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        Directory.CreateDirectory(directoryPath);
        _filePath = Path.Combine(directoryPath, "device-id.json");
    }

    public async Task<string> GetOrCreateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(_filePath))
            {
                await using var read = File.OpenRead(_filePath);
                var existing = await JsonSerializer.DeserializeAsync<DeviceIdDocument>(read, cancellationToken: ct);
                if (!string.IsNullOrWhiteSpace(existing?.DeviceId)
                    && Guid.TryParse(existing.DeviceId, out _))
                {
                    return existing.DeviceId;
                }
            }

            var id = Guid.NewGuid().ToString("N");
            await using var write = File.Create(_filePath);
            await JsonSerializer.SerializeAsync(
                write,
                new DeviceIdDocument { DeviceId = id, CreatedAtUtc = DateTime.UtcNow },
                cancellationToken: ct);
            return id;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class DeviceIdDocument
    {
        public string DeviceId { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
    }
}
