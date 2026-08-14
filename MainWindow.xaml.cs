using Lootlens.Data;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LootLens;

public partial class MainWindow : Window {
    private const int ToggleOverlayHotkeyId = 9000;
    private const int ToggleOcrHotkeyId = 9001;
    private const int WmHotkey = 0x0312;
    private readonly DispatcherTimer _searchDebounceTimer;
    private readonly DispatcherTimer _ocrTimer;
    private readonly OcrService _ocrService = new();
    private Settings _settings = new();
    private string _lastQuery = string.Empty;
    private string _lastRecognizedText = string.Empty;
    private string _lastRecognizedItemName = string.Empty;
    private DateTime _lastValidHoverAt = DateTime.MinValue;
    private DateTime _tooltipHiddenAt = DateTime.MinValue;
    private bool _tooltipVisible;
    private int _searchVersion;
    private bool _ocrInProgress;

    public MainWindow() {
        InitializeComponent();

        _searchDebounceTimer = new DispatcherTimer {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;

        _ocrTimer = new DispatcherTimer {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _ocrTimer.Tick += OcrTimer_Tick;

        Loaded += MainWindow_Loaded;

    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    protected override void OnSourceInitialized(EventArgs e) {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        if (!RegisterHotKey(hwnd, ToggleOverlayHotkeyId, 0, 0x75)) {
            Debug.WriteLine("Could not register F6 hotkey.");
        }

        if (!RegisterHotKey(hwnd, ToggleOcrHotkeyId, 0, 0x76)) {
            Debug.WriteLine("Could not register F7 hotkey.");
        }

        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (msg == WmHotkey) {
            if (wParam.ToInt32() == ToggleOverlayHotkeyId) {
                Toggle();
                handled = true;
            } else if (wParam.ToInt32() == ToggleOcrHotkeyId) {
                _ = ToggleOcrAsync();
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void Toggle() {
        if (Visibility == Visibility.Visible) Hide();
        else {
            PositionWindowNearCursor();
            Show();
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }

    protected override void OnClosed(EventArgs e) {
        var hwnd = new WindowInteropHelper(this).Handle;
        UnregisterHotKey(hwnd, ToggleOverlayHotkeyId);
        UnregisterHotKey(hwnd, ToggleOcrHotkeyId);

        _ocrTimer.Stop();
        base.OnClosed(e);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e) {
        _settings = await Settings.LoadAsync();
        _settings.Normalize();
        UpdateInventoryValuePollingState();
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Escape) { Hide(); return; }
        if (e.Key != Key.Enter) return;

        _searchDebounceTimer.Stop();
        await SearchAndRenderAsync(SearchBox.Text.Trim());
    }

    private void PositionWindowNearCursor() {
        if (!GetCursorPos(out var point))
            return;

        var workArea = SystemParameters.WorkArea;
        var popupWidth = (int)Math.Max(ActualWidth, 180);
        var popupHeight = (int)Math.Max(ActualHeight, 36);

        var left = point.X + 18;
        var top = point.Y + 18;

        if (left + popupWidth > workArea.Right)
            left = point.X - popupWidth - 18;

        if (top + popupHeight > workArea.Bottom)
            top = point.Y - popupHeight - 18;

        var maxLeft = (int)Math.Max(workArea.Left, workArea.Right - popupWidth);
        var maxTop = (int)Math.Max(workArea.Top, workArea.Bottom - popupHeight);

        Left = Math.Clamp(left, (int)workArea.Left, maxLeft);
        Top = Math.Clamp(top, (int)workArea.Top, maxTop);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) {
        var query = SearchBox.Text.Trim();

        if (query.Length == 0) {
            _searchDebounceTimer.Stop();
            _lastQuery = string.Empty;
            ResultText.Inlines.Clear();
            HideTooltip();
            return;
        }

        ResultText.Inlines.Clear();
        ResultText.Text = "Søger…";
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private async void SearchDebounceTimer_Tick(object? sender, EventArgs e) {
        _searchDebounceTimer.Stop();
        await SearchAndRenderAsync(SearchBox.Text.Trim());
    }

    private async Task SearchAndRenderAsync(string name) {
        if (name.Length == 0)
            return;

        if (string.Equals(name, _lastQuery, StringComparison.OrdinalIgnoreCase)) return;

        var currentVersion = ++_searchVersion;

        ResultText.Inlines.Clear();
        ResultText.Text = "Søger…";

        try {
            var items = await ItemCache.SearchItems(name);

            if (currentVersion != _searchVersion)
                return;

            var item = items.FirstOrDefault(i =>
                i.name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                i.shortName.Contains(name, StringComparison.OrdinalIgnoreCase));

            if (item == null) {
                ResultText.Text = "Intet fundet for: " + name;
                HideTooltip();
                return;
            }

            _lastQuery = name;
            RenderResultText(item, _settings);
            PositionWindowNearCursor();
            ShowTooltip();
        } catch (Exception ex) {
            if (currentVersion != _searchVersion)
                return;

            ResultText.Text = "Fejl: " + ex.Message;
            HideTooltip();
        }
    }

    private void RenderResultText(ItemCache.Item item, Settings settings) {
        ResultText.Inlines.Clear();

        ResultText.Inlines.Add(new Run(item.name + Environment.NewLine) { Foreground = Brushes.White });

        if (settings.ShowFleaPrice && item.avg24hPrice > 0) {
            ResultText.Inlines.Add(new Run($"Avg {Fmt(item.avg24hPrice)} ₽") {
                Foreground = Brushes.LimeGreen
            });
            ResultText.Inlines.Add(new Run(Environment.NewLine));
        }

        var traderOffers = Enumerable.Empty<ItemCache.SellFor>();

        if (item.sellFor != null) {
            traderOffers = item.sellFor
                .Where(o => !string.Equals(o.source, "Flea Market", StringComparison.OrdinalIgnoreCase))
                .Where(o => o.price > 0)
                .OrderByDescending(o => o.price)
                .Take(2);
        }

        if (settings.ShowTraderPrice) {
            var bestOffer = traderOffers.FirstOrDefault();
            if (bestOffer != null) {
                ResultText.Inlines.Add(new Run($"{bestOffer.source}: {Fmt(bestOffer.price)} ₽") {
                    Foreground = Brushes.Gold
                });
                ResultText.Inlines.Add(new Run(Environment.NewLine));
            }
        }

        if (settings.ShowProfit) {
            var bestTrader = traderOffers.FirstOrDefault();
            if (bestTrader != null && item.avg24hPrice > 0) {
                var profit = bestTrader.price - item.avg24hPrice;
                ResultText.Inlines.Add(new Run($"Profit {Fmt(profit)} ₽") {
                    Foreground = Brushes.Gold
                });
            }
        }
    }

    private static string Fmt(long v) => v == 0 ? "–" : v.ToString("N0");

    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (e.OriginalSource is DependencyObject source) {
            var parentButton = FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source);
            if (parentButton != null) return;
        }

        DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) {
        System.Windows.Application.Current.Shutdown();
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e) {
        System.Windows.MessageBox.Show(
            "F6 = vis/skjul\n" +
            "F7 = Inventory Value til/fra\n" +
            "Skriv = live-søg\n" +
            "Enter = søg nu\n" +
            "Esc = skjul\n" +
            "✕ = luk\n\n" +
            "Data: json.tarkov.dev (regular/items.json)",
            "LootLens hjælp",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject {
        while (current != null) {
            if (current is T typed)
                return typed;

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private async void OcrTimer_Tick(object? sender, EventArgs e) {
        if (!_settings.InventoryValueEnabled || _ocrInProgress)
            return;

        _ocrInProgress = true;

        try {
            var recognized = await _ocrService.RecognizeAroundCursorAsync(_settings.InventoryRegionWidth, _settings.InventoryRegionHeight);
            if (string.IsNullOrWhiteSpace(recognized)) {
                if (DateTime.UtcNow - _lastValidHoverAt < TimeSpan.FromMilliseconds(400))
                    return;

                SetTooltipVisible(false);
                return;
            }

            if (string.Equals(recognized, _lastRecognizedText, StringComparison.OrdinalIgnoreCase)) {
                if (_tooltipVisible) {
                    PositionWindowNearCursor();
                }
                return;
            }

            _lastRecognizedText = recognized;

            var items = await ItemCache.SearchItems(recognized);
            var item = items.FirstOrDefault(i =>
                i.name.Contains(recognized, StringComparison.OrdinalIgnoreCase) ||
                i.shortName.Contains(recognized, StringComparison.OrdinalIgnoreCase));

            if (item == null) {
                if (DateTime.UtcNow - _lastValidHoverAt < TimeSpan.FromMilliseconds(400))
                    return;

                SetTooltipVisible(false);
                return;
            }

            if (string.Equals(item.name, _lastRecognizedItemName, StringComparison.OrdinalIgnoreCase)) {
                PositionWindowNearCursor();
                SetTooltipVisible(true);
                return;
            }

            _lastRecognizedItemName = item.name;
            _lastValidHoverAt = DateTime.UtcNow;
            _lastQuery = recognized;
            RenderResultText(item, _settings);
            PositionWindowNearCursor();
            SetTooltipVisible(true);
        } catch (Exception ex) {
            Debug.WriteLine($"[OCR] Timer tick failed: {ex.Message}");
            SetTooltipVisible(false);
        } finally {
            _ocrInProgress = false;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT {
        public int X;
        public int Y;
    }

    private void SetTooltipVisible(bool visible) {
        if (visible) {
            _tooltipHiddenAt = DateTime.MinValue;
            if (_tooltipVisible && Visibility == Visibility.Visible && Opacity >= 0.9)
                return;

            BeginAnimation(OpacityProperty, null);
            Visibility = Visibility.Visible;
            Opacity = 0;
            _tooltipVisible = true;

            var showAnimation = new System.Windows.Media.Animation.DoubleAnimation {
                From = 0,
                To = 0.98,
                Duration = TimeSpan.FromMilliseconds(150),
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
            };

            showAnimation.Completed += (_, __) => {
                if (_tooltipVisible)
                    Opacity = 0.98;
            };

            BeginAnimation(OpacityProperty, showAnimation);
            return;
        }

        if (!_tooltipVisible || Visibility != Visibility.Visible)
            return;

        if (_tooltipHiddenAt == DateTime.MinValue)
            _tooltipHiddenAt = DateTime.UtcNow;

        if (DateTime.UtcNow - _tooltipHiddenAt < TimeSpan.FromMilliseconds(220))
            return;

        BeginAnimation(OpacityProperty, null);
        _tooltipVisible = false;

        var hideAnimation = new System.Windows.Media.Animation.DoubleAnimation {
            From = Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(120),
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
        };

        hideAnimation.Completed += (_, __) => {
            if (!_tooltipVisible) {
                Visibility = Visibility.Hidden;
                Opacity = 0;
            }
        };

        BeginAnimation(OpacityProperty, hideAnimation);
    }

    private void ShowTooltip() => SetTooltipVisible(true);

    private void HideTooltip() => SetTooltipVisible(false);

    private async Task ToggleOcrAsync() {
        _settings.InventoryValueEnabled = !_settings.InventoryValueEnabled;
        UpdateInventoryValuePollingState();
        await Settings.SaveAsync(_settings);
    }

    private void UpdateInventoryValuePollingState() {
        if (_settings.InventoryValueEnabled) {
            _lastRecognizedText = string.Empty;
            _lastRecognizedItemName = string.Empty;
            _ocrTimer.Start();
            return;
        }

        _ocrTimer.Stop();
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e) {
        var dialog = new SettingsWindow(_settings) {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        _settings = dialog.EditableSettings.Clone();
        _settings.Normalize();
        UpdateInventoryValuePollingState();
        await Settings.SaveAsync(_settings);
    }
}