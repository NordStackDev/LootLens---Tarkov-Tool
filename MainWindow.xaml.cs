using Lootlens.Data;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lootlens;

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

        ItemCache.ApiStatusChanged += HandleApiStatusChanged;

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

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

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
        ItemCache.ApiStatusChanged -= HandleApiStatusChanged;

        _ocrTimer.Stop();
        base.OnClosed(e);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e) {
        _settings = await Settings.LoadAsync();
        _settings.Normalize();
        UpdateInventoryValuePollingState();
        UpdateApiStatusIndicator(ItemCache.CurrentStatus);
        _ = PrimeApiStatusAsync();
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

            var item = FindBestItemMatch(items, name);

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
<<<<<<< Updated upstream
            var recognized = await _ocrService.RecognizeAroundCursorAsync(_settings.InventoryRegionWidth, _settings.InventoryRegionHeight);
            if (string.IsNullOrWhiteSpace(recognized)) {
                if (DateTime.UtcNow - _lastValidHoverAt < TimeSpan.FromMilliseconds(400))
                    return;

                SetTooltipVisible(false);
=======
            var recognizedCandidates = await _ocrService.RecognizeCandidateLinesAroundCursorAsync(
                _settings.InventoryRegionWidth,
                _settings.InventoryRegionHeight);

            if (recognizedCandidates.Count == 0)
                return;

            var cursorBucket = GetCursorBucket();
            var candidateSignature = BuildCandidateSignature(recognizedCandidates, cursorBucket);
            if (candidateSignature.Length == 0)
>>>>>>> Stashed changes
                return;
            }

            if (string.Equals(recognized, _lastRecognizedText, StringComparison.OrdinalIgnoreCase)) {
                if (_tooltipVisible) {
                    PositionWindowNearCursor();
                }
                return;
            }

            var primaryCandidate = recognizedCandidates[0];

            if (string.Equals(candidateSignature, _lastRecognizedText, StringComparison.OrdinalIgnoreCase))
                return;

            _lastRecognizedText = candidateSignature;

            var item = await FindBestItemMatchFromOcrAsync(recognizedCandidates);

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
<<<<<<< Updated upstream
            _lastValidHoverAt = DateTime.UtcNow;
            _lastQuery = recognized;
            RenderResultText(item, _settings);
            PositionWindowNearCursor();
            SetTooltipVisible(true);
=======
            _lastQuery = primaryCandidate;
            ResultText.Text = FormatItem(item, _settings);
>>>>>>> Stashed changes
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

    private static ItemCache.Item? FindBestItemMatch(IEnumerable<ItemCache.Item> items, string query) {
        var (item, score) = FindBestItemMatchWithScore(items, query);
        return score > 0 ? item : null;
    }

    private static (ItemCache.Item? item, int score) FindBestItemMatchWithScore(IEnumerable<ItemCache.Item> items, string query) {
        var normalizedQuery = NormalizeForMatch(query);
        if (normalizedQuery.Length == 0)
            return (null, int.MinValue);

        ItemCache.Item? bestItem = null;
        var bestScore = int.MinValue;

        foreach (var item in items) {
            var score = ScoreItem(item, normalizedQuery);
            if (score > bestScore) {
                bestScore = score;
                bestItem = item;
            }
        }

        return (bestItem, bestScore);
    }

    private static int ScoreItem(ItemCache.Item item, string normalizedQuery) {
        var normalizedName = NormalizeForMatch(item.name);
        var normalizedShort = NormalizeForMatch(item.shortName);

        var score = 0;

        if (normalizedName == normalizedQuery)
            score += 1000;

        if (normalizedShort == normalizedQuery)
            score += 950;

        if (normalizedName.StartsWith(normalizedQuery, StringComparison.Ordinal))
            score += 700;

        if (normalizedShort.StartsWith(normalizedQuery, StringComparison.Ordinal))
            score += 650;

        if (normalizedName.Contains(normalizedQuery, StringComparison.Ordinal))
            score += 500;

        if (normalizedShort.Contains(normalizedQuery, StringComparison.Ordinal))
            score += 450;

        var queryTokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (queryTokens.Length == 0)
            return score;

        var nameTokenHits = queryTokens.Count(token => normalizedName.Contains(token, StringComparison.Ordinal));
        var shortTokenHits = queryTokens.Count(token => normalizedShort.Contains(token, StringComparison.Ordinal));
        score += nameTokenHits * 60;
        score += shortTokenHits * 40;

        if (nameTokenHits == queryTokens.Length)
            score += 180;

        if (shortTokenHits == queryTokens.Length)
            score += 120;

        // Prefer less noisy OCR candidates by rewarding closer length to query.
        var lengthDelta = Math.Abs(normalizedName.Length - normalizedQuery.Length);
        score -= Math.Min(lengthDelta, 80);

        return score;
    }

    private static string NormalizeForMatch(string input) {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var lowered = input.Trim().ToLowerInvariant();
        var cleaned = Regex.Replace(lowered, "[^a-z0-9 ]+", " ");
        return Regex.Replace(cleaned, "\\s+", " ").Trim();
    }

    private async Task<ItemCache.Item?> FindBestItemMatchFromOcrAsync(IReadOnlyList<string> candidateQueries) {
        if (candidateQueries.Count == 0)
            return null;

        var allItems = await ItemCache.SearchItems(string.Empty);

        // First, try the top OCR line only. This is usually the tooltip header near cursor.
        var primaryQuery = candidateQueries[0];
        var primaryPool = await BuildCandidatePoolAsync(primaryQuery, allItems);
        var (primaryItem, primaryScore) = FindBestItemMatchWithScoreForOcr(primaryPool, primaryQuery);
        if (primaryItem != null && primaryScore >= 560 && IsDiscriminativeOcrQuery(primaryQuery))
            return primaryItem;

        ItemCache.Item? bestItem = null;
        var bestScore = int.MinValue;

        var topCandidates = candidateQueries.Take(2).ToList();
        for (var i = 0; i < topCandidates.Count; i++) {
            var candidateQuery = topCandidates[i];
            var normalized = NormalizeForMatch(candidateQuery);
            if (normalized.Length < 3)
                continue;

            if (!IsDiscriminativeOcrQuery(candidateQuery))
                continue;

            if (i > 0 && !HasAtLeastTwoWords(candidateQuery))
                continue;

            var candidatePool = await BuildCandidatePoolAsync(candidateQuery, allItems);
            var (item, score) = FindBestItemMatchWithScoreForOcr(candidatePool, candidateQuery);
            if (item == null)
                continue;

            // Prefer the line closest to cursor (rank 0) to target tooltip header text.
            score -= i * 420;

            if (score > bestScore) {
                bestScore = score;
                bestItem = item;
            }
        }

        // Confidence gate tuned for adaptive probes: still strict, but avoids dropping too many valid matches.
        return bestScore >= 640 ? bestItem : null;
    }

    private static (ItemCache.Item? item, int score) FindBestItemMatchWithScoreForOcr(IEnumerable<ItemCache.Item> items, string query) {
        var normalizedQuery = NormalizeForMatch(query);
        if (normalizedQuery.Length == 0)
            return (null, int.MinValue);

        var queryTokens = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        ItemCache.Item? bestItem = null;
        var bestScore = int.MinValue;

        foreach (var item in items) {
            var score = ScoreItem(item, normalizedQuery);

            var normalizedName = NormalizeForMatch(item.name);
            var normalizedShort = NormalizeForMatch(item.shortName);
            var itemCorpus = normalizedName + " " + normalizedShort;

            var tokenHits = 0;
            var missingKeyTokens = 0;

            foreach (var token in queryTokens) {
                if (itemCorpus.Contains(token, StringComparison.Ordinal)) {
                    tokenHits++;
                    continue;
                }

                // Tokens with digits or longer chunks are usually highly discriminative.
                if (token.Any(char.IsDigit) || token.Length >= 5)
                    missingKeyTokens++;
            }

            if (queryTokens.Length > 0) {
                var coverage = (double)tokenHits / queryTokens.Length;

                score += tokenHits * 110;
                score -= (queryTokens.Length - tokenHits) * 70;
                score -= missingKeyTokens * 260;

                // Reject matches that only hit generic words like "assault"/"rifle".
                if (queryTokens.Length >= 3 && coverage < 0.55)
                    score -= 1200;

                if (queryTokens.Length >= 4 && coverage < 0.70)
                    score -= 700;
            }

            if (score > bestScore) {
                bestScore = score;
                bestItem = item;
            }
        }

        return (bestItem, bestScore);
    }

    private async Task PrimeApiStatusAsync() {
        try {
            await ItemCache.SearchItems(string.Empty);
        } catch (Exception ex) {
            Debug.WriteLine($"[Startup] API prime failed: {ex.Message}");
        }
    }

    private async Task<List<ItemCache.Item>> BuildCandidatePoolAsync(string query, List<ItemCache.Item> allItems) {
        var byId = new Dictionary<string, ItemCache.Item>(StringComparer.OrdinalIgnoreCase);

        void AddItems(IEnumerable<ItemCache.Item> items) {
            foreach (var item in items) {
                if (!byId.ContainsKey(item.id))
                    byId[item.id] = item;
            }
        }

        AddItems(await ItemCache.SearchItems(query));

        var tokens = NormalizeForMatch(query)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();

        foreach (var token in tokens) {
            AddItems(await ItemCache.SearchItems(token));
        }

        if (byId.Count == 0)
            return allItems;

        return byId.Values.ToList();
    }

    private static string BuildCandidateSignature(IReadOnlyList<string> candidates, string cursorBucket) {
        if (candidates.Count == 0)
            return string.Empty;

        var topCandidates = string.Join(" | ", candidates.Take(3).Select(c => c.Trim().ToLowerInvariant()));
        return $"{cursorBucket}::{topCandidates}";
    }

    private static bool HasAtLeastTwoWords(string text) {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var words = text
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return words.Length >= 2;
    }

    private static bool IsDiscriminativeOcrQuery(string text) {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = NormalizeForMatch(text);
        if (normalized.Length < 5)
            return false;

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length >= 2)
            return true;

        var token = tokens[0];
        return token.Length >= 8 && (token.Any(char.IsDigit) || token.Contains('x'));
    }

    private static string GetCursorBucket() {
        if (!GetCursorPos(out var cursor))
            return "0:0";

        // Bucketize cursor coordinates to react to item changes without over-triggering on tiny jitter.
        var bucketX = cursor.X / 10;
        var bucketY = cursor.Y / 10;
        return $"{bucketX}:{bucketY}";
    }

    private void HandleApiStatusChanged(ItemCache.ApiStatusSnapshot status) {
        if (!Dispatcher.CheckAccess()) {
            Dispatcher.Invoke(() => UpdateApiStatusIndicator(status));
            return;
        }

        UpdateApiStatusIndicator(status);
    }

    private void UpdateApiStatusIndicator(ItemCache.ApiStatusSnapshot status) {
        var (text, color) = status.State switch {
            ItemCache.ApiConnectionState.Live => ($"API: live ({status.Source})", Color.FromRgb(0x6D, 0xE2, 0x8A)),
            ItemCache.ApiConnectionState.Offline => ($"API: offline ({status.Source})", Color.FromRgb(0xF0, 0x8A, 0x8A)),
            _ => ("API: ukendt", Color.FromRgb(0xFF, 0xC5, 0x8A))
        };

        ApiStatusText.Text = text;
        ApiStatusText.Foreground = new SolidColorBrush(color);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT {
        public int X;
        public int Y;
    }
}