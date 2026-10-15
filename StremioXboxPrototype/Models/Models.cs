using System;
using System.Collections.Generic;

namespace StremioXboxPrototype.Models;

public sealed record MediaItem(
    string Id,
    string Type,
    string Title,
    string? Poster,
    string? Background,
    string? Description,
    string? ReleaseInfo,
    string? Runtime,
    IReadOnlyList<string> Genres,
    string AddonId);

public sealed record EpisodeItem(string Id, int Season, int Episode, string Title);
public sealed record MediaMetadata(MediaItem Item, IReadOnlyList<EpisodeItem> Episodes);
public sealed record StreamSource(string Name, string Description, Uri? Uri, string AddonName, string? Quality = null);
public sealed record CatalogDefinition(string Type, string Id, string Name, bool SupportsSearch);
public sealed record AddonManifest(string Id, string Name, string Description, Uri ManifestUri, IReadOnlyList<CatalogDefinition> Catalogs, bool HasMetadata, bool HasStreams);
public sealed record DiagnosticEvent(DateTimeOffset Time, string Level, string Message, string Detail);
public sealed record ServerInfo(string Name, Uri Endpoint, string Status, DateTimeOffset? LastConnected = null);
public sealed record StremioAccount(string Id, string DisplayName, string? Email);
public sealed record FacebookLoginRequest(string State, Uri LoginUri);

public sealed class AppSettings
{
    public bool AutoplayNextEpisode { get; set; } = true;
    public bool SkipIntro { get; set; } = true;
    public string DefaultQuality { get; set; } = "Auto";
    public string PreferredCodec { get; set; } = "Auto";
    public string DynamicRange { get; set; } = "SDR";
    public string SubtitleSize { get; set; } = "Medium";
}
