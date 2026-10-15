using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using StremioXboxPrototype.Models;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;
using Windows.Storage;

namespace StremioXboxPrototype.Services;

public interface ICatalogService { Task<IReadOnlyList<MediaItem>> GetAsync(string type, string? search, CancellationToken token); }
public interface IMetadataService { Task<MediaMetadata> GetMetadataAsync(MediaItem item, CancellationToken token); }
public interface IStreamService { Task<IReadOnlyList<StreamSource>> GetStreamsAsync(MediaItem item, CancellationToken token); }
public interface IAddonService
{
    Task<IReadOnlyList<AddonManifest>> GetInstalledAsync(CancellationToken token);
    Task<AddonManifest> InstallAsync(string manifestUrl, CancellationToken token);
    Task RemoveAsync(Uri manifestUri);
    Task<int> SyncFromAccountAsync(string authKey, CancellationToken token);
}
public interface IDiagnosticsService
{
    IReadOnlyList<DiagnosticEvent> Events { get; }
    void Record(string level, string message, string detail = "");
    string InternetStatus();
}
public interface ISettingsService { Task<AppSettings> LoadAsync(); Task SaveAsync(AppSettings settings); }
public interface IAuthService
{
    Task<StremioAccount?> GetCurrentAsync();
    Task<StremioAccount> LoginAsync(string email, string password, CancellationToken token);
    FacebookLoginRequest CreateFacebookLogin();
    Task<StremioAccount> CompleteFacebookLoginAsync(string state, CancellationToken token);
    string? GetSessionKey();
    Task LogoutAsync(CancellationToken token);
}
public interface IServerService { Task<ServerInfo?> TestAsync(string url, CancellationToken token); }
public interface ILibraryService
{
    IReadOnlyList<MediaItem> Watchlist();
    bool Contains(MediaItem item);
    void SetSaved(MediaItem item, bool saved);
}

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<MediaItem>))]
internal partial class SettingsJsonContext : JsonSerializerContext { }

public sealed class DiagnosticsService : IDiagnosticsService
{
    private readonly List<DiagnosticEvent> _events = new();
    public IReadOnlyList<DiagnosticEvent> Events => _events;

    public void Record(string level, string message, string detail = "")
    {
        _events.Insert(0, new DiagnosticEvent(DateTimeOffset.Now, level, message, detail));
        if (_events.Count > 100) _events.RemoveAt(_events.Count - 1);
    }

    public string InternetStatus()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            return profile?.GetNetworkConnectivityLevel() switch
            {
                NetworkConnectivityLevel.InternetAccess => "Connected",
                NetworkConnectivityLevel.ConstrainedInternetAccess => "Limited",
                NetworkConnectivityLevel.LocalAccess => "Local network only",
                _ => "Offline"
            };
        }
        catch (Exception ex)
        {
            Record("WARN", "Network probe unavailable", ex.Message);
            return "Unavailable";
        }
    }
}

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

public sealed class StremioAuthService : IAuthService
{
    private const string Endpoint = "https://api.strem.io/api/";
    private const string AccountKey = "stremio-account";
    private const string VaultResource = "StremioXbox.Stremio.Auth";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

    public Task<StremioAccount?> GetCurrentAsync()
    {
        var json = ApplicationData.Current.LocalSettings.Values[AccountKey] as string;
        if (string.IsNullOrWhiteSpace(json) || GetAuthKey() is null) return Task.FromResult<StremioAccount?>(null);
        try { return Task.FromResult<StremioAccount?>(JsonSerializer.Deserialize<StremioAccount>(json)); }
        catch (JsonException) { return Task.FromResult<StremioAccount?>(null); }
    }

    public async Task<StremioAccount> LoginAsync(string email, string password, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Enter both your Stremio email and password.");

        return await AuthenticateAsync(new { authKey = (string?)null, email = email.Trim(), password }, email.Trim(), token);
    }

