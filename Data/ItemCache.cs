using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Lootlens.Data;

public static class ItemCache {
    private static readonly HttpClient _directClient = CreateClient(useProxy: false);
    private static readonly HttpClient _proxyClient = CreateClient(useProxy: true);
    private static readonly SemaphoreSlim _loadLock = new(1, 1);

    private static List<Item> _cache = new();
    private static DateTime _lastFetch = DateTime.MinValue;
    private static int _backgroundRefreshRunning;

    private const int CacheDurationMinutes = 60;
    private const int MaxRetries = 3;

    private const string GraphQlApiUrl = "https://api.tarkov.dev/graphql";
    private const string JsonItemsApiUrl = "https://json.tarkov.dev/regular/items";
    private const string JsonItemsLocalizationApiUrl = "https://json.tarkov.dev/regular/items_en";

    public enum ApiConnectionState {
        Unknown,
        Live,
        Offline
    }

    public sealed class ApiStatusSnapshot {
        public ApiConnectionState State { get; init; } = ApiConnectionState.Unknown;
        public string Source { get; init; } = "-";
        public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
    }

    public static ApiStatusSnapshot CurrentStatus { get; private set; } = new();
    public static event Action<ApiStatusSnapshot>? ApiStatusChanged;

    private static readonly Dictionary<string, string> TraderNamesById = new(StringComparer.OrdinalIgnoreCase) {
        ["54cb50c76803fa8b248b4571"] = "Prapor",
        ["54cb57776803fa99248b456e"] = "Therapist",
        ["58330581ace78e27b8b10cee"] = "Skier",
        ["5935c25fb3acc3127c3d8cd9"] = "Peacekeeper",
        ["5a7c2eca46aef81a7ca2145d"] = "Mechanic",
        ["5ac3b934156ae10c4430e83c"] = "Ragman",
        ["5c0647fdd443bc2504c2d371"] = "Jaeger",
        ["6617beeaa9cfa777ca915b7c"] = "Ref"
    };

    static ItemCache() {
        Debug.WriteLine("[ItemCache] Initialized with endpoint fallback strategy");
    }

    public class Item {
        [JsonPropertyName("id")]
        public string id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string name { get; set; } = string.Empty;

        [JsonPropertyName("shortName")]
        public string shortName { get; set; } = string.Empty;

        [JsonPropertyName("avg24hPrice")]
        public long avg24hPrice { get; set; }

        [JsonPropertyName("low24hPrice")]
        public long low24hPrice { get; set; }

        [JsonPropertyName("high24hPrice")]
        public long high24hPrice { get; set; }

        [JsonPropertyName("sellFor")]
        public List<SellFor>? sellFor { get; set; }
    }

    public class SellFor {
        [JsonPropertyName("source")]
        public string source { get; set; } = string.Empty;

        [JsonPropertyName("price")]
        public long price { get; set; }
    }

    public static async Task<List<Item>> SearchItems(string query) {
        await EnsureCacheLoaded();

        if (string.IsNullOrWhiteSpace(query))
            return _cache;

        var results = new List<Item>();
        foreach (var item in _cache) {
            if (item.name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.shortName.Contains(query, StringComparison.OrdinalIgnoreCase)) {
                results.Add(item);
            }
        }

        return results;
    }

    private static async Task EnsureCacheLoaded() {
        var cacheDuration = TimeSpan.FromMinutes(CacheDurationMinutes);

        if (_cache.Count > 0 && DateTime.UtcNow - _lastFetch < cacheDuration) {
            if (CurrentStatus.State == ApiConnectionState.Unknown) {
                SetApiStatus(ApiConnectionState.Offline, "In-memory cache");
            }

            return;
        }

        await _loadLock.WaitAsync();
        try {
            if (_cache.Count > 0 && DateTime.UtcNow - _lastFetch < cacheDuration)
                return;

            // Fast path: load cached data immediately and refresh API in background,
            // so UI status doesn't stay at Unknown while network retries run.
            if (_cache.Count == 0 && TryLoadCacheFile(out var cachedItems, out var cachedTimestamp) && cachedItems.Count > 0) {
                _cache.Clear();
                _cache.AddRange(cachedItems);
                _lastFetch = cachedTimestamp;
                SetApiStatus(ApiConnectionState.Offline, "Offline cache file");
                QueueBackgroundRefresh();
                return;
            }

            if (_cache.Count > 0) {
                SetApiStatus(ApiConnectionState.Offline, "In-memory cache");
                QueueBackgroundRefresh();
                return;
            }

            await LoadItemsFromApi();
        } finally {
            _loadLock.Release();
        }
    }

