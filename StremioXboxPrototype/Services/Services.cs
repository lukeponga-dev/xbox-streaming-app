using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using StremioXboxPrototype.Models;
using System.Text.Json.Serialization;

namespace StremioXboxPrototype.Services;
public interface ICatalogService { Task<IReadOnlyList<MediaItem>> GetAsync(string collection, CancellationToken token); }
public interface IStreamService { Task<IReadOnlyList<StreamSource>> GetAsync(string mediaId, CancellationToken token); }
public interface ISettingsService { Task<AppSettings> LoadAsync(); Task SaveAsync(AppSettings settings); }
public interface IServerService { Task<ServerInfo?> TestAsync(string url, CancellationToken token); }

[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext { }

// No catalog is invented here: without a configured provider these collections are empty by design.
public sealed class CatalogService : ICatalogService { public Task<IReadOnlyList<MediaItem>> GetAsync(string collection, CancellationToken token) => Task.FromResult<IReadOnlyList<MediaItem>>(Array.Empty<MediaItem>()); }
public sealed class StreamService : IStreamService { public Task<IReadOnlyList<StreamSource>> GetAsync(string mediaId, CancellationToken token) => Task.FromResult<IReadOnlyList<StreamSource>>(Array.Empty<StreamSource>()); }
public sealed class SettingsService : ISettingsService
{
    private const string Key = "app-settings";
    public Task<AppSettings> LoadAsync()
    {
        var json = ApplicationData.Current.LocalSettings.Values[Key] as string;
        if (string.IsNullOrEmpty(json)) return Task.FromResult(new AppSettings());
        try { return Task.FromResult(JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings()); }
        catch (JsonException) { return Task.FromResult(new AppSettings()); }
    }
    public Task SaveAsync(AppSettings settings)
    {
        ApplicationData.Current.LocalSettings.Values[Key] = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
        return Task.CompletedTask;
    }
}
public sealed class ServerService : IServerService
{
    public Task<ServerInfo?> TestAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || (endpoint.Scheme != "http" && endpoint.Scheme != "https")) return Task.FromResult<ServerInfo?>(null);
        return Task.FromResult<ServerInfo?>(new ServerInfo(endpoint.Host, endpoint, "Not tested"));
    }
}
