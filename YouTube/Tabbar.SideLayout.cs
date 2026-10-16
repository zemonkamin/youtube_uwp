using System;
using System.Collections.Generic;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace YouTube
{
    public sealed partial class Tabbar
    {
        private bool _sideLayoutLoaded;
        private bool _isSideLayout;
        private bool _updatingPlacement;
        private Grid _placementGrid;
        private Window _placementWindow;
        private int _bottomRowIndex;
        private GridLength _bottomRowHeight;
        private long _visibilityCallbackToken;
        private readonly Dictionary<FrameworkElement, Thickness> _bottomPadding = new Dictionary<FrameworkElement, Thickness>();
        private readonly Dictionary<FrameworkElement, long> _overlappingHeaderCallbacks = new Dictionary<FrameworkElement, long>();

        private void StartResponsivePlacement()
        {
            if (_sideLayoutLoaded) return;
            _placementGrid = Parent as Grid;
            if (_placementGrid == null || _placementGrid.ColumnDefinitions.Count != 2) return;
            _bottomRowIndex = Grid.GetRow(this);
            _bottomRowHeight = _placementGrid.RowDefinitions[_bottomRowIndex].Height;
            _bottomPadding.Clear();
            CollectBottomPadding(_placementGrid);
            WatchOverlappingHeaders();
            _sideLayoutLoaded = true;
            _placementWindow = Window.Current;
            _placementWindow.SizeChanged += PlacementWindow_SizeChanged;
            _placementWindow.Activated += PlacementWindow_Activated;
            SideTabbarController.EnabledChanged += SideTabbar_EnabledChanged;
            _visibilityCallbackToken = RegisterPropertyChangedCallback(VisibilityProperty, PlacementVisibilityChanged);
            UpdateResponsivePlacement();
        }

        private void StopResponsivePlacement()
        {
            if (!_sideLayoutLoaded) return;
            _sideLayoutLoaded = false;
            foreach (var pair in _overlappingHeaderCallbacks)
            {
                pair.Key.SizeChanged -= OverlappingHeader_SizeChanged;
                pair.Key.UnregisterPropertyChangedCallback(VisibilityProperty, pair.Value);
            }
            _overlappingHeaderCallbacks.Clear();
            SideTabbarController.EnabledChanged -= SideTabbar_EnabledChanged;
            UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
            if (_placementWindow != null)
            {
                _placementWindow.SizeChanged -= PlacementWindow_SizeChanged;
                _placementWindow.Activated -= PlacementWindow_Activated;
            }
            ApplyPlacement(false);
            _placementGrid = null;
            _placementWindow = null;
            _bottomPadding.Clear();
        }

        private void PlacementWindow_SizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            UpdateResponsivePlacement();
        }

        private void PlacementWindow_Activated(object sender, WindowActivatedEventArgs e)
        {
            UpdateResponsivePlacement();
        }

        private void SideTabbar_EnabledChanged(object sender, EventArgs e)
        {
            UpdateResponsivePlacement();
        }

        private void PlacementVisibilityChanged(DependencyObject sender, DependencyProperty property)
        {
            UpdateResponsivePlacement();
        }

        public void UpdateResponsivePlacement()
        {
            if (!_sideLayoutLoaded || _updatingPlacement) return;
            ApplyPlacement(Visibility == Visibility.Visible && SideTabbarController.ShouldUseSideLayout());
        }

        private void ApplyPlacement(bool side)
        {
            if (_placementGrid == null || _updatingPlacement) return;
            _updatingPlacement = true;
            try
            {
                _isSideLayout = side;
                _placementGrid.ColumnDefinitions[0].Width = new GridLength(side ? SideTabbarController.RailWidth : 0);
                // Video owns the visibility of its navigation chrome; portrait and fullscreen
                // must not acquire an empty 52-pixel row when the sidebar is restored.
                var videoChrome = _placementGrid.Name == "VideoRoot";
                _placementGrid.RowDefinitions[_bottomRowIndex].Height = videoChrome
                    ? new GridLength(Visibility == Visibility.Visible && !side ? 52 : 0)
                    : (side ? new GridLength(0) : _bottomRowHeight);
                Grid.SetRow(this, side ? 0 : _bottomRowIndex);
                Grid.SetRowSpan(this, side ? _placementGrid.RowDefinitions.Count : 1);
                Grid.SetColumn(this, 0);
                Grid.SetColumnSpan(this, side ? 1 : 2);
                var buttons = new[] { HomeButton, ShortsButton, SubscriptionsButton, AccountButton };
                var labels = new[] { HomeLabel, ShortsLabel, SubscriptionsLabel, AccountLabel };
                if (TabbarButtonsHost.RowDefinitions.Count == 0)
                    for (var i = 0; i < 4; i++) TabbarButtonsHost.RowDefinitions.Add(new RowDefinition());
                for (var i = 0; i < buttons.Length; i++)
                {
                    var button = buttons[i];
                    Grid.SetRow(button, side ? i : 0);
                    Grid.SetColumn(button, side ? 0 : i);
                    TabbarButtonsHost.RowDefinitions[i].Height = side
                        ? new GridLength(i == 1 && !ShortsFeatureController.IsEnabled() ? 0 : 74)
                        : new GridLength(i == 0 ? 1 : 0, i == 0 ? GridUnitType.Star : GridUnitType.Pixel);
                    labels[i].FontSize = side ? 10 : 12;
                    labels[i].Margin = side ? new Thickness(0, 6, 0, 0) : new Thickness(0);
                    labels[i].MaxWidth = side ? 64 : double.PositiveInfinity;
                    labels[i].TextTrimming = side ? TextTrimming.CharacterEllipsis : TextTrimming.None;
                }
                HomeColumn.Width = new GridLength(1, GridUnitType.Star);
                ShortsColumn.Width = new GridLength(side || !ShortsFeatureController.IsEnabled() ? 0 : 1,
                    side || !ShortsFeatureController.IsEnabled() ? GridUnitType.Pixel : GridUnitType.Star);
                SubscriptionsColumn.Width = new GridLength(side ? 0 : 1, side ? GridUnitType.Pixel : GridUnitType.Star);
                AccountColumn.Width = SubscriptionsColumn.Width;
                TabbarButtonsHost.Margin = side ? new Thickness(4, GetSidebarTopInset(), 4, 8) : new Thickness(0);
                TabbarButtonsHost.VerticalAlignment = side ? VerticalAlignment.Top : VerticalAlignment.Stretch;
                TabbarButtonsScrollViewer.VerticalScrollMode = side ? ScrollMode.Enabled : ScrollMode.Disabled;
                TabbarButtonsScrollViewer.VerticalScrollBarVisibility = side ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
                TabbarDivider.Visibility = side ? Visibility.Collapsed : Visibility.Visible;
                TabbarDividerRow.Height = new GridLength(side ? 0 : 2);
                foreach (var pair in _bottomPadding)
                {
                    var padding = pair.Value;
                    if (side) padding.Bottom = Math.Max(0, padding.Bottom - SideTabbarController.GetBottomInset(pair.Key));
                    SetElementPadding(pair.Key, padding);
                }
                UpdateCompactOrientationLayout();
                UpdateButtonHostWidth(TabbarRoot.ActualWidth);
                ApplyGlassEffect();
            }
            finally { _updatingPlacement = false; }
        }

        private void WatchOverlappingHeaders()
        {
            // Only a top-row sibling drawn over the rail needs reserved space. Full-page
            // overlays and content in column 1 must never introduce a permanent top gap.
            foreach (var child in _placementGrid.Children)
            {
                var header = child as FrameworkElement;
                if (header == null || ReferenceEquals(header, this)
                    || Grid.GetRow(header) != 0 || Grid.GetRowSpan(header) != 1
                    || Grid.GetColumn(header) != 0
                    || Canvas.GetZIndex(header) <= Canvas.GetZIndex(this))
                    continue;

                header.SizeChanged += OverlappingHeader_SizeChanged;
                _overlappingHeaderCallbacks[header] = header.RegisterPropertyChangedCallback(
                    VisibilityProperty, OverlappingHeader_VisibilityChanged);
            }
        }

        private double GetSidebarTopInset()
        {
            var inset = 0.0;
            foreach (var header in _overlappingHeaderCallbacks.Keys)
            {
                if (header.Visibility != Visibility.Visible)
                    continue;

                var rowHeight = _placementGrid.RowDefinitions[0].Height;
                // Use the current row height before the first arrange/after rotation instead
                // of an ActualHeight left over from the previous orientation.
                var height = rowHeight.GridUnitType == GridUnitType.Pixel
                    ? rowHeight.Value
                    : header.ActualHeight + header.Margin.Top + header.Margin.Bottom;
                inset = Math.Max(inset, height);
            }
            return inset;
        }

        private void OverlappingHeader_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateSidebarTopMargin();
        }

        private void OverlappingHeader_VisibilityChanged(DependencyObject sender, DependencyProperty property)
        {
            UpdateSidebarTopMargin();
        }

        private void UpdateSidebarTopMargin()
        {
            if (_sideLayoutLoaded && _isSideLayout)
                TabbarButtonsHost.Margin = new Thickness(4, GetSidebarTopInset(), 4, 8);
        }

        private void CollectBottomPadding(DependencyObject parent)
        {
            var element = parent as FrameworkElement;
            if (element != null && SideTabbarController.GetBottomInset(element) > 0)
            {
                var panel = element as StackPanel;
                if (panel != null) _bottomPadding[element] = panel.Padding;
            }
            // Template item trees can contain thousands of controls; only page chrome is marked.
            if (parent is ItemsControl || parent is Tabbar || parent is Navbar) return;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                CollectBottomPadding(VisualTreeHelper.GetChild(parent, i));
        }

        private static void SetElementPadding(FrameworkElement element, Thickness padding)
        {
            var panel = element as StackPanel;
            if (panel != null) panel.Padding = padding;
        }
    }
}