    private static void QueueBackgroundRefresh() {
        if (Interlocked.Exchange(ref _backgroundRefreshRunning, 1) == 1)
            return;

        _ = Task.Run(async () => {
            try {
                await _loadLock.WaitAsync();
                try {
                    await LoadItemsFromApi();
                } finally {
                    _loadLock.Release();
                }
            } catch (Exception ex) {
                Debug.WriteLine($"[ItemCache] Background refresh failed: {ex.Message}");
            } finally {
                Interlocked.Exchange(ref _backgroundRefreshRunning, 0);
            }
        });
    }

    private static async Task LoadItemsFromApi() {
        if (CurrentStatus.State == ApiConnectionState.Unknown) {
            SetApiStatus(ApiConnectionState.Offline, "Forbinder...");
        }

        var hasCacheFile = TryLoadCacheFile(out var fileCachedItems, out var fileCacheTimestamp);

        Exception? lastException = null;

        for (var retry = 1; retry <= MaxRetries; retry++) {
            foreach (var client in GetPreferredClients()) {
                try {
                    Debug.WriteLine($"[ItemCache] Attempt {retry}/{MaxRetries}: GraphQL via {GetClientLabel(client)}");
                    var graphqlItems = await FetchItemsFromGraphQlAsync(client);
                    if (graphqlItems.Count > 0) {
                        SetApiStatus(ApiConnectionState.Live, $"GraphQL ({GetClientLabel(client)})");
                        await StoreFreshCacheAsync(graphqlItems);
                        Debug.WriteLine($"[ItemCache] Loaded {graphqlItems.Count} items from GraphQL");
                        return;
                    }
                } catch (Exception ex) {
                    lastException = ex;
                    Debug.WriteLine($"[ItemCache] GraphQL failed via {GetClientLabel(client)}: {ex.Message}");
                }

                try {
                    Debug.WriteLine($"[ItemCache] Attempt {retry}/{MaxRetries}: JSON API via {GetClientLabel(client)}");
                    var jsonItems = await FetchItemsFromJsonApiAsync(client);
                    if (jsonItems.Count > 0) {
                        SetApiStatus(ApiConnectionState.Live, $"JSON ({GetClientLabel(client)})");
                        await StoreFreshCacheAsync(jsonItems);
                        Debug.WriteLine($"[ItemCache] Loaded {jsonItems.Count} items from JSON API fallback");
                        return;
                    }
                } catch (Exception ex) {
                    lastException = ex;
                    Debug.WriteLine($"[ItemCache] JSON API failed via {GetClientLabel(client)}: {ex.Message}");
                }
            }

            if (retry < MaxRetries) {
                var delayMs = retry * 1000;
                Debug.WriteLine($"[ItemCache] Retrying in {delayMs}ms");
                await Task.Delay(delayMs);
            }
        }

        if (hasCacheFile && fileCachedItems.Count > 0) {
            _cache.Clear();
            _cache.AddRange(fileCachedItems);
            _lastFetch = fileCacheTimestamp;
            SetApiStatus(ApiConnectionState.Offline, "Offline cache file");
            Debug.WriteLine($"[ItemCache] API failed, using offline cache file with {_cache.Count} items");
            return;
        }

        if (_cache.Count > 0) {
            SetApiStatus(ApiConnectionState.Offline, "In-memory cache");
            Debug.WriteLine($"[ItemCache] API failed, using in-memory cache with {_cache.Count} items");
            return;
        }

        SetApiStatus(ApiConnectionState.Offline, "No API connection");

        throw new InvalidOperationException(
            "Kunne ikke oprette forbindelse til Tarkov API. Tjek net/proxy/firewall og prøv igen.",
            lastException);
    }

