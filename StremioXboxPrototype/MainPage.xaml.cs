using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StremioXboxPrototype.Models;
using StremioXboxPrototype.Services;
using Windows.Media.Core;
using Windows.Media.Playback;
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
    private readonly IAuthService _auth = new StremioAuthService();
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
            await UpdateProfileHeaderAsync();
            await SyncAddonsQuietAsync(_pageCts.Token);
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

    private TextBlock PageTitle(string value) => new()
    {
        Text = value,
        FontSize = 38,
        FontWeight = Windows.UI.Text.FontWeights.SemiBold,
        Foreground = Brush("TextPrimaryBrush"),
        Margin = new Thickness(0, 0, 0, 1)
    };

    private Button Action(string label, Action click)
    {
        var button = new Button { Content = label, Style = ButtonStyle };
        button.Click += (_, _) => click();
        return button;
    }

    private Border Panel(UIElement child) => new()
    {
        Background = Brush("SurfaceBrush"), BorderBrush = new SolidColorBrush(Color.FromArgb(255, 44, 53, 105)),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(20),
        Margin = new Thickness(0, 8, 0, 8), Child = child
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
        UpdateNavigation(next.Name);
        ContentHost.Children.Clear();
        ContentHost.Children.Add(PageTitle(next.Name == "Details" ? next.Media?.Title ?? "Details" : next.Name));
        var subtitle = Text(Subtitle(next.Name), 18, false);
        subtitle.Margin = new Thickness(0, 0, 0, 12);
        ContentHost.Children.Add(subtitle);

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
                case "Profile": await ProfileAsync(token); break;
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

    private void UpdateNavigation(string page)
    {
        foreach (var button in new[] { HomeNav, DiscoverNav, LibraryNav, AddonsNav, SettingsNav })
        {
            var active = string.Equals(button.Tag as string, page, StringComparison.Ordinal);
            button.Background = active ? new SolidColorBrush(Color.FromArgb(255, 34, 38, 76)) : new SolidColorBrush(Colors.Transparent);
            button.BorderBrush = active ? new SolidColorBrush(Color.FromArgb(255, 44, 50, 94)) : new SolidColorBrush(Colors.Transparent);
            button.Foreground = active ? Brush("TextPrimaryBrush") : Brush("TextSecondaryBrush");
        }
    }

    private async Task UpdateProfileHeaderAsync()
    {
        var account = await _auth.GetCurrentAsync();
        var displayName = account?.DisplayName ?? "Profile";
        ProfileHeaderText.Text = displayName;
        if (FindName("ProfileInitialText") is TextBlock initialText)
        {
            initialText.Text = !string.IsNullOrWhiteSpace(displayName) ? displayName[..1].ToUpperInvariant() : "P";
        }
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
        "Home" => "Pick up where you left off or find your next favourite.",
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
        "Settings" => "Playback preferences and advanced tools",
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
            intro.Children.Add(Text("Install a Stremio-compatible manifest URL to browse real catalogs and streams, or sync add-ons from your Stremio account.", 16, false));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            actions.Children.Add(Action("Open Add-ons", () => _ = ShowPageAsync(new PageState("Add-ons"))));
            if (_auth.GetSessionKey() is not null)
            {
                actions.Children.Add(Action("Sync Account Add-ons", async () =>
                {
                    try
                    {
                        var count = await _stremio.SyncFromAccountAsync(_auth.GetSessionKey()!, _pageCts.Token);
                        await ShowPageAsync(new PageState("Home"), false);
                        Feedback(count == 0
                            ? "This Stremio account has no add-ons to sync."
                            : $"Synced {count} add-ons from your Stremio account.");
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) when (ex is ArgumentException or HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
                    { Feedback("Could not sync add-ons: " + ex.Message); }
                }));
            }
            else
            {
                actions.Children.Add(Action("Sign in to Stremio", () => _ = ShowPageAsync(new PageState("Profile"))));
            }
            intro.Children.Add(actions);
            ContentHost.Children.Add(Panel(intro));
            return;
        }

        ContentHost.Children.Add(ServerBanner());

        var movies = await _stremio.GetAsync("movie", null, token);
        Row("Popular Movies", movies);

        var series = await _stremio.GetAsync("series", null, token);
        Row("Popular Series", series);
    }

    private Border ServerBanner()
    {
        var banner = new Grid();
        banner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        banner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftSide = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var warnIcon = new FontIcon { Glyph = "\uE7BA", FontSize = 18, Foreground = Brush("AlertIconBrush"), Margin = new Thickness(0, 0, 14, 0) };
        var warnText = new TextBlock
        {
            Text = "Your playback server is offline. You can still browse or try reconnecting.",
            FontSize = 14,
            Foreground = Brush("AlertTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        leftSide.Children.Add(warnIcon);
        leftSide.Children.Add(warnText);
        Grid.SetColumn(leftSide, 0);
        banner.Children.Add(leftSide);

        var reconnectBtn = Action("Reconnect", async () =>
        {
            Feedback("Testing streaming server connection...");
            var saved = ApplicationData.Current.LocalSettings.Values["server-url"] as string ?? "";
            var res = await _server.TestAsync(saved, _pageCts.Token);
            Feedback(res?.Status == "Reachable" ? "Playback server connected." : "Playback server is offline. Configure endpoint in Settings.");
            await ShowPageAsync(new PageState("Home"), false);
        });
        reconnectBtn.Style = (Style)Application.Current.Resources["BannerButton"];
        Grid.SetColumn(reconnectBtn, 1);
        banner.Children.Add(reconnectBtn);

        return new Border
        {
            Background = Brush("AlertBackgroundBrush"),
            BorderBrush = Brush("AlertBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 10, 18, 10),
            Margin = new Thickness(0, 4, 0, 18),
            Child = banner
        };
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
        var headerGrid = new Grid { Margin = new Thickness(0, 12, 0, 10) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleBlock = new TextBlock { Text = heading, FontSize = 20, FontWeight = Windows.UI.Text.FontWeights.Bold, Foreground = Brush("TextPrimaryBrush") };
        Grid.SetColumn(titleBlock, 0);
        headerGrid.Children.Add(titleBlock);

        var seeAllBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "See all", FontSize = 13, Foreground = Brush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center },
                    new FontIcon { Glyph = "\uE76C", FontSize = 11, Foreground = Brush("TextSecondaryBrush"), Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center }
                }
            },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 6, 2),
            CornerRadius = new CornerRadius(4)
        };
        seeAllBtn.Click += (_, _) =>
        {
            if (heading.Contains("Movie", StringComparison.OrdinalIgnoreCase))
                _ = ShowPageAsync(new PageState("Movies"));
            else if (heading.Contains("Series", StringComparison.OrdinalIgnoreCase))
                _ = ShowPageAsync(new PageState("Series"));
        };
        Grid.SetColumn(seeAllBtn, 1);
        headerGrid.Children.Add(seeAllBtn);
        ContentHost.Children.Add(headerGrid);

        if (items.Count == 0) { Message("Nothing available in this collection."); return; }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var item in items) row.Children.Add(MediaCard(item));
        var scrollViewer = new ScrollViewer
        {
            Content = row,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Margin = new Thickness(0, 0, 0, 6)
        };
        ContentHost.Children.Add(scrollViewer);

        var scrollBarGrid = new Grid { Margin = new Thickness(0, 4, 0, 18) };
        scrollBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scrollBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var trackCanvas = new Canvas { Height = 4, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        var trackBg = new Border { Height = 3, Background = new SolidColorBrush(Color.FromArgb(255, 23, 27, 56)), CornerRadius = new CornerRadius(1.5), Width = 1600 };
        var trackThumb = new Border { Height = 3, Width = 110, Background = Brush("AccentBrush"), CornerRadius = new CornerRadius(1.5) };
        trackCanvas.Children.Add(trackBg);
        trackCanvas.Children.Add(trackThumb);
        Grid.SetColumn(trackCanvas, 0);
        scrollBarGrid.Children.Add(trackCanvas);

        scrollViewer.ViewChanged += (_, _) =>
        {
            if (scrollViewer.ScrollableWidth > 0)
            {
                var maxOffset = Math.Max(1, scrollViewer.ScrollableWidth);
                var ratio = scrollViewer.HorizontalOffset / maxOffset;
                Canvas.SetLeft(trackThumb, ratio * Math.Max(0, scrollViewer.ActualWidth - 140));
            }
        };

        var scrollNextBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE76C", FontSize = 12, Foreground = Brush("TextSecondaryBrush"), Margin = new Thickness(0) },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 6, 2),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };
        scrollNextBtn.Click += (_, _) =>
        {
            scrollViewer.ChangeView(scrollViewer.HorizontalOffset + 480, null, null);
        };
        Grid.SetColumn(scrollNextBtn, 1);
        scrollBarGrid.Children.Add(scrollNextBtn);

        ContentHost.Children.Add(scrollBarGrid);
    }

    private Button MediaCard(MediaItem item)
    {
        var card = new StackPanel { Width = 162 };
        var posterBorder = new Border
        {
            Height = 238,
            Background = Brush("SurfaceBrush"),
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(2.5)
        };

        if (Uri.TryCreate(item.Poster, UriKind.Absolute, out var posterUri))
        {
            posterBorder.Child = new Image
            {
                Source = new BitmapImage(posterUri),
                Stretch = Stretch.UniformToFill
            };
        }
        else
        {
            posterBorder.Child = new TextBlock
            {
                Text = item.Title.Length > 0 ? item.Title[..1] : "?",
                FontSize = 52,
                FontWeight = Windows.UI.Text.FontWeights.Bold,
                Foreground = Brush("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        card.Children.Add(posterBorder);

        var titleBlock = new TextBlock
        {
            Text = item.Title,
            FontSize = 14,
            FontWeight = Windows.UI.Text.FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
            Margin = new Thickness(0, 8, 0, 2),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        card.Children.Add(titleBlock);

        var metaParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.ReleaseInfo)) metaParts.Add(item.ReleaseInfo);
        if (item.Genres.Count > 0 && !string.IsNullOrWhiteSpace(item.Genres[0]))
            metaParts.Add(item.Genres[0]);
        else if (!string.IsNullOrWhiteSpace(item.Runtime))
            metaParts.Add(item.Runtime);

        var metaText = metaParts.Count > 0 ? string.Join("  •  ", metaParts) : (item.Type == "series" ? "Series" : "Movie");
        var metaBlock = new TextBlock
        {
            Text = metaText,
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        card.Children.Add(metaBlock);

        var button = new Button
        {
            Content = card,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 14, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        button.Click += (_, _) => _ = ShowPageAsync(new PageState("Details", Media: item));
        button.GotFocus += (_, _) =>
        {
            posterBorder.BorderBrush = Brush("FocusBorderBrush");
            Canvas.SetZIndex(button, 10);
        };
        button.LostFocus += (_, _) =>
        {
            posterBorder.BorderBrush = new SolidColorBrush(Colors.Transparent);
            Canvas.SetZIndex(button, 0);
        };

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
        if (streams.Count == 0)
        {
            Message("No add-on returned a stream for this title. Sync add-ons from your Stremio account, and save a streaming server in Server if the sources are torrents.");
            ContentHost.Children.Add(Action("Open Add-ons", () => _ = ShowPageAsync(new PageState("Add-ons"))));
            return;
        }
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

        var source = MediaSource.CreateFromUri(stream.Uri);
        var playbackItem = new MediaPlaybackItem(source);

        _player = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true,
            AutoPlay = true,
            Height = 640,
            Source = playbackItem
        };

        var transport = _player.TransportControls ?? new MediaTransportControls();
        transport.IsVolumeButtonVisible = true;
        transport.IsVolumeEnabled = true;
        transport.IsFullWindowButtonVisible = true;
        transport.IsFullWindowEnabled = true;
        _player.TransportControls = transport;

        if (_player.MediaPlayer is not null)
        {
            _player.MediaPlayer.AudioCategory = MediaPlayerAudioCategory.Movie;
        }

        ContentHost.Children.Add(_player);

        var audioPanel = new StackPanel { Spacing = 10 };
        audioPanel.Children.Add(Text("Audio & Volume Controls", 20));

        var audioControlsRow = new StackPanel { Orientation = Orientation.Horizontal };

        var trackCombo = new ComboBox
        {
            Header = "Audio Track",
            Background = Brush("SurfaceBrush"),
            Foreground = Brush("TextPrimaryBrush"),
            BorderBrush = Brush("FocusBorderBrush"),
            MinWidth = 280,
            PlaceholderText = "Detecting audio tracks...",
            Margin = new Thickness(0, 0, 16, 0)
        };

        var isUpdatingTracks = false;
        void RefreshAudioTracks()
        {
            isUpdatingTracks = true;
            try
            {
                var tracks = playbackItem.AudioTracks;
                var items = new List<string>();
                for (var i = 0; i < tracks.Count; i++)
                {
                    var track = tracks[i];
                    var label = !string.IsNullOrWhiteSpace(track.Label) ? track.Label : track.Name;
                    if (!string.IsNullOrWhiteSpace(track.Language) && !string.Equals(label, track.Language, StringComparison.OrdinalIgnoreCase))
                        label = $"{label} ({track.Language})";
                    if (string.IsNullOrWhiteSpace(label)) label = $"Audio Track {i + 1}";
                    items.Add(label);
                }

                if (items.Count == 0)
                {
                    items.Add("Default Audio Track");
                }

                trackCombo.ItemsSource = items;
                var selected = tracks.SelectedIndex;
                if (selected >= 0 && selected < items.Count)
                    trackCombo.SelectedIndex = selected;
                else if (items.Count > 0)
                    trackCombo.SelectedIndex = 0;
            }
            finally
            {
                isUpdatingTracks = false;
            }
        }

        RefreshAudioTracks();

        trackCombo.SelectionChanged += (_, _) =>
        {
            if (isUpdatingTracks) return;
            if (trackCombo.SelectedIndex >= 0 && trackCombo.SelectedIndex < playbackItem.AudioTracks.Count)
            {
                playbackItem.AudioTracks.SelectedIndex = trackCombo.SelectedIndex;
                var trackName = trackCombo.SelectedItem as string ?? "Selected Track";
                _diagnostics.Record("INFO", "Audio track switched", trackName);
            }
        };

        playbackItem.AudioTracksChanged += (s, e) =>
        {
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                RefreshAudioTracks();
            });
        };

        if (_player.MediaPlayer is not null)
        {
            _player.MediaPlayer.MediaOpened += (s, e) =>
            {
                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    RefreshAudioTracks();
                });
            };
        }

        audioControlsRow.Children.Add(trackCombo);

        var volumeText = Text($"Volume: {(int)((_player.MediaPlayer?.Volume ?? 1.0) * 100)}%", 16);
        volumeText.VerticalAlignment = VerticalAlignment.Center;
        volumeText.Margin = new Thickness(14, 26, 8, 0);

        void UpdateVolumeText()
        {
            if (_player.MediaPlayer is { } mp)
            {
                volumeText.Text = mp.IsMuted ? "Volume: Muted" : $"Volume: {(int)Math.Round(mp.Volume * 100)}%";
            }
        }

        var volDownBtn = Action("-10%", () =>
        {
            if (_player.MediaPlayer is { } mp)
            {
                mp.Volume = Math.Max(0.0, Math.Round((mp.Volume - 0.1) * 10) / 10);
                if (mp.IsMuted && mp.Volume > 0) mp.IsMuted = false;
                UpdateVolumeText();
            }
        });
        volDownBtn.VerticalAlignment = VerticalAlignment.Bottom;
        volDownBtn.Margin = new Thickness(0, 0, 8, 0);

        var volUpBtn = Action("+10%", () =>
        {
            if (_player.MediaPlayer is { } mp)
            {
                mp.Volume = Math.Min(1.0, Math.Round((mp.Volume + 0.1) * 10) / 10);
                if (mp.IsMuted) mp.IsMuted = false;
                UpdateVolumeText();
            }
        });
        volUpBtn.VerticalAlignment = VerticalAlignment.Bottom;
        volUpBtn.Margin = new Thickness(0, 0, 8, 0);

        var muteBtn = Action("Mute / Unmute", () =>
        {
            if (_player.MediaPlayer is { } mp)
            {
                mp.IsMuted = !mp.IsMuted;
                UpdateVolumeText();
            }
        });
        muteBtn.VerticalAlignment = VerticalAlignment.Bottom;
        muteBtn.Margin = new Thickness(0, 0, 8, 0);

        audioControlsRow.Children.Add(volDownBtn);
        audioControlsRow.Children.Add(volUpBtn);
        audioControlsRow.Children.Add(muteBtn);
        audioControlsRow.Children.Add(volumeText);

        audioPanel.Children.Add(audioControlsRow);
        ContentHost.Children.Add(Panel(audioPanel));

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
        if (_auth.GetSessionKey() is not null)
        {
            ContentHost.Children.Add(Action("Sync add-ons from Stremio account", async () =>
            {
                try
                {
                    var count = await _stremio.SyncFromAccountAsync(_auth.GetSessionKey()!, _pageCts.Token);
                    await ShowPageAsync(new PageState("Add-ons"), false);
                    Feedback(count == 0
                        ? "This Stremio account has no add-ons to sync."
                        : $"Synced {count} add-ons from your Stremio account.");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is ArgumentException or HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
                { Feedback("Could not sync add-ons: " + ex.Message); }
            }));
        }
        else
        {
            Message("Sign in on Profile to sync the add-ons already installed on your Stremio account.");
            ContentHost.Children.Add(Action("Go to Profile to Sign In", () => _ = ShowPageAsync(new PageState("Profile"))));
        }
        ContentHost.Children.Add(Action("Install Add-on", async () =>
        {
            try
            {
                var addon = await _stremio.InstallAsync(input.Text, _pageCts.Token);
                await ShowPageAsync(new PageState("Add-ons"), false);
                Feedback($"Installed {addon.Name}.");
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

        ContentHost.Children.Add(Text("Advanced & System", 24));
        var advancedRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        advancedRow.Children.Add(Action("Server Configuration", () => _ = ShowPageAsync(new PageState("Server"))));
        advancedRow.Children.Add(Action("Diagnostics", () => _ = ShowPageAsync(new PageState("Diagnostics"))));
        advancedRow.Children.Add(Action("Playback Lab", () => _ = ShowPageAsync(new PageState("Playback Lab"))));
        ContentHost.Children.Add(advancedRow);
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

    private async Task ProfileAsync(CancellationToken token)
    {
        var account = await _auth.GetCurrentAsync();
        if (account is not null)
        {
            ContentHost.Children.Add(Panel(Text($"Signed in to Stremio\n{account.DisplayName}\n{account.Email}\n\nSaved items on this device: {_library.Watchlist().Count}", 19)));
            ContentHost.Children.Add(Action("Sync add-ons from this account", async () =>
            {
                try
                {
                    var count = await _stremio.SyncFromAccountAsync(_auth.GetSessionKey() ?? "", _pageCts.Token);
                    await ShowPageAsync(new PageState("Profile"), false);
                    Feedback(count == 0
                        ? "This Stremio account has no add-ons to sync."
                        : $"Synced {count} add-ons from your Stremio account.");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is ArgumentException or HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
                { Feedback("Could not sync add-ons: " + ex.Message); }
            }));
            ContentHost.Children.Add(Action("Sign out", async () =>
            {
                try
                {
                    await _auth.LogoutAsync(_pageCts.Token);
                    await UpdateProfileHeaderAsync();
                    await ShowPageAsync(new PageState("Profile"), false);
                    Feedback("Signed out of Stremio on this device.");
                }
                catch (OperationCanceledException) { }
            }));
            return;
        }

        ContentHost.Children.Add(Panel(Text("Sign in with your Stremio account to use its account session in this app. Your password is sent only to Stremio over HTTPS and is never saved here.", 17, false)));
        var email = new TextBox
        {
            Header = "Stremio email", PlaceholderText = "you@example.com", InputScope = new Windows.UI.Xaml.Input.InputScope
            {
                Names = { new Windows.UI.Xaml.Input.InputScopeName(Windows.UI.Xaml.Input.InputScopeNameValue.EmailSmtpAddress) }
            },
            Background = Brush("SurfaceBrush"), Foreground = Brush("TextPrimaryBrush"), BorderBrush = Brush("FocusBorderBrush"),
            MinWidth = 440, Padding = new Thickness(14), Margin = new Thickness(0, 8, 0, 6)
        };
        var password = new PasswordBox
        {
            Header = "Password", PlaceholderText = "Your Stremio password", Background = Brush("SurfaceBrush"),
            Foreground = Brush("TextPrimaryBrush"), BorderBrush = Brush("FocusBorderBrush"), MinWidth = 440,
            Padding = new Thickness(14), Margin = new Thickness(0, 6, 0, 8)
        };
        ContentHost.Children.Add(email);
        ContentHost.Children.Add(password);
        ContentHost.Children.Add(Action("Sign in to Stremio", async () =>
        {
            var secret = password.Password;
            password.Password = string.Empty;
            try
            {
                var signedIn = await _auth.LoginAsync(email.Text, secret, _pageCts.Token);
                await SyncAddonsQuietAsync(_pageCts.Token);
                await UpdateProfileHeaderAsync();
                await ShowPageAsync(new PageState("Profile"), false);
                Feedback($"Signed in as {signedIn.DisplayName}. Add-ons synced from your account.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is ArgumentException or HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
            { Feedback(ex.Message); }
        }));
        ContentHost.Children.Add(Action("Sign in with Facebook", async () =>
        {
            try
            {
                var request = _auth.CreateFacebookLogin();
                Feedback("Complete Facebook sign-in in the browser. This page continues automatically when Stremio returns the account.");
                ContentHost.Children.Add(Panel(Text($"If the browser did not open, visit:\n{request.LoginUri}", 15, false)));
                _ = await Launcher.LaunchUriAsync(request.LoginUri);
                var signedIn = await _auth.CompleteFacebookLoginAsync(request.State, _pageCts.Token);
                await SyncAddonsQuietAsync(_pageCts.Token);
                await UpdateProfileHeaderAsync();
                await ShowPageAsync(new PageState("Profile"), false);
                Feedback($"Signed in as {signedIn.DisplayName}.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is ArgumentException or HttpRequestException or JsonException or InvalidOperationException
                or TaskCanceledException or TimeoutException)
            { Feedback(ex.Message); }
        }));
        Message("Google and Apple sign-in are not included on this screen.");
    }

    private async Task SyncAddonsQuietAsync(CancellationToken token)
    {
        var key = _auth.GetSessionKey();
        if (string.IsNullOrWhiteSpace(key)) return;
        try { await _stremio.SyncFromAccountAsync(key, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        { _diagnostics.Record("WARN", "Add-on sync failed", ex.Message); }
    }

    private void Back()
    {
        var previous = _history.Count > 0 ? _history.Pop() : new PageState("Home");
        _ = ShowPageAsync(previous, false);
    }
}
