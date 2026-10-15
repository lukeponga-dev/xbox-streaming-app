using System;
using System.Collections.Generic;

namespace StremioXboxPrototype.Models;
public sealed record MediaItem(string Id, string Title, string Subtitle, string Genre, string Year, string ArtworkKey, double Progress = 0, bool IsSeries = false);
public sealed record StreamSource(string Name, string Quality, string Codec, string Audio, string Host, Uri? Uri = null);
public sealed record Addon(string Id, string Name, string Description, bool IsEnabled, bool HasUpdate);
public sealed record DiagnosticEvent(DateTimeOffset Time, string Level, string Message, string Detail);
public sealed record ServerInfo(string Name, Uri Endpoint, string Status, DateTimeOffset? LastConnected = null);
public sealed class AppSettings { public bool AutoplayNextEpisode { get; set; } = true; public bool SkipIntro { get; set; } = true; public string DefaultQuality { get; set; } = "Auto"; public string PreferredCodec { get; set; } = "Auto"; public string DynamicRange { get; set; } = "SDR"; public string SubtitleSize { get; set; } = "Medium"; }