    private static HttpClient[] GetPreferredClients() {
        // Try direct first because forcing a system proxy can fail on some local setups.
        return new[] { _directClient, _proxyClient };
    }

    private static async Task<List<Item>> FetchItemsFromGraphQlAsync(HttpClient client) {
        var graphqlQuery = new {
            query = @"
                query {
                    items {
                        id
                        name
                        shortName
                        avg24hPrice
                        low24hPrice
                        high24hPrice
                        sellFor {
                            source
                            price
                        }
                    }
                }
            "
        };

        var payload = JsonSerializer.Serialize(graphqlQuery);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(GraphQlApiUrl, content);
        response.EnsureSuccessStatusCode();

        var responseString = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseString);
        var root = doc.RootElement;

        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0) {
            var message = errors[0].TryGetProperty("message", out var msgElement)
                ? msgElement.GetString() ?? "Unknown GraphQL error"
                : "Unknown GraphQL error";
            throw new InvalidOperationException(message);
        }

        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("items", out var itemsElement)) {
            throw new InvalidOperationException("Invalid GraphQL response format");
        }

        var items = JsonSerializer.Deserialize<List<Item>>(itemsElement.GetRawText());
        return items ?? new List<Item>();
    }

    private static async Task<List<Item>> FetchItemsFromJsonApiAsync(HttpClient client) {
        var itemsJson = await client.GetStringAsync(JsonItemsApiUrl);
        var localizationJson = await client.GetStringAsync(JsonItemsLocalizationApiUrl);

        var localizedNames = ParseLocalizedNames(localizationJson);
        var localizedShortNames = ParseLocalizedShortNames(localizationJson);

        using var doc = JsonDocument.Parse(itemsJson);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("items", out var itemsObject) ||
            itemsObject.ValueKind != JsonValueKind.Object) {
            throw new InvalidOperationException("Invalid JSON items API response format");
        }

        var items = new List<Item>();

        foreach (var itemProperty in itemsObject.EnumerateObject()) {
            var itemElement = itemProperty.Value;
            var id = GetStringOrDefault(itemElement, "id", itemProperty.Name);

            var name = GetStringOrDefault(itemElement, "name", id);
            if (localizedNames.TryGetValue(id, out var translatedName) && !string.IsNullOrWhiteSpace(translatedName))
                name = translatedName;

            var shortName = GetStringOrDefault(itemElement, "shortName", name);
            if (localizedShortNames.TryGetValue(id, out var translatedShortName) && !string.IsNullOrWhiteSpace(translatedShortName))
                shortName = translatedShortName;

            var item = new Item {
                id = id,
                name = name,
                shortName = shortName,
                avg24hPrice = GetLongOrDefault(itemElement, "avg24hPrice"),
                low24hPrice = GetLongOrDefault(itemElement, "low24hPrice"),
                high24hPrice = GetLongOrDefault(itemElement, "high24hPrice"),
                sellFor = ParseSellOffers(itemElement)
            };

            items.Add(item);
        }

        return items;
    }

    private static List<SellFor> ParseSellOffers(JsonElement itemElement) {
        var offers = new List<SellFor>();

        if (itemElement.TryGetProperty("sellFor", out var sellForElement) && sellForElement.ValueKind == JsonValueKind.Array) {
            foreach (var offer in sellForElement.EnumerateArray()) {
                var source = GetStringOrDefault(offer, "source", string.Empty);
                var price = GetLongOrDefault(offer, "price");
                if (!string.IsNullOrWhiteSpace(source) && price > 0) {
                    offers.Add(new SellFor { source = source, price = price });
                }
            }

            if (offers.Count > 0)
                return offers;
        }

        if (itemElement.TryGetProperty("sellToTrader", out var sellToTraderElement) && sellToTraderElement.ValueKind == JsonValueKind.Array) {
            foreach (var offer in sellToTraderElement.EnumerateArray()) {
                var traderId = GetStringOrDefault(offer, "trader", "Trader");
                var source = TraderNamesById.TryGetValue(traderId, out var traderName)
                    ? traderName
                    : "Trader";

                var priceRub = GetLongOrDefault(offer, "priceRUB");
                var price = priceRub > 0 ? priceRub : GetLongOrDefault(offer, "price");

                if (price > 0) {
                    offers.Add(new SellFor { source = source, price = price });
                }
            }
        }

        return offers;
    }

    private static Dictionary<string, string> ParseLocalizedNames(string localizedJson) {
        return ParseLocalizedMap(localizedJson, " Name");
    }

    private static Dictionary<string, string> ParseLocalizedShortNames(string localizedJson) {
        return ParseLocalizedMap(localizedJson, " ShortName");
    }

    private static Dictionary<string, string> ParseLocalizedMap(string localizedJson, string suffix) {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(localizedJson);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return map;

        foreach (var property in data.EnumerateObject()) {
            if (!property.Name.EndsWith(suffix, StringComparison.Ordinal) || property.Value.ValueKind != JsonValueKind.String)
                continue;

            var id = property.Name.Substring(0, property.Name.Length - suffix.Length);
            var value = property.Value.GetString();
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(value)) {
                map[id] = value;
            }
        }

        return map;
    }

    private static string GetStringOrDefault(JsonElement element, string propertyName, string fallback) {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String) {
            return prop.GetString() ?? fallback;
        }

        return fallback;
    }

    private static long GetLongOrDefault(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out var prop))
            return 0;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var asLong))
            return asLong;

        if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out var parsed))
            return parsed;

        return 0;
    }

    private static HttpClient CreateClient(bool useProxy) {
        var handler = new HttpClientHandler {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseProxy = useProxy
        };

        if (useProxy) {
            handler.Proxy = HttpClient.DefaultProxy;
        }

        var client = new HttpClient(handler) {
            Timeout = TimeSpan.FromSeconds(20)
        };

        client.DefaultRequestHeaders.Add("User-Agent", "LootLens/1.1 (WPF Application)");
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        return client;
    }

    private static string GetClientLabel(HttpClient client) {
        if (ReferenceEquals(client, _directClient))
            return "direct";

        if (ReferenceEquals(client, _proxyClient))
            return "proxy";

        return "unknown";
    }

    private static async Task StoreFreshCacheAsync(List<Item> items) {
        _cache.Clear();
        _cache.AddRange(items);
        _lastFetch = DateTime.UtcNow;

        try {
            await SaveCacheFile(items);
        } catch (Exception ex) {
            Debug.WriteLine($"[ItemCache] Could not write cache file: {ex.Message}");
        }
    }

    private static bool TryLoadCacheFile(out List<Item> items, out DateTime timestampUtc) {
        items = new List<Item>();
        timestampUtc = DateTime.MinValue;

        var cacheFilePath = GetCacheFilePath();
        if (!File.Exists(cacheFilePath))
            return false;

        try {
            var cachedData = File.ReadAllText(cacheFilePath);
            var parsed = JsonSerializer.Deserialize<List<Item>>(cachedData);
            if (parsed == null || parsed.Count == 0)
                return false;

            items = parsed;
            timestampUtc = File.GetLastWriteTimeUtc(cacheFilePath);
            return true;
        } catch (Exception ex) {
            Debug.WriteLine($"[ItemCache] Failed to read cache file: {ex.Message}");
            return false;
        }
    }

    private static string GetCacheFilePath() {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var cacheDir = Path.Combine(appDataPath, "LootLens");
        Directory.CreateDirectory(cacheDir);
        return Path.Combine(cacheDir, "items_cache.json");
    }

    private static async Task SaveCacheFile(List<Item> items) {
        var cacheFilePath = GetCacheFilePath();
        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(cacheFilePath, json);
    }

    private static void SetApiStatus(ApiConnectionState state, string source) {
        var snapshot = new ApiStatusSnapshot {
            State = state,
            Source = source,
            UpdatedUtc = DateTime.UtcNow
        };

        CurrentStatus = snapshot;
        ApiStatusChanged?.Invoke(snapshot);
    }
}
