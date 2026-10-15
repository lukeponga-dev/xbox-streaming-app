using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StremioXboxPrototype.Models;
using StremioXboxPrototype.Services;
using Windows.Media.Core;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace StremioXboxPrototype;

public sealed partial class MainPage : Page
{
    private sealed record PageState(string Name, string Query = "", MediaItem? Media = null, StreamSource? Stream = null);

    private readonly IDiagnosticsService _diagnostics = new DiagnosticsService();
    private readonly ISettingsService _settings = new SettingsService();
    private readonly ILibraryService _library = new LibraryService();
    private readonly IServerService _server = new ServerService();
    private readonly StremioService _stremio;
    private readonly Stack<PageState> _history = new();
    private CancellationTokenSource _pageCts = new();
    private CancellationTokenSource? _searchCts;
    private PageState _current = new("Home");
    private AppSettings _appSettings = new();
    private MediaPlayerElement? _player;

    public MainPage()
    {
        _stremio = new StremioService(_diagnostics);
        InitializeComponent();
        GlobalSearch.TextChanged += GlobalSearch_TextChanged;
        Loaded += async (_, _) =>
        {
            _appSettings = await _settings.LoadAsync();
            await ShowPageAsync(new PageState("Home"), false);
        };
    }

    private Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private Style ButtonStyle => (Style)Application.Current.Resources["FocusButton"];

    private TextBlock Text(string value, double size = 16, bool primary = true) => new()
    {
        Text = value, FontSize = size, Foreground = Brush(primary ? "TextPrimaryBrush" : "TextSecondaryBrush"),
        TextWrapping = TextWrapping.Wrap
    };

    private Button Action(string label, Action click)
    {
        var button = new Button { Content = label, Style = ButtonStyle };
        button.Click += (_, _) => click();
        return button;
    }

    private Border Panel(UIElement child) => new()
    {
        Background = Brush("SurfaceBrush"), BorderBrush = new SolidColorBrush(Color.FromArgb(255, 46, 54, 92)),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(18),
        Margin = new Thickness(0, 7, 0, 7), Child = child
    };

    private void Message(string value) => ContentHost.Children.Add(Panel(Text(value, 17, false)));
    private void Feedback(string value)
    {
        var card = Panel(Text(value, 15, false));
        ContentHost.Children.Insert(Math.Min(2, ContentHost.Children.Count), card);
    }

    private async Task ShowPageAsync(PageState next, bool remember = true)
    {
        if (remember && (next.Name != _current.Name || next.Media != _current.Media)) _history.Push(_current);
        _pageCts.Cancel();
        _pageCts.Dispose();
        _pageCts = new CancellationTokenSource();
        var token = _pageCts.Token;
        if (_player is not null) { _player.Source = null; _player = null; }
        _current = next;
        ContentHost.Children.Clear();
        ContentHost.Children.Add(Text(next.Name == "Details" ? next.Media?.Title ?? "Details" : next.Name, 38));
        ContentHost.Children.Add(Text(Subtitle(next.Name), 18, false));

        try
        {
            switch (next.Name)
            {
                case "Home": await HomeAsync(token); break;
                case "Discover": await DiscoverAsync(token); break;
                case "Movies": Row("Movies", await _stremio.GetAsync("movie", null, token)); break;
                case "Series": Row("Series", await _stremio.GetAsync("series", null, token)); break;
                case "Library": Library(); break;
                case "Add-ons": await AddonsAsync(token); break;
                case "Search": await SearchAsync(next.Query, token); break;
                case "Details" when next.Media is not null: await DetailsAsync(next.Media, token); break;
                case "Streams" when next.Media is not null: await StreamsAsync(next.Media, token); break;
                case "Player" when next.Stream is not null: Player(next.Stream); break;
                case "Settings": Settings(); break;
                case "Server": Server(); break;
                case "Diagnostics": Diagnostics(); break;
                case "Playback Lab": PlaybackLab(); break;
                case "Profile": Profile(); break;
            }
            if (!token.IsCancellationRequested && next.Name != "Search") FocusContent();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _diagnostics.Record("ERROR", $"{next.Name} failed", ex.Message);
            Message("This view could not load. Check Diagnostics for details.");
        }
    }

    private void FocusContent()
    {
        ContentHost.UpdateLayout();
        foreach (var child in ContentHost.Children)
        {
            if (FindButton(child) is { } button && button.Focus(FocusState.Programmatic)) return;
        }
        HomeNav.Focus(FocusState.Programmatic);
    }