    public FacebookLoginRequest CreateFacebookLogin()
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return new FacebookLoginRequest(state, new Uri("https://www.strem.io/login-fb/" + state));
    }

    public async Task<StremioAccount> CompleteFacebookLoginAsync(string state, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(state)) throw new ArgumentException("Facebook sign-in did not start correctly.");
        var (email, facebookToken) = await WaitForFacebookCredentialsAsync(state, token);
        return await AuthenticateAsync(new { type = "Login", email, password = facebookToken, facebook = true }, email, token);
    }

    private async Task<StremioAccount> AuthenticateAsync(object payload, string fallbackIdentity, CancellationToken token)
    {
        var requestBody = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint + "login")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Stremio sign-in is unavailable. Please try again.");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            throw new InvalidOperationException(ErrorText(error) ?? "Stremio rejected the sign-in request.");
        if (!root.TryGetProperty("result", out var result)) throw new JsonException("Stremio returned an unexpected sign-in response.");
        var authKey = Value(result, "authKey") ?? throw new JsonException("Stremio did not return a session key.");
        if (!result.TryGetProperty("user", out var user)) throw new JsonException("Stremio did not return a user profile.");
        var account = new StremioAccount(Value(user, "_id") ?? fallbackIdentity,
            Value(user, "username") ?? Value(user, "email") ?? fallbackIdentity, Value(user, "email") ?? fallbackIdentity);

        SaveAuthKey(account.Id, authKey);
        ApplicationData.Current.LocalSettings.Values[AccountKey] = JsonSerializer.Serialize(account);
        return account;
    }

    private static async Task<(string Email, string Token)> WaitForFacebookCredentialsAsync(string state, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var response = await Client.GetAsync("https://www.strem.io/login-fb-get-acc/" + state, token);
                var body = await response.Content.ReadAsStringAsync(token);
                if (response.IsSuccessStatusCode && TryReadFacebookCredentials(body, out var email, out var facebookToken))
                    return (email, facebookToken);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (HttpRequestException) { }
            catch (JsonException) { }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
            await Task.Delay(1000, token);
        }
        throw new TimeoutException("Facebook sign-in timed out. Start it again and finish in the browser.");
    }

    private static bool TryReadFacebookCredentials(string body, out string email, out string facebookToken)
    {
        email = "";
        facebookToken = "";
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
            return false;
        email = Value(user, "email") ?? "";
        facebookToken = Value(user, "fbLoginToken") ?? "";
        return email.Length > 0 && facebookToken.Length > 0;
    }

    public string? GetSessionKey() => GetAuthKey();

    public async Task LogoutAsync(CancellationToken token)
    {
        var authKey = GetAuthKey();
        if (!string.IsNullOrWhiteSpace(authKey))
        {
            try
            {
                var body = JsonSerializer.Serialize(new { authKey });
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint + "logout")
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                using var response = await Client.SendAsync(request, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (HttpRequestException) { }
        }
        ClearAuthKey();
        ApplicationData.Current.LocalSettings.Values.Remove(AccountKey);
    }

    private static string? GetAuthKey()
    {
        try
        {
            var credential = new PasswordVault().RetrieveAll().FirstOrDefault(c => c.Resource == VaultResource);
            if (credential is null) return null;
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception) { return null; }
    }

    private static void SaveAuthKey(string userId, string authKey)
    {
        ClearAuthKey();
        new PasswordVault().Add(new PasswordCredential(VaultResource, userId, authKey));
    }

    private static void ClearAuthKey()
    {
        try
        {
            var vault = new PasswordVault();
            foreach (var credential in vault.RetrieveAll().Where(c => c.Resource == VaultResource)) vault.Remove(credential);
        }
        catch (Exception) { }
    }

    private static string? ErrorText(JsonElement error) => error.ValueKind switch
    {
        JsonValueKind.String => error.GetString(),
        JsonValueKind.Object when Value(error, "message") is { } message => message,
        _ => null
    };

    private static string? Value(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed class LibraryService : ILibraryService
{
    private const string Key = "watchlist";
    private static string WatchlistFilePath =>
        System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "watchlist.json");

    public IReadOnlyList<MediaItem> Watchlist()
    {
        try
        {
            var path = WatchlistFilePath;
            if (System.IO.File.Exists(path))
            {
                var fileJson = System.IO.File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(fileJson))
                    return JsonSerializer.Deserialize(fileJson, SettingsJsonContext.Default.ListMediaItem) ?? new List<MediaItem>();
            }
        }
        catch { }

        try
        {
            var json = ApplicationData.Current.LocalSettings.Values[Key] as string;
            if (!string.IsNullOrWhiteSpace(json))
                return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.ListMediaItem) ?? new List<MediaItem>();
        }
        catch { }

        return Array.Empty<MediaItem>();
    }

    public bool Contains(MediaItem item) => Watchlist().Any(saved => saved.Type == item.Type && saved.Id == item.Id);

    public void SetSaved(MediaItem item, bool saved)
    {
        var items = Watchlist().ToList();
        items.RemoveAll(existing => existing.Type == item.Type && existing.Id == item.Id);
        if (saved) items.Insert(0, item);
        var json = JsonSerializer.Serialize(items, SettingsJsonContext.Default.ListMediaItem);
        try
        {
            System.IO.File.WriteAllText(WatchlistFilePath, json);
        }
        catch { }
        try
        {
            ApplicationData.Current.LocalSettings.Values.Remove(Key);
        }
        catch { }
    }
}

