using RegNeps.OfflineStore.Abstractions;

namespace RegNeps.Mobile.Local;

/// <summary>
/// Material de auth en SecureStorage de MAUI (Keystore en Android). Sin contraseñas.
/// </summary>
public sealed class MauiSecureAuthMaterialStore : ISecureAuthMaterialStore
{
    private static string Key(string userId) => $"regneps.auth.{userId}";

    public async Task SetAuthMaterialAsync(string userId, string material, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        await SecureStorage.Default.SetAsync(Key(userId), material ?? string.Empty);
    }

    public async Task<string?> GetAuthMaterialAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(Key(userId));
        }
        catch
        {
            return null;
        }
    }

    public Task ClearAsync(string userId, CancellationToken ct = default)
    {
        SecureStorage.Default.Remove(Key(userId));
        return Task.CompletedTask;
    }
}
