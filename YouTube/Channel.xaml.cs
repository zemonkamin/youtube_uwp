using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace YouTube
{
    public sealed partial class Channel : Page
    {
        private enum ChannelSubscriptionState
        {
            Unknown,
            NotSubscribed,
            Subscribed
        }

        private enum ChannelNotificationState
        {
            Unknown,
            Default,
            All,
            None
        }

        private const string InnertubeApiKey = "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8";
        private const string WebClientVersion = "2.20260220.00.00";
        // Protobuf selector for a channel's "Videos" tab.
        private const string ChannelVideosTabParams = "EgZ2aWRlb3PyBgQKAjoA";
        private const string TvClientName = "TVHTML5";
        private const string TvClientVersion = "7.20260429.11.00";
        private const string TvClientHeaderName = "85";
        private const string TvUserAgent = "Mozilla/5.0 (Web0S; Linux; SmartTV) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/79.0.3945.79 Safari/537.36 YouTube/7.20260429.11.00";
        private const string MwebClientName = "MWEB";
        private const string MwebClientVersion = "2.20251222.01.00";
        private const string MwebClientHeaderName = "2";
        private const string MwebUserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";
        private const string DefaultSubscribeParams = "CgIIAxgA";
        private const string DefaultUnsubscribeParams = "EgIIAxgA";
        private const int InitialVideoCount = 50;
        private const int MaxParserNodes = 7000;
        private const double DefaultCardWidth = 360.0;
        private const double VideoThumbnailAspectRatio = 16.0 / 9.0;
        private const string ResponsiveCardTag = "ResponsiveCard";
        private static readonly Thickness PortraitCardMargin = new Thickness(0, 0, 0, 16);
        private static readonly Thickness LandscapeCardMargin = new Thickness(8, 0, 8, 16);

        private readonly HttpClient _httpClient = new HttpClient();
        private readonly ObservableCollection<VideoCardItem> _videos = new ObservableCollection<VideoCardItem>();
        private string _channelParameter = string.Empty;
        private string _currentChannelId = string.Empty;
        private string _fullDescription = string.Empty;
        private bool _isDescriptionBottomSheetOpen;
        private bool _isSubscriptionMenuOpen;
        private ChannelSubscriptionState _currentSubscriptionState = ChannelSubscriptionState.Unknown;
        private ChannelNotificationState _currentNotificationState = ChannelNotificationState.Default;
        private bool _subscriptionRequestInProgress;
        private int _subscriptionStateGeneration;
        private string _subscribeParams = string.Empty;
        private string _unsubscribeParams = string.Empty;
        private string _subscribeClickTrackingParams = string.Empty;
        private string _unsubscribeClickTrackingParams = string.Empty;

        private double _descriptionInitialY;
        private double _descriptionInitialTransformY;
        private bool _descriptionIsDragging;

        private double _subscriptionMenuInitialY;
        private double _subscriptionMenuInitialTransformY;
        private bool _subscriptionMenuIsDragging;

        public Channel()
        {
            this.InitializeComponent();
            VideosItemsControl.ItemsSource = _videos;
            VideosItemsControl.SizeChanged += VideosItemsControl_SizeChanged;

            this.Loaded += Channel_Loaded;
            this.Unloaded += Channel_Unloaded;
            Window.Current.SizeChanged += Window_SizeChanged;
            SystemNavigationManager.GetForCurrentView().BackRequested += Channel_BackRequested;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _channelParameter = e.Parameter == null ? string.Empty : e.Parameter.ToString();
            await LoadChannelDataAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            SystemNavigationManager.GetForCurrentView().BackRequested -= Channel_BackRequested;
        }

        private void Channel_Loaded(object sender, RoutedEventArgs e)
        {
            Window.Current.SizeChanged -= Window_SizeChanged;
            Window.Current.SizeChanged += Window_SizeChanged;
            VideosItemsControl.SizeChanged -= VideosItemsControl_SizeChanged;
            VideosItemsControl.SizeChanged += VideosItemsControl_SizeChanged;
            UpdateResponsiveCardLayouts();
        }

        private void Channel_Unloaded(object sender, RoutedEventArgs e)
        {
            Window.Current.SizeChanged -= Window_SizeChanged;
            VideosItemsControl.SizeChanged -= VideosItemsControl_SizeChanged;
        }

        private async Task LoadChannelDataAsync()
        {
            if (string.IsNullOrWhiteSpace(_channelParameter))
            {
                ShowErrorPanel("Channel was not provided");
                return;
            }

            try
            {
                LoadingGrid.Visibility = Visibility.Visible;
                if (LoadingRing != null)
                {
                    LoadingRing.IsActive = true;
                }
                MainContent.Visibility = Visibility.Collapsed;
                ErrorPanel.Visibility = Visibility.Collapsed;
                _videos.Clear();
                ResetSubscriptionUi();

                var data = await FetchChannelDataAsync(_channelParameter, InitialVideoCount);
                if (data == null || data.Info == null)
                {
                    ShowErrorPanel("Could not load channel");
                    return;
                }

                ApplyChannelInfo(data.Info);
                ApplySubscriptionEndpointData(data.SubscriptionState);
                await LoadChannelSubscriptionStateAsync(_currentChannelId, data.SubscriptionState);

                foreach (var video in data.Videos)
                {
                    _videos.Add(video);
                }

                LoadingGrid.Visibility = Visibility.Collapsed;
                if (LoadingRing != null)
                {
                    LoadingRing.IsActive = false;
                }
                MainContent.Visibility = Visibility.Visible;
                UpdateResponsiveCardLayouts();

                // Playlists shelf fills in after the main content is already on screen.
                await LoadChannelPlaylistsAsync(_currentChannelId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Channel] Load error: " + ex.Message);
                ShowErrorPanel("Channel loading error");
            }
        }

        private async Task LoadChannelPlaylistsAsync(string channelId)
        {
            if (ChannelPlaylistsSection == null || ChannelPlaylistsItemsControl == null)
            {
                return;
            }

            ChannelPlaylistsSection.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(channelId))
            {
                return;
            }

            try
            {
                var playlists = await Config.GetChannelPlaylistsAsync(channelId, 25);
                if (playlists == null || playlists.Count == 0)
                {
                    return;
                }

                // The channel may have changed while this was in flight.
                if (!string.Equals(_currentChannelId, channelId, StringComparison.Ordinal))
                {
                    return;
                }

                ChannelPlaylistsItemsControl.ItemsSource = playlists;
                ChannelPlaylistsSection.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Channel] Playlists load error: " + ex.Message);
            }
        }

        private void ChannelPlaylistCard_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button != null ? button.DataContext as PlaylistItem : null;
            if (item == null || string.IsNullOrWhiteSpace(item.PlaylistId))
            {
                return;
            }

            Frame.Navigate(typeof(Playlist), item.PlaylistId);
        }

        private void ApplyChannelInfo(ChannelPageInfo info)
        {
            _currentChannelId = FirstNonEmpty(info.ChannelId, NormalizeChannelId(_channelParameter));
            ChannelTitle.Text = FirstNonEmpty(info.Title, "Channel");
            ChannelHandle.Text = FirstNonEmpty(info.Handle, string.Empty);
            ChannelHandle.Visibility = string.IsNullOrWhiteSpace(ChannelHandle.Text) ? Visibility.Collapsed : Visibility.Visible;

            ChannelStats.Text = BuildStatsText(info.SubscriberCount, info.VideoCount);
            _fullDescription = FirstNonEmpty(info.Description, "No description available.");
            ChannelDescription.Text = _fullDescription;
            FullDescriptionText.Text = _fullDescription;
            DescriptionButton.Visibility = string.IsNullOrWhiteSpace(info.Description) ? Visibility.Collapsed : Visibility.Visible;

            SetImageBrushSource(ChannelIconBrush, info.ThumbnailUrl);

            // Many channels simply have no banner in the API response — show the strip only
            // when there is artwork, instead of an empty grey box.
            var hasBanner = !string.IsNullOrWhiteSpace(info.BannerUrl);
            if (ChannelBannerSection != null)
            {
                ChannelBannerSection.Visibility = hasBanner ? Visibility.Visible : Visibility.Collapsed;
            }
            if (hasBanner)
            {
                SetImageBrushSource(ChannelBannerBrush, info.BannerUrl);
            }

            UpdateSubscriptionVisualState();
        }

        private static void SetImageBrushSource(ImageBrush brush, string url)
        {
            if (brush == null || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            try
            {
                brush.ImageSource = new BitmapImage(new Uri(NormalizeImageUrl(url)));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Channel] Image error: " + ex.Message);
            }
        }

        private static string NormalizeImageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            if (url.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + url;
            }

            return url;
        }

        private void ShowErrorPanel(string errorMessage)
        {
            LoadingGrid.Visibility = Visibility.Collapsed;
            if (LoadingRing != null)
            {
                LoadingRing.IsActive = false;
            }
            MainContent.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Text = errorMessage;
        }

        private async void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadChannelDataAsync();
        }

        private void VideoCard_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null)
            {
                return;
            }

            var item = button.DataContext as VideoCardItem;
            if (item == null || string.IsNullOrWhiteSpace(item.VideoId))
            {
                return;
            }

            // Carry a mix / "jam" playlist so the video page can show its queue.
            if (!string.IsNullOrWhiteSpace(item.PlaylistId))
            {
                Frame.Navigate(typeof(Video), new VideoNavigationArgs
                {
                    VideoId = item.VideoId,
                    PlaylistId = item.PlaylistId
                });
            }
            else
            {
                Frame.Navigate(typeof(Video), item.VideoId);
            }
        }

        private async Task<ChannelPageData> FetchChannelDataAsync(string channelInput, int count)
        {
            var channelId = NormalizeChannelId(channelInput);
            if (string.IsNullOrWhiteSpace(channelId))
            {
                channelId = await ResolveHandleToChannelIdAsync(channelInput);
            }

            if (string.IsNullOrWhiteSpace(channelId))
            {
                return null;
            }

            var payload = BuildBrowsePayload(channelId);
            var json = await PostInnertubeAsync("browse", payload);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var root = JsonValue.Parse(json).GetObject();
            var info = ExtractChannelInfo(root, channelId);
            var subscriptionState = ExtractSubscriptionStateFromBrowse(root, "public /browse", channelId);
            var videosContent = FindVideosContent(root);
            var videos = new List<VideoCardItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = 0;

            ExtractVideosRecursively(videosContent ?? root, videos, info.Title, seen, count, ref visited);

            return new ChannelPageData
            {
                Info = info,
                Videos = videos,
                SubscriptionState = subscriptionState
            };
        }

        private async Task<string> ResolveHandleToChannelIdAsync(string input)
        {
            var handle = NormalizeHandle(input);
            if (string.IsNullOrWhiteSpace(handle))
            {
                return string.Empty;
            }

            var payload = new JsonObject();
            payload["context"] = BuildContext();
            payload["url"] = JsonValue.CreateStringValue("https://www.youtube.com/@" + handle);

            var json = await PostInnertubeAsync("navigation/resolve_url", payload.Stringify());
            if (string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            try
            {
                var root = JsonValue.Parse(json).GetObject();
                var endpoint = GetObject(root, "endpoint");
                var browseEndpoint = GetObject(endpoint, "browseEndpoint");
                return GetString(browseEndpoint, "browseId");
            }
            catch
            {
                return string.Empty;
            }
        }

        private async Task<string> PostInnertubeAsync(string endpoint, string payload)
        {
            var url = "https://www.youtube.com/youtubei/v1/" + endpoint + "?key=" + InnertubeApiKey;
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", WebClientVersion);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    System.Diagnostics.Debug.WriteLine("[Channel] Innertube " + endpoint + " failed: " + response.StatusCode + " " + error);
                    return string.Empty;
                }

                return await response.Content.ReadAsStringAsync();
            }
        }

        private static string BuildBrowsePayload(string channelId)
        {
            var payload = new JsonObject();
            payload["context"] = BuildContext();
            payload["browseId"] = JsonValue.CreateStringValue(channelId);
            // Ask for the Videos tab. The default (home) tab returns promo shelves that may hold
            // other channels' content and no plain video list, which left the page empty. The
            // channel metadata we need is present in this response too.
            payload["params"] = JsonValue.CreateStringValue(ChannelVideosTabParams);
            return payload.Stringify();
        }

        private static JsonObject BuildContext()
        {
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue("WEB");
            client["clientVersion"] = JsonValue.CreateStringValue(WebClientVersion);
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);

            var context = new JsonObject();
            context["client"] = client;
            return context;
        }

        private static ChannelPageInfo ExtractChannelInfo(JsonObject root, string channelId)
        {
            var info = new ChannelPageInfo();
            var metadata = GetObject(GetObject(root, "metadata"), "channelMetadataRenderer");
            info.Title = FirstNonEmpty(GetString(metadata, "title"), "Channel");
            info.Description = GetString(metadata, "description");

            var externalId = FirstNonEmpty(GetString(metadata, "externalId"), channelId);
            info.ChannelId = externalId;
            info.ThumbnailUrl = ExtractThumbnailUrl(GetObject(metadata, "avatar"));
            if (string.IsNullOrWhiteSpace(info.ThumbnailUrl))
            {
                info.ThumbnailUrl = ExtractFirstImageUrl(root, "avatar");
            }

            info.BannerUrl = ExtractBannerUrl(root);
            info.Handle = ExtractHandle(root, metadata, externalId);
            ExtractStats(root, info);
            return info;
        }

        private static void ExtractStats(JsonObject root, ChannelPageInfo info)
        {
            // Same layout used by yt-api-legacy channel.rs:
            // header.pageHeaderRenderer.content.pageHeaderViewModel.metadata.contentMetadataViewModel.metadataRows[1].metadataParts
            var metadataViewModel = GetObjectPath(root, "header", "pageHeaderRenderer", "content", "pageHeaderViewModel", "metadata", "contentMetadataViewModel");
            var rows = GetArray(metadataViewModel, "metadataRows");

            if (rows != null && rows.Count > 1)
            {
                var countRow = rows.GetObjectAt(1);
                var parts = GetArray(countRow, "metadataParts");

                if (parts != null && parts.Count > 0)
                {
                    var subscriberText = ExtractMetadataPartText(parts.GetObjectAt(0));
                    if (!string.IsNullOrWhiteSpace(subscriberText))
                    {
                        info.SubscriberCount = ParseNumberText(subscriberText);
                    }
                }

                if (parts != null && parts.Count > 1)
                {
                    var videoText = ExtractMetadataPartText(parts.GetObjectAt(1));
                    if (!string.IsNullOrWhiteSpace(videoText))
                    {
                        info.VideoCount = ParseNumberText(videoText);
                    }
                }
            }

            // Fallback for older/alternate YouTube layouts.
            if (string.IsNullOrWhiteSpace(info.SubscriberCount) || info.SubscriberCount == "0" ||
                string.IsNullOrWhiteSpace(info.VideoCount) || info.VideoCount == "0")
            {
                ExtractStatsFallback(root, info);
            }
        }

        private static string ExtractMetadataPartText(JsonObject part)
        {
            return ExtractText(GetObject(part, "text"));
        }

        private static void ExtractStatsFallback(JsonObject root, ChannelPageInfo info)
        {
            var metadataViewModel = GetObjectPath(root, "header", "pageHeaderRenderer", "content", "pageHeaderViewModel", "metadata", "contentMetadataViewModel");
            var rows = GetArray(metadataViewModel, "metadataRows");
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            for (uint i = 0; i < rows.Count; i++)
            {
                var row = rows.GetObjectAt(i);
                var parts = GetArray(row, "metadataParts");
                if (parts == null)
                {
                    continue;
                }

                for (uint p = 0; p < parts.Count; p++)
                {
                    var text = ExtractMetadataPartText(parts.GetObjectAt(p));
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    var lower = text.ToLowerInvariant();
                    if ((string.IsNullOrWhiteSpace(info.SubscriberCount) || info.SubscriberCount == "0") &&
                        (lower.Contains("subscriber") || lower.Contains("подпис")))
                    {
                        info.SubscriberCount = ParseNumberText(text);
                    }
                    else if ((string.IsNullOrWhiteSpace(info.VideoCount) || info.VideoCount == "0") &&
                             (lower.Contains("video") || lower.Contains("видео")))
                    {
                        info.VideoCount = ParseNumberText(text);
                    }
                    else if (text.StartsWith("@") && string.IsNullOrWhiteSpace(info.Handle))
                    {
                        info.Handle = text;
                    }
                }
            }
        }

        private static string ExtractBannerUrl(JsonObject root)
        {
            var bannerImage = GetObjectPath(root, "header", "pageHeaderRenderer", "content", "pageHeaderViewModel", "banner", "imageBannerViewModel", "image");
            var sources = GetArray(bannerImage, "sources");
            var url = LastUrlFromArray(sources);
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            return ExtractFirstImageUrl(root, "banner");
        }

        private static string ExtractHandle(JsonObject root, JsonObject metadata, string externalId)
        {
            var vanityUrl = FirstNonEmpty(GetString(metadata, "vanityChannelUrl"), GetString(metadata, "channelUrl"));
            if (!string.IsNullOrWhiteSpace(vanityUrl))
            {
                var marker = "/@";
                var index = vanityUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    return "@" + vanityUrl.Substring(index + marker.Length).Trim('/');
                }
            }

            var fromRows = FindFirstTextStartingWithAt(root, 0);
            if (!string.IsNullOrWhiteSpace(fromRows))
            {
                return fromRows;
            }

            return string.Empty;
        }

        private static IJsonValue FindVideosContent(JsonObject root)
        {
            var tabs = GetArray(GetObject(GetObject(root, "contents"), "twoColumnBrowseResultsRenderer"), "tabs");
            if (tabs == null)
            {
                return null;
            }

            IJsonValue selectedContent = null;

            for (uint i = 0; i < tabs.Count; i++)
            {
                var tab = tabs.GetObjectAt(i);
                var renderer = GetObject(tab, "tabRenderer");
                var title = GetString(renderer, "title");

                if (renderer != null && renderer.ContainsKey("content") && IsSelected(renderer))
                {
                    selectedContent = renderer["content"];
                }

                if ((string.Equals(title, "Videos", StringComparison.OrdinalIgnoreCase) || string.Equals(title, "Видео", StringComparison.OrdinalIgnoreCase)) && renderer != null && renderer.ContainsKey("content"))
                {
                    return renderer["content"];
                }
            }

            return selectedContent;
        }

        private static bool IsSelected(JsonObject renderer)
        {
            if (renderer == null || !renderer.ContainsKey("selected"))
            {
                return false;
            }

            var selected = renderer["selected"];
            return selected != null && selected.ValueType == JsonValueType.Boolean && selected.GetBoolean();
        }

        private static void ExtractVideosRecursively(IJsonValue value, List<VideoCardItem> videos, string channelTitle, HashSet<string> seen, int maxCount, ref int visited)
        {
            if (value == null || videos.Count >= maxCount || visited >= MaxParserNodes)
            {
                return;
            }

            visited++;

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                VideoCardItem item = null;

                if (obj.ContainsKey("videoRenderer"))
                {
                    item = ParseVideoRenderer(obj.GetNamedObject("videoRenderer"), channelTitle);
                }
                else if (obj.ContainsKey("gridVideoRenderer"))
                {
                    item = ParseVideoRenderer(obj.GetNamedObject("gridVideoRenderer"), channelTitle);
                }
                else if (obj.ContainsKey("compactVideoRenderer"))
                {
                    item = ParseVideoRenderer(obj.GetNamedObject("compactVideoRenderer"), channelTitle);
                }
                else if (obj.ContainsKey("reelItemRenderer"))
                {
                    item = ParseReelItemRenderer(obj.GetNamedObject("reelItemRenderer"), channelTitle);
                }
                else if (obj.ContainsKey("lockupViewModel"))
                {
                    // Current YouTube responses deliver channel videos as lockupViewModel cards
                    // (wrapped in richItemRenderer). Without this the list came back empty.
                    item = ParseLockupVideo(obj.GetNamedObject("lockupViewModel"), channelTitle);
                }

                if (item != null && !string.IsNullOrWhiteSpace(item.VideoId) && !seen.Contains(item.VideoId))
                {
                    seen.Add(item.VideoId);
                    videos.Add(item);
                    if (videos.Count >= maxCount)
                    {
                        return;
                    }
                }

                foreach (var child in obj)
                {
                    ExtractVideosRecursively(child.Value, videos, channelTitle, seen, maxCount, ref visited);
                    if (videos.Count >= maxCount || visited >= MaxParserNodes)
                    {
                        return;
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (uint i = 0; i < array.Count; i++)
                {
                    ExtractVideosRecursively(array[(int)i], videos, channelTitle, seen, maxCount, ref visited);
                    if (videos.Count >= maxCount || visited >= MaxParserNodes)
                    {
                        return;
                    }
                }
            }
        }

        private static VideoCardItem ParseVideoRenderer(JsonObject renderer, string channelTitle)
        {
            if (renderer == null)
            {
                return null;
            }

            var videoId = GetString(renderer, "videoId");
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return null;
            }

            return new VideoCardItem
            {
                VideoId = videoId,
                Title = FirstNonEmpty(ExtractText(GetObject(renderer, "title")), "No title"),
                ChannelTitle = FirstNonEmpty(channelTitle, ExtractText(GetObject(renderer, "ownerText")), ExtractText(GetObject(renderer, "shortBylineText"))),
                Duration = FirstNonEmpty(ExtractText(GetObject(renderer, "lengthText")), ExtractDurationFromOverlays(renderer), string.Empty),
                ThumbnailUrl = "https://i.ytimg.com/vi/" + videoId + "/mqdefault.jpg"
            };
        }

        private static VideoCardItem ParseReelItemRenderer(JsonObject renderer, string channelTitle)
        {
            if (renderer == null)
            {
                return null;
            }

            var videoId = GetString(renderer, "videoId");
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return null;
            }

            return new VideoCardItem
            {
                VideoId = videoId,
                Title = FirstNonEmpty(ExtractText(GetObject(renderer, "headline")), "Shorts"),
                ChannelTitle = FirstNonEmpty(channelTitle, string.Empty),
                Duration = "Shorts",
                ThumbnailUrl = "https://i.ytimg.com/vi/" + videoId + "/mqdefault.jpg"
            };
        }

        // lockupViewModel video card (the shape the Videos tab returns today).
        private static VideoCardItem ParseLockupVideo(JsonObject lockup, string channelTitle)
        {
            if (lockup == null)
            {
                return null;
            }

            // Skip playlist / channel lockups — only real videos belong in this list.
            var contentType = GetString(lockup, "contentType");
            if (!string.IsNullOrWhiteSpace(contentType) &&
                contentType.IndexOf("VIDEO", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return null;
            }

            var videoId = GetString(lockup, "contentId");
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return null;
            }

            var meta = GetObjectPath(lockup, "metadata", "lockupMetadataViewModel");
            var title = GetString(GetObject(meta, "title"), "content");

            // On a channel's Videos tab the rows omit the channel name, so the only row is
            // ["125K views", "8 hours ago"]. Elsewhere row 0 is the channel and the stats sit in
            // the next one — reading the LAST row covers both layouts.
            var views = string.Empty;
            var published = string.Empty;
            var rows = GetArray(GetObjectPath(meta, "metadata", "contentMetadataViewModel"), "metadataRows");
            if (rows != null && rows.Count > 0)
            {
                for (var r = rows.Count - 1; r >= 0 && string.IsNullOrWhiteSpace(views); r--)
                {
                    if (rows[r].ValueType != JsonValueType.Object) continue;
                    var parts = GetArray(rows[r].GetObject(), "metadataParts");
                    if (parts == null || parts.Count == 0) continue;

                    var first = parts[0].ValueType == JsonValueType.Object
                        ? GetString(GetObject(parts[0].GetObject(), "text"), "content") : string.Empty;
                    var second = parts.Count > 1 && parts[1].ValueType == JsonValueType.Object
                        ? GetString(GetObject(parts[1].GetObject(), "text"), "content") : string.Empty;

                    // A stats row has at least two parts (views + date).
                    if (!string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second))
                    {
                        views = first;
                        published = second;
                    }
                }
            }

            return new VideoCardItem
            {
                VideoId = videoId,
                Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title,
                ChannelTitle = channelTitle,
                Duration = ExtractDurationFromLockup(lockup),
                ThumbnailUrl = "https://i.ytimg.com/vi/" + videoId + "/hqdefault.jpg",
                ViewCount = views,
                PublishedText = published
            };
        }

        // contentImage.thumbnailViewModel.overlays[].thumbnailBottomOverlayViewModel.badges[].thumbnailBadgeViewModel.text
        private static string ExtractDurationFromLockup(JsonObject lockup)
        {
            try
            {
                var overlays = GetArray(GetObjectPath(lockup, "contentImage", "thumbnailViewModel"), "overlays");
                if (overlays == null) return string.Empty;

                for (var i = 0; i < overlays.Count; i++)
                {
                    if (overlays[i].ValueType != JsonValueType.Object) continue;
                    var badges = GetArray(GetObject(overlays[i].GetObject(), "thumbnailBottomOverlayViewModel"), "badges");
                    if (badges == null) continue;

                    for (var b = 0; b < badges.Count; b++)
                    {
                        if (badges[b].ValueType != JsonValueType.Object) continue;
                        var text = GetString(GetObject(badges[b].GetObject(), "thumbnailBadgeViewModel"), "text");
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ExtractDurationFromOverlays(JsonObject renderer)
        {
            var overlays = GetArray(renderer, "thumbnailOverlays");
            if (overlays == null)
            {
                return string.Empty;
            }

            for (uint i = 0; i < overlays.Count; i++)
            {
                var overlay = overlays.GetObjectAt(i);
                var timeRenderer = GetObject(overlay, "thumbnailOverlayTimeStatusRenderer");
                var text = ExtractText(GetObject(timeRenderer, "text"));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return string.Empty;
        }

        private static JsonObject GetObject(JsonObject obj, string key)
        {
            if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
            {
                return null;
            }

            var value = obj[key];
            return value != null && value.ValueType == JsonValueType.Object ? value.GetObject() : null;
        }

        private static JsonArray GetArray(JsonObject obj, string key)
        {
            if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
            {
                return null;
            }

            var value = obj[key];
            return value != null && value.ValueType == JsonValueType.Array ? value.GetArray() : null;
        }

        private static JsonObject GetObjectPath(JsonObject root, params string[] keys)
        {
            var current = root;
            if (current == null || keys == null)
            {
                return null;
            }

            for (int i = 0; i < keys.Length; i++)
            {
                current = GetObject(current, keys[i]);
                if (current == null)
                {
                    return null;
                }
            }

            return current;
        }

        private static string GetString(JsonObject obj, string key)
        {
            if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
            {
                return string.Empty;
            }

            var value = obj[key];
            return value != null && value.ValueType == JsonValueType.String ? value.GetString() : string.Empty;
        }

        private static string ExtractText(JsonObject textObject)
        {
            if (textObject == null)
            {
                return string.Empty;
            }

            var simpleText = GetString(textObject, "simpleText");
            if (!string.IsNullOrWhiteSpace(simpleText))
            {
                return simpleText;
            }

            var content = GetString(textObject, "content");
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content;
            }

            var runs = GetArray(textObject, "runs");
            if (runs == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            for (uint i = 0; i < runs.Count; i++)
            {
                var run = runs.GetObjectAt(i);
                sb.Append(GetString(run, "text"));
            }

            return sb.ToString();
        }

        private static string ExtractThumbnailUrl(JsonObject imageObject)
        {
            if (imageObject == null)
            {
                return string.Empty;
            }

            return FirstNonEmpty(LastUrlFromArray(GetArray(imageObject, "thumbnails")), LastUrlFromArray(GetArray(imageObject, "sources")));
        }

        private static string LastUrlFromArray(JsonArray array)
        {
            if (array == null || array.Count == 0)
            {
                return string.Empty;
            }

            for (int i = (int)array.Count - 1; i >= 0; i--)
            {
                var obj = array.GetObjectAt((uint)i);
                var url = GetString(obj, "url");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }

            return string.Empty;
        }

        private static string ExtractFirstImageUrl(IJsonValue value, string preferredKey)
        {
            var visited = 0;
            return ExtractFirstImageUrlInternal(value, preferredKey, ref visited);
        }

        private static string ExtractFirstImageUrlInternal(IJsonValue value, string preferredKey, ref int visited)
        {
            if (value == null || visited >= MaxParserNodes)
            {
                return string.Empty;
            }

            visited++;

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                if (obj.ContainsKey(preferredKey))
                {
                    var url = ExtractThumbnailUrl(GetObject(obj, preferredKey));
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }
                }

                foreach (var child in obj)
                {
                    var url = ExtractFirstImageUrlInternal(child.Value, preferredKey, ref visited);
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (uint i = 0; i < array.Count; i++)
                {
                    var url = ExtractFirstImageUrlInternal(array[(int)i], preferredKey, ref visited);
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }
                }
            }

            return string.Empty;
        }

        private static string FindFirstTextStartingWithAt(IJsonValue value, int depth)
        {
            if (value == null || depth > 7)
            {
                return string.Empty;
            }

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                var text = ExtractText(obj);
                if (!string.IsNullOrWhiteSpace(text) && text.StartsWith("@"))
                {
                    return text;
                }

                foreach (var child in obj)
                {
                    var result = FindFirstTextStartingWithAt(child.Value, depth + 1);
                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        return result;
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (uint i = 0; i < array.Count; i++)
                {
                    var result = FindFirstTextStartingWithAt(array[(int)i], depth + 1);
                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        return result;
                    }
                }
            }

            return string.Empty;
        }

        private static string NormalizeChannelId(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            var text = input.Trim();
            var marker = "/channel/";
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                text = text.Substring(index + marker.Length).Trim('/');
            }

            return text.StartsWith("UC", StringComparison.OrdinalIgnoreCase) && text.Length >= 20 ? text : string.Empty;
        }

        private static string NormalizeHandle(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            var text = input.Trim();
            var marker = "/@";
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                text = text.Substring(index + marker.Length).Trim('/');
            }

            return text.TrimStart('@').Trim();
        }

        private static string ParseNumberText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "0";
            }

            var lower = text.Trim().ToLowerInvariant();
            var multiplier = 1.0;

            if (lower.Contains("k") || lower.Contains("thousand") || lower.Contains("тыс"))
            {
                multiplier = 1000.0;
            }
            else if (lower.Contains("m") || lower.Contains("million") || lower.Contains("млн"))
            {
                multiplier = 1000000.0;
            }
            else if (lower.Contains("b") || lower.Contains("billion") || lower.Contains("млрд"))
            {
                multiplier = 1000000000.0;
            }

            // Matches channel.rs behavior: keep digits and decimal dots only.
            // This also fixes values like "1,234 videos" => "1234" instead of "1.234".
            var sb = new StringBuilder();
            foreach (var ch in lower)
            {
                if ((ch >= '0' && ch <= '9') || ch == '.')
                {
                    sb.Append(ch);
                }
            }

            double number;
            if (double.TryParse(sb.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number))
            {
                return ((ulong)Math.Round(number * multiplier)).ToString(CultureInfo.InvariantCulture);
            }

            return "0";
        }

        private static string BuildStatsText(string subscribers, string videos)
        {
            var subsRaw = ParseNumberText(subscribers);
            var vidsRaw = ParseNumberText(videos);

            var subs = FormatCompactNumber(subsRaw);
            var vids = FormatPlainNumber(vidsRaw);

            if (string.IsNullOrWhiteSpace(subs))
            {
                subs = "0";
            }

            if (string.IsNullOrWhiteSpace(vids))
            {
                vids = "0";
            }

            return subs + " " + Pluralize("subscriber", subsRaw) + " • " + vids + " " + Pluralize("video", vidsRaw);
        }

        private static string FormatCompactNumber(string raw)
        {
            ulong value;
            if (!ulong.TryParse(ParseNumberText(raw), NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            {
                return raw;
            }

            if (value >= 1000000000UL)
            {
                return FormatOneDecimal(value / 1000000000.0) + "B";
            }
            if (value >= 1000000UL)
            {
                return FormatOneDecimal(value / 1000000.0) + "M";
            }
            if (value >= 1000UL)
            {
                return FormatOneDecimal(value / 1000.0) + "K";
            }

            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatPlainNumber(string raw)
        {
            ulong value;
            if (!ulong.TryParse(ParseNumberText(raw), NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            {
                return raw;
            }

            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        private static string FormatOneDecimal(double value)
        {
            var rounded = Math.Round(value, 1);
            if (Math.Abs(rounded - Math.Round(rounded)) < 0.05)
            {
                return Math.Round(rounded).ToString(CultureInfo.InvariantCulture);
            }

            return rounded.ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static string Pluralize(string word, string rawNumber)
        {
            ulong value;
            if (ulong.TryParse(ParseNumberText(rawNumber), NumberStyles.Any, CultureInfo.InvariantCulture, out value) && value == 1UL)
            {
                return word;
            }

            return word + "s";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return string.Empty;
            }

            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private void Window_SizeChanged(object sender, Windows.UI.Core.WindowSizeChangedEventArgs e)
        {
            UpdateResponsiveCardLayouts();
        }

        private void VideosItemsControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateResponsiveCardLayouts();
        }

        // Decoded at card width with a maxres->API-thumbnail fallback; see ThumbnailImageLoader
        // for why a Source binding was replaced by per-item assignment.
        private void CardThumbnail_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var image = sender as Image;
            if (image == null)
            {
                return;
            }

            var item = image.DataContext as VideoCardItem;
            if (item == null || string.IsNullOrWhiteSpace(item.LargeThumbnailUrl))
            {
                image.Source = null;
                return;
            }

            ThumbnailImageLoader.Assign(image, item.LargeThumbnailUrl, item.ThumbnailUrl, 360);
        }

        private void VideoThumbnailHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var host = sender as FrameworkElement;
            if (host == null)
            {
                return;
            }

            var width = e.NewSize.Width;
            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
            {
                return;
            }

            var targetHeight = Math.Round(width / VideoThumbnailAspectRatio);
            if (double.IsNaN(host.Height) || Math.Abs(host.Height - targetHeight) > 0.5)
            {
                host.Height = targetHeight;
            }
        }

        private void ResponsiveCard_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyResponsiveCardMargin(sender as FrameworkElement);
        }

        private void ResponsiveCard_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyResponsiveCardMargin(sender as FrameworkElement);
        }

        private void ApplyResponsiveCardMargin(FrameworkElement element)
        {
            if (element == null)
            {
                return;
            }

            var targetMargin = IsPortraitOrientation()
                ? PortraitCardMargin
                : LandscapeCardMargin;

            if (Math.Abs(element.Margin.Left - targetMargin.Left) > 0.5 ||
                Math.Abs(element.Margin.Top - targetMargin.Top) > 0.5 ||
                Math.Abs(element.Margin.Right - targetMargin.Right) > 0.5 ||
                Math.Abs(element.Margin.Bottom - targetMargin.Bottom) > 0.5)
            {
                element.Margin = targetMargin;
            }
        }

        private void UpdateResponsiveCardMargins(DependencyObject root)
        {
            if (root == null)
            {
                return;
            }

            var element = root as FrameworkElement;
            if (element != null && IsResponsiveCardElement(element))
            {
                ApplyResponsiveCardMargin(element);
            }

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                UpdateResponsiveCardMargins(VisualTreeHelper.GetChild(root, i));
            }
        }

        private static bool IsResponsiveCardElement(FrameworkElement element)
        {
            if (element == null)
            {
                return false;
            }

            if (element is Button)
            {
                return true;
            }

            return string.Equals(element.Tag as string, ResponsiveCardTag, StringComparison.Ordinal);
        }

        private static bool IsPortraitOrientation()
        {
            return Window.Current.Bounds.Height > Window.Current.Bounds.Width;
        }

        private void UpdateResponsiveCardLayouts()
        {
            bool isPortrait = IsPortraitOrientation();

            if (VideosItemsControl != null)
            {
                VideosItemsControl.Padding = isPortrait
                    ? new Thickness(0, 8, 0, 16)
                    : new Thickness(8, 8, 8, 16);
            }

            double baseWidth = GetItemsControlContentWidth(VideosItemsControl, Window.Current.Bounds.Width);
            var itemWidth = isPortrait ? Math.Max(0, baseWidth) : DefaultCardWidth;
            var maxColumns = isPortrait ? 1 : 3;
            UpdateItemsWrapGrid(VideosItemsControl, itemWidth, maxColumns);
            UpdateResponsiveCardMargins(VideosItemsControl);
        }

        private static double GetItemsControlContentWidth(Control control, double fallbackWidth)
        {
            double width = control != null && control.ActualWidth > 0
                ? control.ActualWidth
                : fallbackWidth;

            if (control != null)
            {
                width -= control.Padding.Left + control.Padding.Right;
            }

            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
            {
                width = fallbackWidth;
            }

            return Math.Max(0, width);
        }

        private static void UpdateItemsWrapGrid(DependencyObject root, double itemWidth, int maxColumns)
        {
            if (root == null)
            {
                return;
            }

            var wrapGrid = FindDescendant<ItemsWrapGrid>(root);
            if (wrapGrid != null)
            {
                wrapGrid.ItemWidth = itemWidth;
                wrapGrid.MaximumRowsOrColumns = maxColumns;
            }
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
            {
                return null;
            }

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var typedChild = child as T;
                if (typedChild != null)
                {
                    return typedChild;
                }

                var descendant = FindDescendant<T>(child);
                if (descendant != null)
                {
                    return descendant;
                }
            }

            return null;
        }

        private void DescriptionButton_Click(object sender, RoutedEventArgs e)
        {
            AnimateDescriptionBottomSheet(true);
        }

        private void Channel_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (_isSubscriptionMenuOpen)
            {
                e.Handled = true;
                AnimateSubscriptionMenuBottomSheet(false);
                return;
            }

            if (_isDescriptionBottomSheetOpen)
            {
                e.Handled = true;
                AnimateDescriptionBottomSheet(false);
                return;
            }

            if (Frame != null && Frame.CanGoBack)
            {
                e.Handled = true;
                Frame.GoBack();
            }
        }

        private void AnimateDescriptionBottomSheet(bool show)
        {
            _isDescriptionBottomSheetOpen = show;
            OverlayGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            DescriptionBottomSheetPanel.Visibility = Visibility.Visible;

            var storyboard = new Storyboard();
            var animation = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            if (show)
            {
                animation.From = 410;
                animation.To = 0;
            }
            else
            {
                animation.From = DescriptionBottomSheetTransform.Y;
                animation.To = 410;
                storyboard.Completed += (s, e) =>
                {
                    DescriptionBottomSheetPanel.Visibility = Visibility.Collapsed;
                    OverlayGrid.Visibility = Visibility.Collapsed;
                };
            }

            Storyboard.SetTarget(animation, DescriptionBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private void OverlayGrid_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_isSubscriptionMenuOpen)
            {
                AnimateSubscriptionMenuBottomSheet(false);
                return;
            }

            if (_isDescriptionBottomSheetOpen)
            {
                AnimateDescriptionBottomSheet(false);
                return;
            }

            OverlayGrid.Visibility = Visibility.Collapsed;
        }

        private void DescriptionDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateDescriptionBottomSheet(false);
        }

        private void DescriptionDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element != null && element.CapturePointer(e.Pointer))
            {
                _descriptionInitialY = e.GetCurrentPoint(element).Position.Y;
                _descriptionInitialTransformY = DescriptionBottomSheetTransform.Y;
                _descriptionIsDragging = true;
                e.Handled = true;
            }
        }

        private void DescriptionDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_descriptionIsDragging)
            {
                return;
            }

            var element = sender as UIElement;
            if (element == null)
            {
                return;
            }

            var currentY = e.GetCurrentPoint(element).Position.Y;
            var deltaY = currentY - _descriptionInitialY;
            var newY = Math.Max(0, _descriptionInitialTransformY + deltaY);
            DescriptionBottomSheetTransform.Y = newY;
            e.Handled = true;
        }

        private void DescriptionDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_descriptionIsDragging)
            {
                return;
            }

            _descriptionIsDragging = false;
            var element = sender as UIElement;
            if (element != null)
            {
                element.ReleasePointerCapture(e.Pointer);
            }

            if (DescriptionBottomSheetTransform.Y > 205)
            {
                AnimateDescriptionBottomSheet(false);
            }
            else
            {
                AnimateDescriptionBottomSheet(true);
            }

            e.Handled = true;
        }


        private async void SubscribeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSubscriptionState == ChannelSubscriptionState.Subscribed)
            {
                ShowSubscriptionMenuBottomSheet();
                return;
            }

            await SetChannelSubscriptionStateAsync(true);
        }

        private async Task SetChannelSubscriptionStateAsync(bool subscribe)
        {
            if (_subscriptionRequestInProgress || string.IsNullOrWhiteSpace(_currentChannelId))
            {
                return;
            }

            var token = await GetTvAccessTokenAsync(true);
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            var channelIdAtClick = _currentChannelId;
            var oldState = _currentSubscriptionState;
            var oldNotificationState = _currentNotificationState;
            var newState = subscribe ? ChannelSubscriptionState.Subscribed : ChannelSubscriptionState.NotSubscribed;
            var newNotificationState = subscribe ? ChannelNotificationState.Default : ChannelNotificationState.Default;
            var stateGeneration = ++_subscriptionStateGeneration;

            _subscriptionRequestInProgress = true;
            _currentSubscriptionState = newState;
            _currentNotificationState = newNotificationState;
            UpdateSubscriptionVisualState();
            UpdateSubscriptionMenuVisualState();

            try
            {
                var success = await SetChannelSubscriptionAsync(channelIdAtClick, subscribe, token);
                if (stateGeneration != _subscriptionStateGeneration || !string.Equals(channelIdAtClick, _currentChannelId, StringComparison.Ordinal))
                {
                    return;
                }

                if (!success)
                {
                    _currentSubscriptionState = oldState;
                    _currentNotificationState = oldNotificationState;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                    await ShowMessageAsync("Subscription failed", "YouTube rejected the subscription request.");
                    return;
                }

                _currentSubscriptionState = newState;
                _currentNotificationState = newNotificationState;
                UpdateSubscriptionVisualState();
                UpdateSubscriptionMenuVisualState();

                if (!subscribe)
                {
                    AnimateSubscriptionMenuBottomSheet(false);
                }
            }
            catch (Exception ex)
            {
                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, _currentChannelId, StringComparison.Ordinal))
                {
                    _currentSubscriptionState = oldState;
                    _currentNotificationState = oldNotificationState;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                }

                System.Diagnostics.Debug.WriteLine("[Channel] Subscription update error: " + ex.Message);
                await ShowMessageAsync("Subscription failed", ex.Message);
            }
            finally
            {
                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, _currentChannelId, StringComparison.Ordinal))
                {
                    _subscriptionRequestInProgress = false;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                }
            }
        }

        private async Task<bool> SetChannelSubscriptionAsync(string channelId, bool subscribe, string accessToken)
        {
            var endpoint = subscribe ? "subscription/subscribe" : "subscription/unsubscribe";
            var parameters = subscribe ? _subscribeParams : _unsubscribeParams;
            var clickTrackingParams = subscribe ? _subscribeClickTrackingParams : _unsubscribeClickTrackingParams;

            if (string.IsNullOrWhiteSpace(parameters))
            {
                parameters = subscribe ? DefaultSubscribeParams : DefaultUnsubscribeParams;
            }

            var url = BuildInnertubeUrl(endpoint);
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(
                    BuildSubscriptionPayload(channelId, parameters, clickTrackingParams),
                    Encoding.UTF8,
                    "application/json"
                );
                AddYouTubeAuthHeaders(request, accessToken);

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine("[Channel] Subscription update OK: " + (subscribe ? "subscribe" : "unsubscribe"));
                    return true;
                }

                var body = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine("[Channel] Subscription update failed: " + (int)response.StatusCode + " " + response.ReasonPhrase + " " + body);
                return false;
            }
        }

        private async Task<bool> ModifyChannelNotificationPreferenceAsync(ChannelNotificationState targetState)
        {
            if (_subscriptionRequestInProgress || string.IsNullOrWhiteSpace(_currentChannelId))
            {
                return false;
            }

            var token = await GetTvAccessTokenAsync(true);
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var oldState = _currentNotificationState;
            var channelIdAtClick = _currentChannelId;
            var stateGeneration = ++_subscriptionStateGeneration;

            _subscriptionRequestInProgress = true;
            _currentNotificationState = targetState;
            UpdateSubscriptionVisualState();
            UpdateSubscriptionMenuVisualState();

            try
            {
                var url = BuildInnertubeUrl("notification/modify_channel_preference");
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new StringContent(
                        BuildNotificationPreferencePayload(BuildNotificationPreferenceParams(channelIdAtClick, targetState)),
                        Encoding.UTF8,
                        "application/json"
                    );
                    AddYouTubeAuthHeaders(request, token);

                    var response = await _httpClient.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        _currentNotificationState = oldState;
                        UpdateSubscriptionVisualState();
                        UpdateSubscriptionMenuVisualState();
                        System.Diagnostics.Debug.WriteLine("[Channel] Notification update failed: " + (int)response.StatusCode + " " + response.ReasonPhrase + " " + body);
                        await ShowMessageAsync("Notifications failed", "Could not update notification preference.");
                        return false;
                    }
                }

                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, _currentChannelId, StringComparison.Ordinal))
                {
                    _currentNotificationState = targetState;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                    AnimateSubscriptionMenuBottomSheet(false);
                }

                return true;
            }
            catch (Exception ex)
            {
                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, _currentChannelId, StringComparison.Ordinal))
                {
                    _currentNotificationState = oldState;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                }

                System.Diagnostics.Debug.WriteLine("[Channel] Notification update error: " + ex.Message);
                await ShowMessageAsync("Notifications failed", ex.Message);
                return false;
            }
            finally
            {
                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, _currentChannelId, StringComparison.Ordinal))
                {
                    _subscriptionRequestInProgress = false;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                }
            }
        }

        private async void NotificationAllOptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentNotificationState == ChannelNotificationState.All)
            {
                AnimateSubscriptionMenuBottomSheet(false);
                return;
            }

            await ModifyChannelNotificationPreferenceAsync(ChannelNotificationState.All);
        }

        private async void NotificationPersonalizedOptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentNotificationState == ChannelNotificationState.Default || _currentNotificationState == ChannelNotificationState.Unknown)
            {
                AnimateSubscriptionMenuBottomSheet(false);
                return;
            }

            await ModifyChannelNotificationPreferenceAsync(ChannelNotificationState.Default);
        }

        private async void NotificationNoneOptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentNotificationState == ChannelNotificationState.None)
            {
                AnimateSubscriptionMenuBottomSheet(false);
                return;
            }

            await ModifyChannelNotificationPreferenceAsync(ChannelNotificationState.None);
        }

        private async void NotificationUnsubscribeOptionButton_Click(object sender, RoutedEventArgs e)
        {
            await SetChannelSubscriptionStateAsync(false);
        }

        private async Task LoadChannelSubscriptionStateAsync(string channelId, SubscriptionLoadResult preliminaryResult)
        {
            var generation = _subscriptionStateGeneration;

            if (string.IsNullOrWhiteSpace(channelId))
            {
                _currentSubscriptionState = ChannelSubscriptionState.Unknown;
                UpdateSubscriptionVisualState();
                return;
            }

            ApplySubscriptionEndpointData(preliminaryResult);

            var token = await GetTvAccessTokenAsync(false);
            if (generation != _subscriptionStateGeneration || !string.Equals(channelId, _currentChannelId, StringComparison.Ordinal))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                if (preliminaryResult != null && preliminaryResult.Found)
                {
                    ApplyLoadedSubscriptionState(preliminaryResult);
                }
                else
                {
                    _currentSubscriptionState = ChannelSubscriptionState.NotSubscribed;
                    _currentNotificationState = ChannelNotificationState.Default;
                    UpdateSubscriptionVisualState();
                }
                return;
            }

            try
            {
                // Do not use YouTube Data API here. The TV OAuth project can have Data API disabled,
                // while Innertube /browse still returns the signed-in subscribeButtonRenderer.
                var browseResult = await TryLoadChannelSubscriptionFromAuthenticatedBrowseAsync(channelId, token);
                if (generation != _subscriptionStateGeneration || !string.Equals(channelId, _currentChannelId, StringComparison.Ordinal))
                {
                    return;
                }

                if (browseResult != null)
                {
                    ApplySubscriptionEndpointData(browseResult);
                }

                if (browseResult != null && browseResult.Found)
                {
                    ApplyLoadedSubscriptionState(browseResult);
                    System.Diagnostics.Debug.WriteLine("[Channel] Subscription state from " + browseResult.Source + ": " + _currentSubscriptionState + ", " + _currentNotificationState);
                    return;
                }

                if (preliminaryResult != null && preliminaryResult.Found)
                {
                    ApplyLoadedSubscriptionState(preliminaryResult);
                    return;
                }

                _currentSubscriptionState = ChannelSubscriptionState.NotSubscribed;
                _currentNotificationState = ChannelNotificationState.Default;
                UpdateSubscriptionVisualState();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Channel] Subscription state load error: " + ex.Message);
                if (preliminaryResult != null && preliminaryResult.Found)
                {
                    ApplyLoadedSubscriptionState(preliminaryResult);
                }
                else
                {
                    _currentSubscriptionState = ChannelSubscriptionState.NotSubscribed;
                    _currentNotificationState = ChannelNotificationState.Default;
                    UpdateSubscriptionVisualState();
                }
            }
        }

        private async Task<SubscriptionLoadResult> TryLoadChannelSubscriptionFromDataApiAsync(string channelId, string accessToken)
        {
            if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(accessToken))
            {
                return null;
            }

            try
            {
                var url = "https://www.googleapis.com/youtube/v3/subscriptions?part=id&mine=true&forChannelId=" + Uri.EscapeDataString(channelId);
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    request.Headers.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");

                    var response = await _httpClient.SendAsync(request);
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine("[Channel] Data API subscription check failed: " + (int)response.StatusCode + " " + response.ReasonPhrase + " " + json);
                        return null;
                    }

                    var root = JsonValue.Parse(json).GetObject();
                    var items = GetArray(root, "items");
                    var subscribed = items != null && items.Count > 0;
                    return new SubscriptionLoadResult
                    {
                        Found = true,
                        State = subscribed ? ChannelSubscriptionState.Subscribed : ChannelSubscriptionState.NotSubscribed,
                        NotificationState = ChannelNotificationState.Default,
                        ChannelId = channelId,
                        Source = "YouTube Data API subscriptions/list"
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Channel] Data API subscription check error: " + ex.Message);
                return null;
            }
        }

        private async Task<SubscriptionLoadResult> TryLoadChannelSubscriptionFromAuthenticatedBrowseAsync(string channelId, string accessToken)
        {
            var tv = await TryLoadChannelSubscriptionFromBrowseClientAsync(channelId, accessToken, false, "authenticated /browse TVHTML5");
            if (tv != null && tv.Found)
            {
                return tv;
            }

            return await TryLoadChannelSubscriptionFromBrowseClientAsync(channelId, accessToken, true, "authenticated /browse MWEB");
        }

        private async Task<SubscriptionLoadResult> TryLoadChannelSubscriptionFromBrowseClientAsync(string channelId, string accessToken, bool mobileWebClient, string sourceName)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, BuildInnertubeUrl("browse")))
                {
                    request.Content = new StringContent(
                        BuildAuthenticatedBrowsePayload(channelId, mobileWebClient),
                        Encoding.UTF8,
                        "application/json"
                    );

                    if (mobileWebClient)
                    {
                        AddInnertubeAuthHeadersForClient(request, accessToken, MwebClientHeaderName, MwebClientVersion, MwebUserAgent);
                    }
                    else
                    {
                        AddInnertubeAuthHeadersForClient(request, accessToken, TvClientHeaderName, TvClientVersion, TvUserAgent);
                    }

                    var response = await _httpClient.SendAsync(request);
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine("[Channel] " + sourceName + " failed: " + (int)response.StatusCode + " " + response.ReasonPhrase + " " + json);
                        return null;
                    }

                    var root = JsonValue.Parse(json).GetObject();
                    return ExtractSubscriptionStateFromBrowse(root, sourceName, channelId);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Channel] " + sourceName + " error: " + ex.Message);
                return null;
            }
        }

        private void ApplyLoadedSubscriptionState(SubscriptionLoadResult result)
        {
            if (result == null)
            {
                return;
            }

            ApplySubscriptionEndpointData(result);
            if (!string.IsNullOrWhiteSpace(result.ChannelId))
            {
                _currentChannelId = result.ChannelId;
            }

            _currentSubscriptionState = result.State == ChannelSubscriptionState.Unknown
                ? ChannelSubscriptionState.NotSubscribed
                : result.State;
            _currentNotificationState = result.NotificationState == ChannelNotificationState.Unknown
                ? ChannelNotificationState.Default
                : result.NotificationState;

            UpdateSubscriptionVisualState();
            UpdateSubscriptionMenuVisualState();
        }

        private void ApplySubscriptionEndpointData(SubscriptionLoadResult result)
        {
            if (result == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.SubscribeParams))
            {
                _subscribeParams = result.SubscribeParams;
            }
            if (!string.IsNullOrWhiteSpace(result.UnsubscribeParams))
            {
                _unsubscribeParams = result.UnsubscribeParams;
            }
            if (!string.IsNullOrWhiteSpace(result.SubscribeClickTrackingParams))
            {
                _subscribeClickTrackingParams = result.SubscribeClickTrackingParams;
            }
            if (!string.IsNullOrWhiteSpace(result.UnsubscribeClickTrackingParams))
            {
                _unsubscribeClickTrackingParams = result.UnsubscribeClickTrackingParams;
            }
        }

        private void ResetSubscriptionUi()
        {
            _subscriptionStateGeneration++;
            _currentSubscriptionState = ChannelSubscriptionState.Unknown;
            _currentNotificationState = ChannelNotificationState.Default;
            _subscriptionRequestInProgress = false;
            _subscribeParams = string.Empty;
            _unsubscribeParams = string.Empty;
            _subscribeClickTrackingParams = string.Empty;
            _unsubscribeClickTrackingParams = string.Empty;
            _isSubscriptionMenuOpen = false;
            if (SubscriptionMenuBottomSheetPanel != null)
            {
                SubscriptionMenuBottomSheetPanel.Visibility = Visibility.Collapsed;
            }
            if (SubscriptionMenuBottomSheetTransform != null)
            {
                SubscriptionMenuBottomSheetTransform.Y = 280;
            }
            UpdateSubscriptionVisualState();
        }

        private void UpdateSubscriptionVisualState()
        {
            var isSubscribed = _currentSubscriptionState == ChannelSubscriptionState.Subscribed;

            if (SubscribeButton != null)
            {
                SubscribeButton.IsEnabled = !string.IsNullOrWhiteSpace(_currentChannelId);
                SubscribeButton.Background = new SolidColorBrush(Windows.UI.Colors.Transparent);
                SubscribeButton.Opacity = 1.0;
                SubscribeButton.Padding = isSubscribed ? new Thickness(0) : new Thickness(0);
            }

            if (SubscribeButtonContainer != null)
            {
                var color = isSubscribed
                    ? Windows.UI.Color.FromArgb(255, 39, 39, 39)
                    : Windows.UI.Color.FromArgb(255, 241, 241, 241);
                SubscribeButtonContainer.Background = new SolidColorBrush(color);
                SubscribeButtonContainer.BorderBrush = new SolidColorBrush(color);
            }

            if (SubscribeButtonText != null)
            {
                SubscribeButtonText.Text = "Subscribe";
                SubscribeButtonText.Visibility = isSubscribed ? Visibility.Collapsed : Visibility.Visible;
                SubscribeButtonText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 15, 15, 15));
            }

            if (SubscribeSubscribedIconsPanel != null)
            {
                SubscribeSubscribedIconsPanel.Visibility = isSubscribed ? Visibility.Visible : Visibility.Collapsed;
                SubscribeSubscribedIconsPanel.HorizontalAlignment = HorizontalAlignment.Center;
            }

            if (SubscribeNotificationIcon != null)
            {
                SubscribeNotificationIcon.Visibility = Visibility.Visible;
                SubscribeNotificationIcon.Width = 22;
                SubscribeNotificationIcon.Height = 22;
                SubscribeNotificationIcon.Stretch = Stretch.Uniform;
                SetImageSource(SubscribeNotificationIcon, GetNotificationIconAssetPath(_currentNotificationState));
            }

            if (SubscribeDownArrowIcon != null)
            {
                SubscribeDownArrowIcon.Visibility = Visibility.Visible;
                SubscribeDownArrowIcon.Width = 16;
                SubscribeDownArrowIcon.Height = 16;
                SubscribeDownArrowIcon.Margin = new Thickness(6, 0, 10, 0);
                SubscribeDownArrowIcon.Stretch = Stretch.Uniform;
                SetImageSource(SubscribeDownArrowIcon, "Assets/down_arrow.png");
            }

            UpdateSubscriptionMenuVisualState();
        }

        private void UpdateSubscriptionMenuVisualState()
        {
            var state = _currentNotificationState == ChannelNotificationState.Unknown
                ? ChannelNotificationState.Default
                : _currentNotificationState;

            if (NotificationAllCheckmark != null)
            {
                NotificationAllCheckmark.Visibility = state == ChannelNotificationState.All ? Visibility.Visible : Visibility.Collapsed;
            }
            if (NotificationPersonalizedCheckmark != null)
            {
                NotificationPersonalizedCheckmark.Visibility = state == ChannelNotificationState.Default ? Visibility.Visible : Visibility.Collapsed;
            }
            if (NotificationNoneCheckmark != null)
            {
                NotificationNoneCheckmark.Visibility = state == ChannelNotificationState.None ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static string GetNotificationIconAssetPath(ChannelNotificationState state)
        {
            if (state == ChannelNotificationState.All)
            {
                return "Assets/all_notifications.png";
            }
            if (state == ChannelNotificationState.None)
            {
                return "Assets/none_notifications.png";
            }
            return "Assets/notifications.png";
        }

        private void ShowSubscriptionMenuBottomSheet()
        {
            if (_currentSubscriptionState != ChannelSubscriptionState.Subscribed)
            {
                return;
            }

            _isSubscriptionMenuOpen = true;
            UpdateSubscriptionMenuVisualState();
            OverlayGrid.Visibility = Visibility.Visible;
            SubscriptionMenuBottomSheetPanel.Visibility = Visibility.Visible;
            AnimateSubscriptionMenuBottomSheet(true);
        }

        private void AnimateSubscriptionMenuBottomSheet(bool show)
        {
            _isSubscriptionMenuOpen = show;
            if (SubscriptionMenuBottomSheetPanel == null || SubscriptionMenuBottomSheetTransform == null)
            {
                return;
            }

            if (show)
            {
                OverlayGrid.Visibility = Visibility.Visible;
                SubscriptionMenuBottomSheetPanel.Visibility = Visibility.Visible;
            }

            var storyboard = new Storyboard();
            var animation = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            if (show)
            {
                animation.From = 280;
                animation.To = 0;
            }
            else
            {
                animation.From = SubscriptionMenuBottomSheetTransform.Y;
                animation.To = 280;
                storyboard.Completed += (s, e) =>
                {
                    SubscriptionMenuBottomSheetPanel.Visibility = Visibility.Collapsed;
                    if (!_isDescriptionBottomSheetOpen)
                    {
                        OverlayGrid.Visibility = Visibility.Collapsed;
                    }
                };
            }

            Storyboard.SetTarget(animation, SubscriptionMenuBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private void SubscriptionMenuDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateSubscriptionMenuBottomSheet(false);
        }

        private void SubscriptionMenuDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element != null && element.CapturePointer(e.Pointer))
            {
                _subscriptionMenuInitialY = e.GetCurrentPoint(element).Position.Y;
                _subscriptionMenuInitialTransformY = SubscriptionMenuBottomSheetTransform.Y;
                _subscriptionMenuIsDragging = true;
                e.Handled = true;
            }
        }

        private void SubscriptionMenuDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_subscriptionMenuIsDragging)
            {
                return;
            }

            var element = sender as UIElement;
            if (element == null)
            {
                return;
            }

            var currentY = e.GetCurrentPoint(element).Position.Y;
            var deltaY = currentY - _subscriptionMenuInitialY;
            var newY = Math.Max(0, _subscriptionMenuInitialTransformY + deltaY);
            if (newY <= 280)
            {
                SubscriptionMenuBottomSheetTransform.Y = newY;
            }
            e.Handled = true;
        }

        private void SubscriptionMenuDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_subscriptionMenuIsDragging)
            {
                return;
            }

            _subscriptionMenuIsDragging = false;
            var element = sender as UIElement;
            if (element != null)
            {
                element.ReleasePointerCapture(e.Pointer);
            }

            if (SubscriptionMenuBottomSheetTransform.Y > 140)
            {
                AnimateSubscriptionMenuBottomSheet(false);
            }
            else
            {
                AnimateSubscriptionMenuBottomSheet(true);
            }

            e.Handled = true;
        }

        private static string BuildInnertubeUrl(string endpoint)
        {
            return "https://www.youtube.com/youtubei/v1/" + endpoint + "?key=" + InnertubeApiKey;
        }

        private static string BuildAuthenticatedBrowsePayload(string channelId, bool mobileWebClient)
        {
            var payload = new JsonObject();
            payload["context"] = mobileWebClient ? BuildMwebContext() : BuildTvContext();
            payload["browseId"] = JsonValue.CreateStringValue(channelId);
            return payload.Stringify();
        }

        private static JsonObject BuildTvContext()
        {
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(TvClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(TvClientVersion);
            client["hl"] = JsonValue.CreateStringValue("ru");
            client["gl"] = JsonValue.CreateStringValue("RU");
            client["platform"] = JsonValue.CreateStringValue("TV");
            client["clientFormFactor"] = JsonValue.CreateStringValue("UNKNOWN_FORM_FACTOR");

            var user = new JsonObject();
            user["enableSafetyMode"] = JsonValue.CreateBooleanValue(false);

            var request = new JsonObject();
            request["internalExperimentFlags"] = new JsonArray();
            request["consistencyTokenJars"] = new JsonArray();

            var context = new JsonObject();
            context["client"] = client;
            context["user"] = user;
            context["request"] = request;
            return context;
        }

        private static JsonObject BuildMwebContext()
        {
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(MwebClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(MwebClientVersion);
            client["hl"] = JsonValue.CreateStringValue("ru");
            client["gl"] = JsonValue.CreateStringValue("RU");

            var context = new JsonObject();
            context["client"] = client;
            return context;
        }

        private static string BuildSubscriptionPayload(string channelId, string parameters, string clickTrackingParams)
        {
            var payload = new JsonObject();
            payload["context"] = BuildTvContext();

            var channelIds = new JsonArray();
            channelIds.Add(JsonValue.CreateStringValue(channelId));
            payload["channelIds"] = channelIds;

            if (!string.IsNullOrWhiteSpace(parameters))
            {
                payload["params"] = JsonValue.CreateStringValue(parameters);
            }

            if (!string.IsNullOrWhiteSpace(clickTrackingParams))
            {
                var clickTracking = new JsonObject();
                clickTracking["clickTrackingParams"] = JsonValue.CreateStringValue(clickTrackingParams);
                payload["context"].GetObject()["clickTracking"] = clickTracking;
            }

            return payload.Stringify();
        }

        private static string BuildNotificationPreferencePayload(string parameters)
        {
            var payload = new JsonObject();
            payload["context"] = BuildTvContext();
            if (!string.IsNullOrWhiteSpace(parameters))
            {
                payload["params"] = JsonValue.CreateStringValue(parameters);
            }
            return payload.Stringify();
        }

        private static string BuildNotificationPreferenceParams(string channelId, ChannelNotificationState targetState)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                return string.Empty;
            }

            byte stateCode = 1;
            if (targetState == ChannelNotificationState.All)
            {
                stateCode = 2;
            }
            else if (targetState == ChannelNotificationState.None)
            {
                stateCode = 3;
            }

            var channelBytes = Encoding.UTF8.GetBytes(channelId);
            var bytes = new List<byte>();
            bytes.Add(0x0A);
            bytes.Add((byte)channelBytes.Length);
            bytes.AddRange(channelBytes);
            bytes.Add(0x12);
            bytes.Add(0x02);
            bytes.Add(0x08);
            bytes.Add(stateCode);
            bytes.Add(0x18);
            bytes.Add(0x00);
            bytes.Add(0x20);
            bytes.Add(0x04);

            return Uri.EscapeDataString(Convert.ToBase64String(bytes.ToArray()));
        }

        private static void AddYouTubeAuthHeaders(HttpRequestMessage request, string accessToken)
        {
            AddInnertubeAuthHeadersForClient(request, accessToken, TvClientHeaderName, TvClientVersion, TvUserAgent);
        }

        private static void AddInnertubeAuthHeadersForClient(HttpRequestMessage request, string accessToken, string clientNameHeader, string clientVersion, string userAgent)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", clientNameHeader);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
            request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
            request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
        }

        private static async Task<string> GetTvAccessTokenAsync(bool showErrors)
        {
            global::Config.LoadUserToken();
            var refreshToken = global::Config.UserToken;

            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                if (showErrors)
                {
                    await ShowStaticMessageAsync("Sign in required", "TV refresh token was not found. Sign in again so yt_refresh_token is saved, then retry.");
                }
                return string.Empty;
            }

            var accessToken = await global::Config.RefreshAccessTokenAsync(refreshToken);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                if (showErrors)
                {
                    await ShowStaticMessageAsync("Sign in required", "Could not exchange TV refresh token for access token.");
                }
                return string.Empty;
            }

            return accessToken;
        }

        private async Task ShowMessageAsync(string title, string message)
        {
            await ShowStaticMessageAsync(title, message);
        }

        private static async Task ShowStaticMessageAsync(string title, string message)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    PrimaryButtonText = "OK"
                };
                await dialog.ShowAsync();
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine("[Channel] " + title + ": " + message);
            }
        }

        private static void SetImageSource(Image image, string path)
        {
            if (image == null || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                image.Source = new BitmapImage(new Uri("ms-appx:///" + path.TrimStart('/')));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Channel] Icon load error: " + ex.Message);
            }
        }

        private static SubscriptionLoadResult ExtractSubscriptionStateFromBrowse(JsonObject root, string sourceName, string currentChannelId)
        {
            var result = new SubscriptionLoadResult
            {
                Found = false,
                State = ChannelSubscriptionState.Unknown,
                NotificationState = ChannelNotificationState.Unknown,
                Source = sourceName
            };

            if (root == null || string.IsNullOrWhiteSpace(currentChannelId))
            {
                return result;
            }

            // Important: authenticated /browse can contain both subscribe and unsubscribe
            // endpoint templates. The reliable signed-in state is the subscribeButtonRenderer.subscribed
            // boolean for the current channel.
            CollectSubscriptionEndpointData(root, string.Empty, result, 0, currentChannelId);

            bool foundRendererState;
            ChannelNotificationState rendererNotificationState;
            var rendererState = FindSubscribeButtonRendererState(root, currentChannelId, out foundRendererState, out rendererNotificationState);
            if (foundRendererState)
            {
                result.Found = true;
                result.State = rendererState;
                if (rendererNotificationState != ChannelNotificationState.Unknown)
                {
                    result.NotificationState = rendererNotificationState;
                }
                System.Diagnostics.Debug.WriteLine("[Channel] Subscription state from subscribeButtonRenderer.subscribed: " + rendererState + ", " + result.NotificationState);
            }

            // Fallback for WEB browse responses that expose a top-level header.
            var header = GetObject(root, "header");
            bool foundHeaderState;
            var headerState = FindVisibleSubscriptionStateInHeader(header, out foundHeaderState);
            if (result.State == ChannelSubscriptionState.Unknown && foundHeaderState)
            {
                result.Found = true;
                result.State = headerState;
                System.Diagnostics.Debug.WriteLine("[Channel] Subscription state from visible header button: " + headerState);
            }

            // Endpoints are useful for params, but they are not always a reliable state by
            // themselves. Some /browse responses contain both subscribeEndpoint and
            // unsubscribeEndpoint templates for the same channel. In that case, prefer the
            // visible header button; if it was not found, default to NotSubscribed instead of
            // incorrectly showing every channel as subscribed.
            if (result.State == ChannelSubscriptionState.Unknown)
            {
                var hasSubscribeEndpoint = !string.IsNullOrWhiteSpace(result.SubscribeParams);
                var hasUnsubscribeEndpoint = !string.IsNullOrWhiteSpace(result.UnsubscribeParams);

                if (hasUnsubscribeEndpoint && !hasSubscribeEndpoint)
                {
                    result.Found = true;
                    result.State = ChannelSubscriptionState.Subscribed;
                }
                else if (hasSubscribeEndpoint)
                {
                    result.Found = true;
                    result.State = ChannelSubscriptionState.NotSubscribed;
                    if (hasUnsubscribeEndpoint)
                    {
                        System.Diagnostics.Debug.WriteLine("[Channel] Both subscribe and unsubscribe endpoints found; using NotSubscribed because no visible subscribed header state was found.");
                    }
                }
            }

            // Notification state must not by itself mean "subscribed". /browse may include
            // notification menu templates even for channels we are not subscribed to.
            if (result.State == ChannelSubscriptionState.Subscribed)
            {
                bool foundNotification;
                var notification = FindNotificationState(header, string.Empty, 0, out foundNotification);
                if (foundNotification)
                {
                    result.NotificationState = notification;
                }
            }

            return result;
        }

        private static ChannelSubscriptionState FindSubscribeButtonRendererState(
            IJsonValue value,
            string currentChannelId,
            out bool found,
            out ChannelNotificationState notificationState
        )
        {
            return FindSubscribeButtonRendererState(value, currentChannelId, 0, out found, out notificationState);
        }

        private static ChannelSubscriptionState FindSubscribeButtonRendererState(
            IJsonValue value,
            string currentChannelId,
            int depth,
            out bool found,
            out ChannelNotificationState notificationState
        )
        {
            found = false;
            notificationState = ChannelNotificationState.Unknown;

            if (value == null || depth > 80 || string.IsNullOrWhiteSpace(currentChannelId))
            {
                return ChannelSubscriptionState.Unknown;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    if (obj.ContainsKey("subscribeButtonRenderer"))
                    {
                        var renderer = GetObject(obj, "subscribeButtonRenderer");
                        if (SubscribeButtonRendererTargetsChannel(renderer, currentChannelId))
                        {
                            ChannelSubscriptionState state;
                            if (TryReadSubscribeButtonRendererState(renderer, out state))
                            {
                                found = true;
                                notificationState = ReadNotificationStateFromSubscribeButtonRenderer(renderer);
                                return state;
                            }
                        }
                    }

                    foreach (var pair in obj)
                    {
                        bool nestedFound;
                        ChannelNotificationState nestedNotificationState;
                        var nested = FindSubscribeButtonRendererState(pair.Value, currentChannelId, depth + 1, out nestedFound, out nestedNotificationState);
                        if (nestedFound)
                        {
                            found = true;
                            notificationState = nestedNotificationState;
                            return nested;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        bool nestedFound;
                        ChannelNotificationState nestedNotificationState;
                        var nested = FindSubscribeButtonRendererState(array[(int)i], currentChannelId, depth + 1, out nestedFound, out nestedNotificationState);
                        if (nestedFound)
                        {
                            found = true;
                            notificationState = nestedNotificationState;
                            return nested;
                        }
                    }
                }
            }
            catch
            {
            }

            return ChannelSubscriptionState.Unknown;
        }

        private static bool SubscribeButtonRendererTargetsChannel(JsonObject renderer, string currentChannelId)
        {
            if (renderer == null || string.IsNullOrWhiteSpace(currentChannelId))
            {
                return false;
            }

            var rendererChannelId = GetString(renderer, "channelId");
            if (string.Equals(rendererChannelId, currentChannelId, StringComparison.Ordinal))
            {
                return true;
            }

            return JsonValueContainsExactString(renderer, currentChannelId, 0);
        }

        private static bool JsonValueContainsExactString(IJsonValue value, string expected, int depth)
        {
            if (value == null || string.IsNullOrWhiteSpace(expected) || depth > 40)
            {
                return false;
            }

            try
            {
                if (value.ValueType == JsonValueType.String)
                {
                    return string.Equals(value.GetString(), expected, StringComparison.Ordinal);
                }

                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    foreach (var pair in obj)
                    {
                        if (JsonValueContainsExactString(pair.Value, expected, depth + 1))
                        {
                            return true;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        if (JsonValueContainsExactString(array[(int)i], expected, depth + 1))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryReadSubscribeButtonRendererState(JsonObject renderer, out ChannelSubscriptionState state)
        {
            state = ChannelSubscriptionState.Unknown;
            if (renderer == null)
            {
                return false;
            }

            bool subscribed;
            if (TryGetBoolean(renderer, "subscribed", out subscribed))
            {
                state = subscribed ? ChannelSubscriptionState.Subscribed : ChannelSubscriptionState.NotSubscribed;
                return true;
            }

            var buttonText = ExtractText(GetObject(renderer, "buttonText"));
            if (TryParseVisibleSubscriptionButtonText(buttonText, out state))
            {
                return true;
            }

            return false;
        }

        private static ChannelNotificationState ReadNotificationStateFromSubscribeButtonRenderer(JsonObject renderer)
        {
            var toggle = GetObjectPath(renderer, "notificationPreferenceButton", "subscriptionNotificationToggleButtonRenderer");
            if (toggle == null)
            {
                return ChannelNotificationState.Unknown;
            }

            int currentStateId;
            if (TryGetInt(toggle, "currentStateId", out currentStateId))
            {
                var states = GetArray(toggle, "states");
                if (states != null)
                {
                    for (uint i = 0; i < states.Count; i++)
                    {
                        if (states[(int)i].ValueType != JsonValueType.Object)
                        {
                            continue;
                        }

                        var stateObject = states[(int)i].GetObject();
                        int stateId;
                        if (TryGetInt(stateObject, "stateId", out stateId) && stateId == currentStateId)
                        {
                            ChannelNotificationState parsed;
                            if (TryParseNotificationStateText(GetString(stateObject, "notificationState"), out parsed))
                            {
                                return parsed;
                            }
                        }
                    }
                }
            }

            ChannelNotificationState directParsed;
            if (TryParseNotificationStateText(GetString(toggle, "notificationState"), out directParsed))
            {
                return directParsed;
            }

            return ChannelNotificationState.Unknown;
        }

        private static ChannelSubscriptionState FindVisibleSubscriptionStateInHeader(IJsonValue value, out bool found)
        {
            return FindVisibleSubscriptionStateInHeader(value, string.Empty, 0, out found);
        }

        private static ChannelSubscriptionState FindVisibleSubscriptionStateInHeader(IJsonValue value, string path, int depth, out bool found)
        {
            found = false;
            if (value == null || depth > 40)
            {
                return ChannelSubscriptionState.Unknown;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    var lowerPath = path.ToLowerInvariant();

                    if (!IsHiddenSubscriptionTemplatePath(lowerPath))
                    {
                        bool boolValue;
                        if (TryGetBoolean(obj, "subscribed", out boolValue)
                            || TryGetBoolean(obj, "isSubscribed", out boolValue)
                            || TryGetBoolean(obj, "is_subscribed", out boolValue))
                        {
                            found = true;
                            return boolValue ? ChannelSubscriptionState.Subscribed : ChannelSubscriptionState.NotSubscribed;
                        }

                        var visibleText = CollectVisibleButtonStateText(obj);
                        ChannelSubscriptionState parsed;
                        if (TryParseVisibleSubscriptionButtonText(visibleText, out parsed))
                        {
                            found = true;
                            return parsed;
                        }
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var lowerKey = key.ToLowerInvariant();
                        var nextPath = string.IsNullOrEmpty(path) ? lowerKey : path + "." + lowerKey;

                        if (IsHiddenSubscriptionTemplatePath(nextPath))
                        {
                            continue;
                        }

                        bool nestedFound;
                        var nested = FindVisibleSubscriptionStateInHeader(pair.Value, nextPath, depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        bool nestedFound;
                        var nested = FindVisibleSubscriptionStateInHeader(array[(int)i], path + "[]", depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
            }
            catch
            {
            }

            return ChannelSubscriptionState.Unknown;
        }

        private static bool IsHiddenSubscriptionTemplatePath(string lowerPath)
        {
            var p = lowerPath ?? string.Empty;
            return p.Contains("menu")
                || p.Contains("dialog")
                || p.Contains("confirmation")
                || p.Contains("modal")
                || p.Contains("sheet")
                || p.Contains("popup")
                || p.Contains("notification")
                || p.Contains("subscribeendpoint")
                || p.Contains("unsubscribeendpoint")
                || p.Contains("command")
                || p.Contains("endpoint")
                || p.Contains("tracking")
                || p.Contains("logging");
        }

        private static string CollectVisibleButtonStateText(JsonObject obj)
        {
            if (obj == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            AppendVisibleStateText(sb, obj, "title");
            AppendVisibleStateText(sb, obj, "text");
            AppendVisibleStateText(sb, obj, "label");
            AppendVisibleStateText(sb, obj, "accessibilityText");
            AppendVisibleStateText(sb, obj, "ariaLabel");
            AppendVisibleStateText(sb, obj, "accessibilityLabel");

            var accessibility = GetObject(obj, "accessibility");
            if (accessibility != null)
            {
                AppendVisibleStateText(sb, accessibility, "label");
                var data = GetObject(accessibility, "accessibilityData");
                AppendVisibleStateText(sb, data, "label");
            }

            return sb.ToString();
        }

        private static void AppendVisibleStateText(StringBuilder sb, JsonObject obj, string key)
        {
            if (sb == null || obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
            {
                return;
            }

            var value = obj[key];
            if (value == null)
            {
                return;
            }

            if (value.ValueType == JsonValueType.String)
            {
                sb.Append(' ');
                sb.Append(value.GetString());
            }
            else if (value.ValueType == JsonValueType.Object)
            {
                var text = ExtractText(value.GetObject());
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.Append(' ');
                    sb.Append(text);
                }
            }
        }

        private static bool TryParseVisibleSubscriptionButtonText(string text, out ChannelSubscriptionState state)
        {
            state = ChannelSubscriptionState.Unknown;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var upper = text.Trim().ToUpperInvariant();
            var compact = Compact(upper);

            if (upper.Contains("SUBSCRIBERS") || upper.Contains("SUBSCRIBER") || upper.Contains("ПОДПИСЧИК"))
            {
                return false;
            }

            if (compact == "SUBSCRIBED"
                || compact.Contains("SUBSCRIBED")
                || upper.Contains("ВЫ ПОДПИСАН")
                || upper.Contains("ПОДПИСАНЫ"))
            {
                state = ChannelSubscriptionState.Subscribed;
                return true;
            }

            if (compact == "SUBSCRIBE"
                || compact.Contains("SUBSCRIBE")
                || upper.Contains("ПОДПИСАТЬСЯ"))
            {
                state = ChannelSubscriptionState.NotSubscribed;
                return true;
            }

            return false;
        }

        private static void CollectSubscriptionEndpointData(IJsonValue value, string path, SubscriptionLoadResult result, int depth, string currentChannelId)
        {
            if (value == null || result == null || depth > 80)
            {
                return;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var lower = key.ToLowerInvariant();
                        var nextPath = string.IsNullOrEmpty(path) ? lower : path + "." + lower;

                        if (string.Equals(key, "subscribeEndpoint", StringComparison.OrdinalIgnoreCase))
                        {
                            var endpoint = pair.Value != null && pair.Value.ValueType == JsonValueType.Object ? pair.Value.GetObject() : null;
                            if (EndpointTargetsChannel(endpoint, obj, currentChannelId))
                            {
                                result.SubscribeParams = FirstNonEmpty(result.SubscribeParams, GetString(endpoint, "params"), GetString(obj, "params"));
                                result.SubscribeClickTrackingParams = FirstNonEmpty(result.SubscribeClickTrackingParams, GetString(endpoint, "clickTrackingParams"), GetString(obj, "clickTrackingParams"));
                                result.ChannelId = FirstNonEmpty(result.ChannelId, currentChannelId, ExtractChannelIdFromEndpoint(endpoint));
                            }
                        }
                        else if (string.Equals(key, "unsubscribeEndpoint", StringComparison.OrdinalIgnoreCase))
                        {
                            var endpoint = pair.Value != null && pair.Value.ValueType == JsonValueType.Object ? pair.Value.GetObject() : null;
                            if (EndpointTargetsChannel(endpoint, obj, currentChannelId))
                            {
                                result.UnsubscribeParams = FirstNonEmpty(result.UnsubscribeParams, GetString(endpoint, "params"), GetString(obj, "params"));
                                result.UnsubscribeClickTrackingParams = FirstNonEmpty(result.UnsubscribeClickTrackingParams, GetString(endpoint, "clickTrackingParams"), GetString(obj, "clickTrackingParams"));
                                result.ChannelId = FirstNonEmpty(result.ChannelId, currentChannelId, ExtractChannelIdFromEndpoint(endpoint));
                            }
                        }

                        CollectSubscriptionEndpointData(pair.Value, nextPath, result, depth + 1, currentChannelId);
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        CollectSubscriptionEndpointData(array[(int)i], path + "[]", result, depth + 1, currentChannelId);
                    }
                }
            }
            catch
            {
            }
        }

        private static bool EndpointTargetsChannel(JsonObject endpoint, JsonObject container, string currentChannelId)
        {
            if (endpoint == null || string.IsNullOrWhiteSpace(currentChannelId))
            {
                return false;
            }

            var endpointChannelId = FirstNonEmpty(
                ExtractChannelIdFromEndpoint(endpoint),
                ExtractChannelIdFromEndpoint(container),
                ExtractChannelIdFromEndpoint(GetObject(container, "commandMetadata")),
                ExtractChannelIdFromEndpoint(GetObject(container, "navigationEndpoint"))
            );

            if (string.IsNullOrWhiteSpace(endpointChannelId))
            {
                return false;
            }

            return string.Equals(endpointChannelId, currentChannelId, StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractChannelIdFromEndpoint(JsonObject endpoint)
        {
            if (endpoint == null)
            {
                return string.Empty;
            }

            var ids = GetArray(endpoint, "channelIds");
            if (ids != null && ids.Count > 0)
            {
                var first = ids[(int)0];
                if (first != null && first.ValueType == JsonValueType.String)
                {
                    return first.GetString();
                }
            }

            var direct = FirstNonEmpty(GetString(endpoint, "channelId"), GetString(endpoint, "browseId"), GetString(endpoint, "targetId"));
            if (!string.IsNullOrWhiteSpace(direct) && direct.StartsWith("UC", StringComparison.OrdinalIgnoreCase))
            {
                return direct;
            }

            var browseEndpoint = GetObject(endpoint, "browseEndpoint");
            if (browseEndpoint != null)
            {
                var browseId = FirstNonEmpty(GetString(browseEndpoint, "browseId"), GetString(browseEndpoint, "canonicalBaseUrl"));
                if (!string.IsNullOrWhiteSpace(browseId) && browseId.StartsWith("UC", StringComparison.OrdinalIgnoreCase))
                {
                    return browseId;
                }
            }

            var subscribeEndpoint = GetObject(endpoint, "subscribeEndpoint");
            if (subscribeEndpoint != null)
            {
                var subscribeChannelId = ExtractChannelIdFromEndpoint(subscribeEndpoint);
                if (!string.IsNullOrWhiteSpace(subscribeChannelId))
                {
                    return subscribeChannelId;
                }
            }

            var unsubscribeEndpoint = GetObject(endpoint, "unsubscribeEndpoint");
            if (unsubscribeEndpoint != null)
            {
                var unsubscribeChannelId = ExtractChannelIdFromEndpoint(unsubscribeEndpoint);
                if (!string.IsNullOrWhiteSpace(unsubscribeChannelId))
                {
                    return unsubscribeChannelId;
                }
            }

            return string.Empty;
        }

        private static ChannelSubscriptionState FindSubscriptionState(IJsonValue value, string path, int depth, out bool found)
        {
            found = false;
            if (value == null || depth > 80)
            {
                return ChannelSubscriptionState.Unknown;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    var lowerPath = path.ToLowerInvariant();

                    bool boolValue;
                    if (TryGetBoolean(obj, "subscribed", out boolValue) || TryGetBoolean(obj, "isSubscribed", out boolValue) || TryGetBoolean(obj, "is_subscribed", out boolValue))
                    {
                        found = true;
                        return boolValue ? ChannelSubscriptionState.Subscribed : ChannelSubscriptionState.NotSubscribed;
                    }

                    if (IsSubscriptionRelatedPath(lowerPath) || HasSubscriptionRelatedKey(obj))
                    {
                        var text = CollectObjectText(obj, 0, 3);
                        ChannelSubscriptionState parsed;
                        if (TryParseSubscriptionStateText(text, out parsed))
                        {
                            found = true;
                            return parsed;
                        }
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var lowerKey = key.ToLowerInvariant();
                        var nextPath = string.IsNullOrEmpty(path) ? lowerKey : path + "." + lowerKey;

                        if (pair.Value != null && pair.Value.ValueType == JsonValueType.String && IsSubscriptionRelatedPath(nextPath))
                        {
                            ChannelSubscriptionState parsed;
                            if (TryParseSubscriptionStateText(pair.Value.GetString(), out parsed))
                            {
                                found = true;
                                return parsed;
                            }
                        }

                        bool nestedFound;
                        var nested = FindSubscriptionState(pair.Value, nextPath, depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        bool nestedFound;
                        var nested = FindSubscriptionState(array[(int)i], path + "[]", depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
            }
            catch
            {
            }

            return ChannelSubscriptionState.Unknown;
        }

        private static ChannelNotificationState FindNotificationState(IJsonValue value, string path, int depth, out bool found)
        {
            found = false;
            if (value == null || depth > 80)
            {
                return ChannelNotificationState.Unknown;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    var lowerPath = path.ToLowerInvariant();

                    if (ObjectHasSelectedTrue(obj) && IsNotificationRelatedPath(lowerPath))
                    {
                        var text = CollectObjectText(obj, 0, 4);
                        ChannelNotificationState parsed;
                        if (TryParseNotificationStateText(text, out parsed))
                        {
                            found = true;
                            return parsed;
                        }
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var lowerKey = key.ToLowerInvariant();
                        var compactKey = Compact(lowerKey);
                        var nextPath = string.IsNullOrEmpty(path) ? lowerKey : path + "." + lowerKey;

                        if (pair.Value != null
                            && pair.Value.ValueType == JsonValueType.String
                            && (IsCurrentNotificationStateKey(compactKey, nextPath) || (IsNotificationRelatedPath(nextPath) && IsPreferredNotificationTextKey(compactKey))))
                        {
                            ChannelNotificationState parsed;
                            if (TryParseNotificationStateText(pair.Value.GetString(), out parsed))
                            {
                                found = true;
                                return parsed;
                            }
                        }

                        bool nestedFound;
                        var nested = FindNotificationState(pair.Value, nextPath, depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        bool nestedFound;
                        var nested = FindNotificationState(array[(int)i], path + "[]", depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
            }
            catch
            {
            }

            return ChannelNotificationState.Unknown;
        }

        private static bool IsSubscriptionRelatedPath(string path)
        {
            var p = path ?? string.Empty;
            return p.Contains("subscribe") || p.Contains("subscription") || p.Contains("subscribed") || p.Contains("owner") || p.Contains("header") || p.Contains("actions");
        }

        private static bool HasSubscriptionRelatedKey(JsonObject obj)
        {
            if (obj == null)
            {
                return false;
            }

            foreach (var pair in obj)
            {
                var key = (pair.Key ?? string.Empty).ToLowerInvariant();
                if (key.Contains("subscribe") || key.Contains("subscription"))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseSubscriptionStateText(string text, out ChannelSubscriptionState state)
        {
            state = ChannelSubscriptionState.Unknown;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var upper = text.Trim().ToUpperInvariant();
            var compact = Compact(upper);

            if (upper.Contains("SUBSCRIBERS") || upper.Contains("SUBSCRIBER") || upper.Contains("ПОДПИСЧИК"))
            {
                return false;
            }

            if (compact.Contains("UNSUBSCRIBE") || compact.Contains("SUBSCRIBED") || upper.Contains("ОТПИСАТЬ") || upper.Contains("ВЫПОДПИСАН") || upper.Contains("ПОДПИСАН"))
            {
                state = ChannelSubscriptionState.Subscribed;
                return true;
            }

            if (compact == "SUBSCRIBE" || compact.Contains("SUBSCRIBE") || upper.Contains("ПОДПИСАТЬСЯ"))
            {
                state = ChannelSubscriptionState.NotSubscribed;
                return true;
            }

            return false;
        }

        private static bool IsNotificationRelatedPath(string path)
        {
            var p = path ?? string.Empty;
            return p.Contains("notification") || p.Contains("bell") || p.Contains("preference");
        }

        private static bool IsCurrentNotificationStateKey(string compactKey, string path)
        {
            var key = compactKey ?? string.Empty;
            var p = path ?? string.Empty;
            return key == "currentnotificationstate"
                || key == "currentnotificationpreference"
                || key == "currentstate"
                || key == "subscriptionnotificationpreference"
                || (p.Contains("current") && p.Contains("notification"));
        }

        private static bool IsPreferredNotificationTextKey(string compactKey)
        {
            var key = compactKey ?? string.Empty;
            return key == "title" || key == "text" || key == "label" || key == "accessibility" || key == "accessibilitytext" || key == "arialabel" || key == "icontype";
        }

        private static bool TryParseNotificationStateText(string text, out ChannelNotificationState state)
        {
            state = ChannelNotificationState.Unknown;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var upper = text.Trim().ToUpperInvariant();
            var compact = Compact(upper);

            if (compact.Contains("PERSONALIZED") || compact.Contains("PERSONALISED") || compact.Contains("DEFAULT") || compact.Contains("OCCASIONAL") || upper.Contains("НА ОСНОВЕ") || upper.Contains("ПРЕДПОЧТ") || upper.Contains("ПЕРСОНАЛ"))
            {
                state = ChannelNotificationState.Default;
                return true;
            }

            if (compact == "ALL" || compact.Contains("ALLNOTIFICATION") || compact.Contains("NOTIFICATIONSALL") || compact.Contains("RINGING") || upper.Contains("ВСЕ УВЕДОМ") || upper.Contains("ВСЕ ОПОВЕЩ"))
            {
                state = ChannelNotificationState.All;
                return true;
            }

            if (compact == "NONE" || compact.Contains("NONOTIFICATION") || compact.Contains("NOTIFICATIONNONE") || compact.Contains("NOTIFICATIONSOFF") || compact.Contains("MUTED") || upper.Contains("НИКАК") || upper.Contains("БЕЗ УВЕДОМ") || upper.Contains("НЕТ УВЕДОМ"))
            {
                state = ChannelNotificationState.None;
                return true;
            }

            if (compact == "NOTIFICATIONS" || compact == "NOTIFICATION")
            {
                state = ChannelNotificationState.Default;
                return true;
            }

            return false;
        }

        private static bool TryGetBoolean(JsonObject obj, string key, out bool value)
        {
            value = false;
            if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
            {
                return false;
            }

            var jsonValue = obj[key];
            if (jsonValue != null && jsonValue.ValueType == JsonValueType.Boolean)
            {
                value = jsonValue.GetBoolean();
                return true;
            }

            return false;
        }


        private static bool TryGetInt(JsonObject obj, string key, out int value)
        {
            value = 0;
            if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
            {
                return false;
            }

            var jsonValue = obj[key];
            if (jsonValue != null && jsonValue.ValueType == JsonValueType.Number)
            {
                value = (int)jsonValue.GetNumber();
                return true;
            }

            return false;
        }

        private static bool ObjectHasSelectedTrue(JsonObject obj)
        {
            bool value;
            return TryGetBoolean(obj, "selected", out value) && value
                || TryGetBoolean(obj, "isSelected", out value) && value
                || TryGetBoolean(obj, "checked", out value) && value
                || TryGetBoolean(obj, "isChecked", out value) && value;
        }

        private static string CollectObjectText(IJsonValue value, int depth, int maxDepth)
        {
            if (value == null || depth > maxDepth)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    var extracted = ExtractText(obj);
                    if (!string.IsNullOrWhiteSpace(extracted))
                    {
                        sb.Append(extracted);
                        sb.Append(' ');
                    }

                    foreach (var pair in obj)
                    {
                        var key = Compact(pair.Key ?? string.Empty);
                        if (pair.Value != null && pair.Value.ValueType == JsonValueType.String)
                        {
                            if (IsPreferredNotificationTextKey(key) || key.Contains("subscribe") || key.Contains("label") || key.Contains("title") || key.Contains("text"))
                            {
                                sb.Append(pair.Value.GetString());
                                sb.Append(' ');
                            }
                        }
                        else if (pair.Value != null && (pair.Value.ValueType == JsonValueType.Object || pair.Value.ValueType == JsonValueType.Array))
                        {
                            var nested = CollectObjectText(pair.Value, depth + 1, maxDepth);
                            if (!string.IsNullOrWhiteSpace(nested))
                            {
                                sb.Append(nested);
                                sb.Append(' ');
                            }
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        var nested = CollectObjectText(array[(int)i], depth + 1, maxDepth);
                        if (!string.IsNullOrWhiteSpace(nested))
                        {
                            sb.Append(nested);
                            sb.Append(' ');
                        }
                    }
                }
            }
            catch
            {
            }

            return sb.ToString();
        }

        private static string Compact(string text)
        {
            return (text ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
        }

        private sealed class ChannelPageData
        {
            public ChannelPageInfo Info { get; set; }
            public List<VideoCardItem> Videos { get; set; }
            public SubscriptionLoadResult SubscriptionState { get; set; }
        }

        private sealed class SubscriptionLoadResult
        {
            public bool Found { get; set; }
            public ChannelSubscriptionState State { get; set; }
            public ChannelNotificationState NotificationState { get; set; }
            public string SubscribeParams { get; set; }
            public string UnsubscribeParams { get; set; }
            public string SubscribeClickTrackingParams { get; set; }
            public string UnsubscribeClickTrackingParams { get; set; }
            public string ChannelId { get; set; }
            public string Source { get; set; }
        }

        private sealed class ChannelPageInfo
        {
            public string ChannelId { get; set; }
            public string Title { get; set; }
            public string Handle { get; set; }
            public string Description { get; set; }
            public string ThumbnailUrl { get; set; }
            public string BannerUrl { get; set; }
            public string SubscriberCount { get; set; }
            public string VideoCount { get; set; }
        }
    }
}