    private static Button? FindButton(DependencyObject root)
    {
        if (root is Button { IsEnabled: true } button) return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = FindButton(VisualTreeHelper.GetChild(root, index));
            if (found is not null) return found;
        }
        return null;
    }

    private static string Subtitle(string page) => page switch
    {
        "Home" => "Your installed add-ons and watchlist",
        "Discover" => "Browse the catalogs provided by your add-ons",
        "Movies" => "Movie catalogs provided by your add-ons",
        "Series" => "Series catalogs provided by your add-ons",
        "Library" => "Items saved on this device",
        "Add-ons" => "Connect Stremio-compatible catalog and stream providers",
        "Search" => "Search available add-on catalogs",
        "Details" => "Metadata and available episodes",
        "Streams" => "Choose a source for playback",
        "Player" => "Direct HTTP or HTTPS stream",
        "Playback Lab" => "Test a direct stream on this device",
        "Server" => "Check a local or remote playback endpoint",
        "Diagnostics" => "Live connectivity and recorded application events",
        "Settings" => "Playback and appearance preferences",
        _ => "Local profile"
    };

    private void Navigate_Click(object sender, RoutedEventArgs e) =>
        _ = ShowPageAsync(new PageState((string)((Button)sender).Tag));

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = ShowPageAsync(_current, false);

    private void Search_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        _ = ShowPageAsync(new PageState("Search", GlobalSearch.Text));
    }

    private async void GlobalSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_current.Name != "Search") return;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        try
        {
            await Task.Delay(300, token);
            await ShowPageAsync(new PageState("Search", GlobalSearch.Text), false);
        }
        catch (OperationCanceledException) { }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.GamepadY or VirtualKey.Y)
        {
            e.Handled = true;
            _ = ShowPageAsync(new PageState("Search", GlobalSearch.Text));
            GlobalSearch.Focus(FocusState.Programmatic);
        }
        else if (e.Key is VirtualKey.GamepadB or VirtualKey.Escape ||
                 (e.Key == VirtualKey.Back && FocusManager.GetFocusedElement() is not TextBox))
        {
            e.Handled = true;
            var previous = _history.Count > 0 ? _history.Pop() : new PageState("Home");
            _ = ShowPageAsync(previous, false);
        }
        else if (e.Key is VirtualKey.GamepadMenu or VirtualKey.M && _current.Media is not null)
        {
            e.Handled = true;
            _ = ShowPageAsync(new PageState("Details", Media: _current.Media));
        }
    }

    private async Task HomeAsync(CancellationToken token)
    {
        var addons = await _stremio.GetInstalledAsync(token);
        if (addons.Count == 0)
        {
            var intro = new StackPanel();
            intro.Children.Add(Text("Connect your first add-on", 24));
            intro.Children.Add(Text("Install a Stremio-compatible manifest URL to browse real catalogs and streams.", 16, false));
            intro.Children.Add(Action("Open Add-ons", () => _ = ShowPageAsync(new PageState("Add-ons"))));
            ContentHost.Children.Add(Panel(intro));
            return;
        }
        ContentHost.Children.Add(Panel(Text("Connected add-ons: " + string.Join(", ", addons.Select(a => a.Name)), 16, false)));
        Message("Continue Watching is empty until a playback session is recorded on this device.");
        var movies = await _stremio.GetAsync("movie", null, token);
        Row("Movies", movies);
        var series = await _stremio.GetAsync("series", null, token);
        Row("Series", series);
    }

    private async Task DiscoverAsync(CancellationToken token)
    {
        var filterBar = new StackPanel { Orientation = Orientation.Horizontal };
        filterBar.Children.Add(Action("Movies", () => _ = ShowPageAsync(new PageState("Movies"))));
        filterBar.Children.Add(Action("Series", () => _ = ShowPageAsync(new PageState("Series"))));
        ContentHost.Children.Add(filterBar);
        var movies = await _stremio.GetAsync("movie", null, token);
        var series = await _stremio.GetAsync("series", null, token);
        if (movies.Count == 0 && series.Count == 0)
        {
            Message("No catalogs are available. Install an add-on with catalog support to browse.");
            ContentHost.Children.Add(Action("Manage Add-ons", () => _ = ShowPageAsync(new PageState("Add-ons"))));
            return;
        }
        Row("Movies from your add-ons", movies);
        Row("Series from your add-ons", series);
    }

    private void Library()
    {
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var label in new[] { "Watchlist", "Continue Watching", "History", "Downloads" })
            tabs.Children.Add(Action(label, () => Feedback(label == "Watchlist"
                ? "Your saved items appear below." : $"{label} has no entries on this device.")));
        ContentHost.Children.Add(tabs);
        Row("Watchlist", _library.Watchlist());
        Message("Playback history and downloads are not yet recorded on this device.");
    }

    private void Row(string heading, IReadOnlyList<MediaItem> items)
    {
        ContentHost.Children.Add(Text(heading, 24));
        if (items.Count == 0) { Message("Nothing available in this collection."); return; }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var item in items) row.Children.Add(MediaCard(item));
        ContentHost.Children.Add(new ScrollViewer
        {
            Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled
        });
    }

    private Button MediaCard(MediaItem item)
    {
        var card = new StackPanel { Width = 174 };
        var poster = new Border { Height = 235, Background = Brush("SurfaceBrush"), CornerRadius = new CornerRadius(8) };
        if (Uri.TryCreate(item.Poster, UriKind.Absolute, out var posterUri))
            poster.Child = new Image { Source = new BitmapImage(posterUri), Stretch = Stretch.UniformToFill };
        else poster.Child = new TextBlock { Text = item.Title.Length > 0 ? item.Title[..1] : "?", FontSize = 56, Foreground = Brush("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        card.Children.Add(poster);
        card.Children.Add(Text(item.Title, 16));
        card.Children.Add(Text(string.Join(" • ", new[] { item.ReleaseInfo, item.Runtime }.Where(x => !string.IsNullOrWhiteSpace(x))), 13, false));
        var button = new Button { Content = card, Style = ButtonStyle, Width = 190,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(2, 4, 8, 4) };
        button.Click += (_, _) => _ = ShowPageAsync(new PageState("Details", Media: item));
        button.GotFocus += (_, _) => Canvas.SetZIndex(button, 10);
        button.LostFocus += (_, _) => Canvas.SetZIndex(button, 0);
        return button;
    }

    private async Task DetailsAsync(MediaItem item, CancellationToken token)
    {
        var metadata = await _stremio.GetMetadataAsync(item, token);
        var media = metadata.Item;
        if (Uri.TryCreate(media.Background ?? media.Poster, UriKind.Absolute, out var backdrop))
            ContentHost.Children.Add(new Image { Source = new BitmapImage(backdrop), Height = 310, Stretch = Stretch.UniformToFill });
        ContentHost.Children.Add(Text(string.Join(" • ", new[] { media.ReleaseInfo, media.Runtime, string.Join(", ", media.Genres) }
            .Where(s => !string.IsNullOrWhiteSpace(s))), 16, false));
        ContentHost.Children.Add(Panel(Text(media.Description ?? "No description supplied by this add-on.", 17)));
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var playItem = metadata.Episodes.Count > 0 ? media with { Id = metadata.Episodes[0].Id } : media;
        actions.Children.Add(Action("▶  Choose Stream", () => _ = ShowPageAsync(new PageState("Streams", Media: playItem))));
        actions.Children.Add(Action(_library.Contains(media) ? "Remove from Watchlist" : "Add to Watchlist", () =>
        {
            var save = !_library.Contains(media);
            _library.SetSaved(media, save);
            Feedback(save ? "Saved to your watchlist." : "Removed from your watchlist.");
        }));
        ContentHost.Children.Add(actions);
        if (metadata.Episodes.Count > 0)
        {
            ContentHost.Children.Add(Text("Episodes", 24));
            foreach (var episode in metadata.Episodes.OrderBy(e => e.Season).ThenBy(e => e.Episode).Take(100))
            {
                var selection = media with { Id = episode.Id };
                ContentHost.Children.Add(Action($"S{episode.Season} E{episode.Episode}  {episode.Title}",
                    () => _ = ShowPageAsync(new PageState("Streams", Media: selection))));
            }
        }
    }

    private async Task StreamsAsync(MediaItem item, CancellationToken token)
    {
        var streams = await _stremio.GetStreamsAsync(item, token);
        if (streams.Count == 0) { Message("No add-on returned a stream for this item."); return; }
        foreach (var stream in streams)
        {
            var label = $"{stream.Name}  •  {stream.AddonName}\n{stream.Description}";
            var button = Action(label, () => _ = ShowPageAsync(new PageState("Player", Media: item, Stream: stream)));
            button.IsEnabled = stream.Uri is not null;
            ContentHost.Children.Add(Panel(button));
        }
        if (streams.All(s => s.Uri is null)) Message("These sources require a separate stream engine. Direct HTTP or HTTPS streams can play here.");
    }

    private void Player(StreamSource stream)
    {
        if (stream.Uri is null) { Message("This source does not provide a direct playback URL."); return; }
        ContentHost.Children.Add(Text(stream.Name + " • " + stream.AddonName, 19, false));
        _player = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true, AutoPlay = true, Height = 640,
            Source = MediaSource.CreateFromUri(stream.Uri)
        };
        ContentHost.Children.Add(_player);
        ContentHost.Children.Add(Action("Back to Streams", Back));
    }

    private async Task AddonsAsync(CancellationToken token)
    {
        var installed = await _stremio.GetInstalledAsync(token);
        ContentHost.Children.Add(Panel(Text($"Installed add-ons: {installed.Count}  •  Catalogs: {installed.Sum(a => a.Catalogs.Count)}", 18)));
        var input = new TextBox { PlaceholderText = "https://your-addon.example/manifest.json", Background = Brush("SurfaceBrush"),
            Foreground = Brush("TextPrimaryBrush"), BorderBrush = Brush("AccentBrush"), Padding = new Thickness(14),
            MinWidth = 500, CornerRadius = new CornerRadius(8) };
        ContentHost.Children.Add(input);
        ContentHost.Children.Add(Action("Install Add-on", async () =>
        {
            try
            {
                var addon = await _stremio.InstallAsync(input.Text, _pageCts.Token);
                Feedback($"Installed {addon.Name}.");
                await ShowPageAsync(new PageState("Add-ons"), false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is ArgumentException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
            { Feedback("Could not install add-on: " + ex.Message); }
        }));
        ContentHost.Children.Add(Text("Installed", 24));
        if (installed.Count == 0) { Message("No add-ons installed. Enter a manifest URL above."); return; }
        foreach (var addon in installed)
        {
            var card = new StackPanel();
            card.Children.Add(Text(addon.Name, 21));
            card.Children.Add(Text(addon.Description, 15, false));
            card.Children.Add(Text($"{addon.Catalogs.Count} catalogs  •  Metadata: {(addon.HasMetadata ? "Yes" : "No")}  •  Streams: {(addon.HasStreams ? "Yes" : "No")}", 14, false));
            card.Children.Add(Action("Remove", async () =>
            {
                await _stremio.RemoveAsync(addon.ManifestUri);
                await ShowPageAsync(new PageState("Add-ons"), false);
            }));
            ContentHost.Children.Add(Panel(card));
        }
    }

    private async Task SearchAsync(string query, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query)) { Message("Type a title in the search box above."); return; }
        Row("Movies", await _stremio.GetAsync("movie", query, token));
        Row("Series", await _stremio.GetAsync("series", query, token));
        var addonMatches = (await _stremio.GetInstalledAsync(token))
            .Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        ContentHost.Children.Add(Text("Add-ons", 24));
        foreach (var addon in addonMatches) ContentHost.Children.Add(Panel(Text(addon.Name + "\n" + addon.Description)));
        if (addonMatches.Count == 0) Message("No add-on names match this search.");
    }

    private void Settings()
    {
        ContentHost.Children.Add(Text("Playback Settings", 25));
        Toggle("Autoplay next episode", _appSettings.AutoplayNextEpisode, v => _appSettings.AutoplayNextEpisode = v);
        Choice("Default quality", new[] { "Auto", "4K", "1080p", "720p" }, _appSettings.DefaultQuality, v => _appSettings.DefaultQuality = v);
        Choice("Preferred codec", new[] { "Auto", "HEVC", "H.264", "AV1" }, _appSettings.PreferredCodec, v => _appSettings.PreferredCodec = v);
        Choice("Dynamic range", new[] { "SDR", "HDR", "Dolby Vision" }, _appSettings.DynamicRange, v => _appSettings.DynamicRange = v);
        Choice("Subtitle size", new[] { "Small", "Medium", "Large" }, _appSettings.SubtitleSize, v => _appSettings.SubtitleSize = v);
        Toggle("Skip intro", _appSettings.SkipIntro, v => _appSettings.SkipIntro = v);
        ContentHost.Children.Add(Action("Save Changes", async () =>
        {
            await _settings.SaveAsync(_appSettings);
            Feedback("Settings saved on this device.");
        }));
    }

    private void Toggle(string label, bool value, Action<bool> changed)
    {
        var control = new ToggleSwitch { Header = label, IsOn = value, Foreground = Brush("TextPrimaryBrush"), Margin = new Thickness(8) };
        control.Toggled += (_, _) => changed(control.IsOn);
        ContentHost.Children.Add(Panel(control));
    }

    private void Choice(string label, string[] choices, string current, Action<string> changed)
    {
        var control = new ComboBox { Header = label, ItemsSource = choices, SelectedItem = current,
            Foreground = Brush("TextPrimaryBrush"), Background = Brush("SurfaceBrush"),
            BorderBrush = Brush("FocusBorderBrush"), MinWidth = 300, Margin = new Thickness(8) };
        control.SelectionChanged += (_, _) => { if (control.SelectedItem is string value) changed(value); };
        ContentHost.Children.Add(Panel(control));
    }

    private void Server()
    {
        var saved = ApplicationData.Current.LocalSettings.Values["server-url"] as string ?? "";
        ContentHost.Children.Add(Text(string.IsNullOrWhiteSpace(saved) ? "No server saved" : "Saved endpoint: " + saved, 18, false));
        var input = new TextBox { Text = saved, PlaceholderText = "http://192.168.1.42:8096",
            Background = Brush("SurfaceBrush"), Foreground = Brush("TextPrimaryBrush"),
            BorderBrush = Brush("AccentBrush"), Padding = new Thickness(14), MinWidth = 500 };
        ContentHost.Children.Add(input);
        ContentHost.Children.Add(Action("Test Connection", async () =>
        {
            try
            {
                var result = await _server.TestAsync(input.Text, _pageCts.Token);
                Feedback(result is null ? "Enter a valid HTTP or HTTPS URL." : $"{result.Name}: {result.Status}");
                _diagnostics.Record(result?.Status == "Reachable" ? "INFO" : "WARN", "Server test", result?.Status ?? "Invalid URL");
            }
            catch (OperationCanceledException) { }
        }));
        ContentHost.Children.Add(Action("Save Endpoint", () =>
        {
            if (!Uri.TryCreate(input.Text, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            { Feedback("Enter a valid HTTP or HTTPS URL."); return; }
            ApplicationData.Current.LocalSettings.Values["server-url"] = uri.AbsoluteUri;
            Feedback("Server endpoint saved on this device.");
        }));
        Message("Local LAN discovery requires a server discovery protocol and is unavailable in this build.");
    }

    private void Diagnostics()
    {
        ContentHost.Children.Add(Panel(Text("Internet connection: " + _diagnostics.InternetStatus(), 20)));
        foreach (var metric in new[] { "Network speed", "Storage", "Decoder latency", "Memory usage", "Frame drops" })
            ContentHost.Children.Add(Panel(Text(metric + ": Unavailable", 16, false)));
        ContentHost.Children.Add(Action("Run Checks", () =>
        {
            var status = _diagnostics.InternetStatus();
            _diagnostics.Record(status == "Connected" ? "INFO" : "WARN", "Internet connection", status);
            _ = ShowPageAsync(new PageState("Diagnostics"), false);
        }));
        ContentHost.Children.Add(Text("Recent Events", 24));
        if (_diagnostics.Events.Count == 0) Message("No events recorded in this session.");
        foreach (var entry in _diagnostics.Events)
            ContentHost.Children.Add(Panel(Text($"{entry.Time:HH:mm:ss}  {entry.Level}  {entry.Message}\n{entry.Detail}", 15)));
    }

    private void PlaybackLab()
    {
        Message("Codec, HDR, audio, bitrate, and frame metrics remain Unavailable until measured by a playback probe.");
        var input = new TextBox { PlaceholderText = "Direct HTTPS test-stream URL", Background = Brush("SurfaceBrush"),
            Foreground = Brush("TextPrimaryBrush"), BorderBrush = Brush("AccentBrush"), MinWidth = 500, Padding = new Thickness(14) };
        ContentHost.Children.Add(input);
        ContentHost.Children.Add(Action("Play Test Stream", () =>
        {
            if (!Uri.TryCreate(input.Text, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            { Feedback("Enter a direct HTTP or HTTPS media URL."); return; }
            _ = ShowPageAsync(new PageState("Player", Stream: new StreamSource("Test Stream", "User supplied", uri, "Playback Lab")));
        }));
    }

    private void Profile()
    {
        ContentHost.Children.Add(Panel(Text($"Local profile\nSaved items: {_library.Watchlist().Count}\nInternet: {_diagnostics.InternetStatus()}", 19)));
    }

    private void Back()
    {
        var previous = _history.Count > 0 ? _history.Pop() : new PageState("Home");
        _ = ShowPageAsync(previous, false);
    }
}
