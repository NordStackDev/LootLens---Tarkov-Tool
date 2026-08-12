using System;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace LootLens;

public partial class MainWindow : Window {
    private const int HotkeyId = 9000;
    private const int WmHotkey = 0x0312;
    private static readonly HttpClient Http = new();

    public MainWindow() {
        InitializeComponent();
        Loaded += (_, _) => Hide();          // starter skjult – F6 viser den
    }

    /* ---------- Global hotkey: F6 ---------- */
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    protected override void OnSourceInitialized(EventArgs e) {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        RegisterHotKey(hwnd, HotkeyId, 0, 0x75);   // 0x75 = F6
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId) {
            Toggle();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Toggle() {
        if (Visibility == Visibility.Visible) Hide();
        else {
            Show();
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }

    protected override void OnClosed(EventArgs e) {
        UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
        base.OnClosed(e);
    }

    /* ---------- Søgning mod tarkov.dev ---------- */
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Escape) { Hide(); return; }
        if (e.Key != Key.Enter) return;

        var name = SearchBox.Text.Trim();
        if (name.Length == 0) return;

        ResultText.Text = "Søger…";

        try {
            var query = """
            {
              itemsByName(name: "NAME") {
                name
                avg24hPrice
                low24hPrice
                high24hPrice
                sellFor { source price }
              }
            }
            """.Replace("NAME", name.Replace("\"", "\\\""));

            using var resp = await Http.PostAsync(
                "https://api.tarkov.dev/graphql",
                new StringContent(JsonSerializer.Serialize(new { query }),
                                  Encoding.UTF8, "application/json"));
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var item = doc.RootElement
                .GetProperty("data").GetProperty("itemsByName")
                .EnumerateArray().FirstOrDefault();

            ResultText.Text = item.ValueKind == JsonValueKind.Undefined
                ? "Intet fundet for: " + name
                : FormatItem(item);
        } catch (Exception ex) {
            ResultText.Text = "Fejl: " + ex.Message;
        }
    }

    private static string FormatItem(JsonElement item) {
        var sb = new StringBuilder();
        sb.AppendLine(item.GetProperty("name").GetString() ?? "?");
        sb.AppendLine();
        sb.AppendLine($"Flea 24h: avg {Fmt(GetLong(item, "avg24hPrice"))} · " +
                      $"low {Fmt(GetLong(item, "low24hPrice"))} · " +
                      $"high {Fmt(GetLong(item, "high24hPrice"))}");
        sb.AppendLine();
        sb.AppendLine("Bedste salg:");

        var offers = item.GetProperty("sellFor").EnumerateArray()
            .Select(o => (Source: o.GetProperty("source").GetString() ?? "?",
                          Price: GetLong(o, "price")))
            .Where(o => o.Price > 0)
            .OrderByDescending(o => o.Price)
            .Take(3);

        foreach (var (source, price) in offers)
            sb.AppendLine($"   {source}: {Fmt(price)} ₽");

        return sb.ToString();
    }

    private static long GetLong(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt64() : 0;

    private static string Fmt(long v) => v == 0 ? "–" : v.ToString("N0");
}