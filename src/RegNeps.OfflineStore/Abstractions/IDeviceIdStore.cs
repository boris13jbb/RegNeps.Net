namespace RegNeps.OfflineStore.Abstractions;

/// <summary>
/// Identificador de instalación generado por la app (no IMEI/MAC/teléfono).
/// Si el usuario desinstala/reinstala, se genera un DeviceId nuevo.
/// </summary>
public interface IDeviceIdStore
{
    Task<string> GetOrCreateAsync(CancellationToken ct = default);
}
