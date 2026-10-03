using AravalsStream.Core.Settings;
namespace AravalsStream.Core.Interfaces;
public interface ISettingsService { Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default); Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default); }
public interface ISecretStorage { Task StoreAsync(string key, string secret, CancellationToken cancellationToken = default); Task<string?> GetAsync(string key, CancellationToken cancellationToken = default); Task RemoveAsync(string key, CancellationToken cancellationToken = default); }