public sealed class ServerService : IServerService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(8) };
    public async Task<ServerInfo?> TestAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            return new ServerInfo(endpoint.Host, endpoint, "Reachable", DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return new ServerInfo(endpoint.Host, endpoint, "Unreachable"); }
        catch (TaskCanceledException) { return new ServerInfo(endpoint.Host, endpoint, "Timed out"); }
    }
}

public sealed class StremioService : IAddonService, ICatalogService, IMetadataService, IStreamService
{
    private const string AddonKey = "addon-manifest-urls";
    private const string Api = "https://api.strem.io/api/";
    private static readonly HttpClient Client = CreateClient();
    private readonly IDiagnosticsService _diagnostics;
    private IReadOnlyList<AddonManifest>? _installedCache;

    public StremioService(IDiagnosticsService diagnostics) => _diagnostics = diagnostics;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Stremio/4.4 (Xbox)");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return client;
    }

    public async Task<AddonManifest> InstallAsync(string manifestUrl, CancellationToken token)
    {
        var uri = NormalizeManifestUri(manifestUrl);
        var manifest = await LoadManifestAsync(uri, token);
        var urls = SavedUrls();
        if (!urls.Contains(uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase))
        {
            urls.Add(uri.AbsoluteUri);
            SaveUrls(urls);
        }
        _installedCache = null;
        _diagnostics.Record("INFO", "Add-on installed", manifest.Name);
        return manifest;
    }

    public Task RemoveAsync(Uri manifestUri)
    {
        var urls = SavedUrls();
        urls.RemoveAll(url => string.Equals(url, manifestUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase));
        SaveUrls(urls);
        _installedCache = null;
        _diagnostics.Record("INFO", "Add-on removed", manifestUri.Host);
        return Task.CompletedTask;
    }

    public async Task<int> SyncFromAccountAsync(string authKey, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(authKey)) throw new ArgumentException("Sign in to Stremio before syncing add-ons.");
        var body = JsonSerializer.Serialize(new { type = "AddonCollectionGet", authKey, update = true });
        using var request = new HttpRequestMessage(HttpMethod.Post, Api + "addonCollectionGet")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        var json = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Stremio add-on sync is unavailable. Please try again.");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            throw new InvalidOperationException(Value(error, "message") ?? "Stremio rejected the add-on sync.");
        if (!root.TryGetProperty("result", out var result)) throw new JsonException("Stremio returned an unexpected add-on collection.");

        var cloud = new List<string>();
        JsonElement addons = default;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("addons", out addons) && addons.ValueKind == JsonValueKind.Array)
        { }
        else if (result.ValueKind == JsonValueKind.Array) addons = result;
        else throw new JsonException("Stremio did not return an add-on list.");

        foreach (var addon in addons.EnumerateArray())
        {
            var url = TransportUrl(addon);
            if (url is null) continue;
            try
            {
                var uri = NormalizeManifestUri(url);
                if (!cloud.Contains(uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)) cloud.Add(uri.AbsoluteUri);
            }
            catch (ArgumentException) { }
        }

        if (cloud.Count == 0)
        {
            _diagnostics.Record("WARN", "Add-on sync", "The Stremio account did not return any add-on URLs.");
            return 0;
        }

        var merged = cloud.ToList();
        foreach (var local in SavedUrls())
            if (!merged.Contains(local, StringComparer.OrdinalIgnoreCase)) merged.Add(local);
        SaveUrls(merged);
        _installedCache = null;
        _diagnostics.Record("INFO", "Add-ons synced from Stremio", $"{cloud.Count} from account, {merged.Count} total");
        return cloud.Count;
    }

    public async Task<IReadOnlyList<AddonManifest>> GetInstalledAsync(CancellationToken token)
    {
        if (_installedCache is not null) return _installedCache;
        var result = new List<AddonManifest>();
        foreach (var url in SavedUrls())
        {
            token.ThrowIfCancellationRequested();
            var uri = new Uri(url);
            try { result.Add(await LoadManifestAsync(uri, token)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
            {
                _diagnostics.Record("WARN", "Add-on unavailable", $"{uri.Host}: {ex.Message}");
                result.Add(new AddonManifest(uri.AbsoluteUri, uri.Host, "Manifest unavailable", uri,
                    Array.Empty<CatalogDefinition>(), false, false));
            }
        }
        _installedCache = result;
        return result;
    }

    public async Task<IReadOnlyList<MediaItem>> GetAsync(string type, string? search, CancellationToken token)
    {
        var items = new List<MediaItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var addon in await GetInstalledAsync(token))
        foreach (var catalog in addon.Catalogs.Where(c => c.Type == type && (search is null || c.SupportsSearch)))
        {
            try
            {
                var suffix = search is null ? "" : "/search=" + Uri.EscapeDataString(search);
                using var json = await GetJsonAsync(ResourceUri(addon.ManifestUri, "catalog", type, catalog.Id, suffix), token);
                if (!json.RootElement.TryGetProperty("metas", out var metas) || metas.ValueKind != JsonValueKind.Array) continue;
                foreach (var meta in metas.EnumerateArray())
                {
                    var item = ParseMedia(meta, addon.Id, type);
                    if (item is not null && seen.Add(item.Type + ":" + item.Id)) items.Add(item);
                    if (items.Count >= 80) return items;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            { _diagnostics.Record("WARN", "Catalog failed", $"{addon.Name}: {ex.Message}"); }
        }
        return items;
    }

    public async Task<MediaMetadata> GetMetadataAsync(MediaItem item, CancellationToken token)
    {
        var addons = (await GetInstalledAsync(token)).Where(a => a.HasMetadata)
            .OrderByDescending(a => a.Id == item.AddonId);
        foreach (var addon in addons)
        {
            try
            {
                using var json = await GetJsonAsync(ResourceUri(addon.ManifestUri, "meta", item.Type, item.Id), token);
                if (!json.RootElement.TryGetProperty("meta", out var meta)) continue;
                var detailed = ParseMedia(meta, addon.Id, item.Type) ?? item;
                var episodes = new List<EpisodeItem>();
                if (meta.TryGetProperty("videos", out var videos) && videos.ValueKind == JsonValueKind.Array)
                foreach (var video in videos.EnumerateArray())
                {
                    var id = Value(video, "id");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    episodes.Add(new EpisodeItem(id, Number(video, "season"), Number(video, "episode"),
                        Value(video, "title") ?? $"Episode {Number(video, "episode")}"));
                }
                return new MediaMetadata(detailed, episodes);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            { _diagnostics.Record("WARN", "Metadata failed", $"{addon.Name}: {ex.Message}"); }
        }
        return new MediaMetadata(item, Array.Empty<EpisodeItem>());
    }

    public async Task<IReadOnlyList<StreamSource>> GetStreamsAsync(MediaItem item, CancellationToken token)
    {
        var server = StreamingServerBase();
        var addons = (await GetInstalledAsync(token)).Where(a => a.HasStreams).ToList();
        var batches = await Task.WhenAll(addons.Select(addon => GetAddonStreamsAsync(addon, item, server, token)));
        return batches.SelectMany(batch => batch).ToList();
    }

    private async Task<IReadOnlyList<StreamSource>> GetAddonStreamsAsync(AddonManifest addon, MediaItem item, Uri? server, CancellationToken token)
    {
        try
        {
            using var json = await TryGetJsonAsync(ResourceUri(addon.ManifestUri, "stream", item.Type, item.Id), token);
            if (json is null || !json.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
                return Array.Empty<StreamSource>();
            var sources = new List<StreamSource>();
            foreach (var stream in streams.EnumerateArray())
            {
                var playable = ResolveStreamUri(stream, server);
                var title = Value(stream, "title") ?? Value(stream, "description");
                sources.Add(new StreamSource(
                    Value(stream, "name") ?? addon.Name,
                    title ?? (playable is null ? "Requires a streaming server for torrent or magnet sources" : "Direct stream"),
                    playable, addon.Name, Value(stream, "name")));
            }
            return sources;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _diagnostics.Record("WARN", "Streams failed", $"{addon.Name}: {ex.Message}");
            return Array.Empty<StreamSource>();
        }
    }

    private static Uri NormalizeManifestUri(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
            trimmed = "https://" + trimmed["stremio://".Length..];
        else if (trimmed.StartsWith("stremio:", StringComparison.OrdinalIgnoreCase))
            trimmed = "https://" + trimmed["stremio:".Length..].TrimStart('/');

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Enter an HTTP or HTTPS add-on URL.");
        return uri.AbsolutePath.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase)
            ? uri : new Uri(uri.AbsoluteUri.TrimEnd('/') + "/manifest.json");
    }

    private async Task<AddonManifest> LoadManifestAsync(Uri uri, CancellationToken token)
    {
        using var json = await GetJsonAsync(uri, token);
        var root = json.RootElement;
        var id = Value(root, "id") ?? throw new JsonException("Manifest is missing id.");
        var name = Value(root, "name") ?? id;
        var catalogs = new List<CatalogDefinition>();
        if (root.TryGetProperty("catalogs", out var list) && list.ValueKind == JsonValueKind.Array)
        foreach (var catalog in list.EnumerateArray())
        {
            var type = Value(catalog, "type");
            var catalogId = Value(catalog, "id");
            if (type is null || catalogId is null) continue;
            var searchable = CatalogSupportsSearch(catalog);
            catalogs.Add(new CatalogDefinition(type, catalogId, Value(catalog, "name") ?? catalogId, searchable));
        }
        return new AddonManifest(id, name, Value(root, "description") ?? "Stremio add-on", uri,
            catalogs, HasResource(root, "meta"), HasResource(root, "stream"));
    }

    private static bool HasResource(JsonElement root, string resource)
    {
        if (!root.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array) return false;
        return resources.EnumerateArray().Any(x =>
            (x.ValueKind == JsonValueKind.String && ResourceNameEquals(x.GetString(), resource)) ||
            (x.ValueKind == JsonValueKind.Object && ResourceNameEquals(Value(x, "name"), resource)));
    }

    private static bool ResourceNameEquals(string? name, string resource) =>
        name is not null && (name.Equals(resource, StringComparison.OrdinalIgnoreCase) ||
                             name.Equals(resource + "s", StringComparison.OrdinalIgnoreCase));

    private static bool CatalogSupportsSearch(JsonElement catalog)
    {
        if (catalog.TryGetProperty("extraSupported", out var extra) && extra.ValueKind == JsonValueKind.Array &&
            extra.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "search"))
            return true;
        return catalog.TryGetProperty("extra", out var list) && list.ValueKind == JsonValueKind.Array &&
               list.EnumerateArray().Any(x => Value(x, "name") == "search");
    }

    private static string? TransportUrl(JsonElement addon)
    {
        if (addon.ValueKind == JsonValueKind.String) return addon.GetString();
        var url = Value(addon, "transportUrl") ?? Value(addon, "transport_url") ?? Value(addon, "url");
        if (url is not null) return url;
        return addon.TryGetProperty("manifest", out var manifest) ? Value(manifest, "url") : null;
    }

    private static Uri? StreamingServerBase()
    {
        var saved = ApplicationData.Current.LocalSettings.Values["server-url"] as string;
        if (string.IsNullOrWhiteSpace(saved) || !Uri.TryCreate(saved.Trim(), UriKind.Absolute, out var endpoint))
            return null;
        if (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps) return null;
        var text = endpoint.AbsoluteUri.TrimEnd('/') + "/";
        return new Uri(text);
    }

    private static Uri? ResolveStreamUri(JsonElement stream, Uri? server)
    {
        var url = Value(stream, "url") ?? Value(stream, "externalUrl");
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
            return parsed;

        var infoHash = Value(stream, "infoHash");
        if (!string.IsNullOrWhiteSpace(infoHash) && server is not null)
        {
            var hash = infoHash.ToLowerInvariant();
            if (stream.TryGetProperty("fileIdx", out var index) && index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out var fileIdx))
                return new Uri(server, hash + "/" + fileIdx);
            return new Uri(server, hash + "/");
        }
        return null;
    }

    private static Uri ResourceUri(Uri manifestUri, string resource, string type, string id, string extra = "") =>
        new(new Uri(manifestUri, "./"), $"{resource}/{Uri.EscapeDataString(type)}/{EncodeStremioId(id)}{extra}.json");

    private static string EncodeStremioId(string id) =>
        string.Join(':', id.Split(':').Select(Uri.EscapeDataString));

    private static MediaItem? ParseMedia(JsonElement meta, string addonId, string fallbackType)
    {
        var id = Value(meta, "id");
        var title = Value(meta, "name") ?? Value(meta, "title");
        if (id is null || title is null) return null;
        var genres = new List<string>();
        if (meta.TryGetProperty("genres", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var genre in array.EnumerateArray()) if (genre.ValueKind == JsonValueKind.String) genres.Add(genre.GetString()!);
        return new MediaItem(id, Value(meta, "type") ?? fallbackType, title, Value(meta, "poster"),
            Value(meta, "background"), Value(meta, "description"), Value(meta, "releaseInfo"),
            Value(meta, "runtime"), genres, addonId);
    }

    private static string? Value(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private static async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken token) =>
        await TryGetJsonAsync(uri, token) ?? throw new HttpRequestException($"Provider returned no content from {uri.Host}.");

    private static async Task<JsonDocument?> TryGetJsonAsync(Uri uri, CancellationToken token)
    {
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 2_000_000) throw new HttpRequestException("Provider response is too large.");
        using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 32 }, token);
    }

    private static string AddonsFilePath =>
        System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "addon-manifest-urls.txt");

    private static List<string> SavedUrls()
    {
        try
        {
            var path = AddonsFilePath;
            if (System.IO.File.Exists(path))
            {
                return System.IO.File.ReadAllLines(path)
                    .Select(s => s.Trim())
                    .Where(s => Uri.TryCreate(s, UriKind.Absolute, out _))
                    .ToList();
            }
        }
        catch { }

        try
        {
            var legacy = ApplicationData.Current.LocalSettings.Values[AddonKey] as string ?? "";
            return legacy
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => Uri.TryCreate(s, UriKind.Absolute, out _)).ToList();
        }
        catch { return new List<string>(); }
    }

    private static void SaveUrls(IEnumerable<string> urls)
    {
        var list = urls.ToList();
        try
        {
            System.IO.File.WriteAllLines(AddonsFilePath, list);
        }
        catch { }

        try
        {
            ApplicationData.Current.LocalSettings.Values.Remove(AddonKey);
        }
        catch { }
    }
}
