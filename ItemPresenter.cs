using Lootlens.Data;
using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Lootlens;

// Shared rendering used by both the search result box and the hover tooltip.
internal static class ItemPresenter {
    public static void RenderInlines(TextBlock target, ItemCache.Item item, Settings settings) {
        target.Inlines.Clear();

        target.Inlines.Add(new Run(item.name + Environment.NewLine) { Foreground = Brushes.White });

        if (settings.ShowFleaPrice && item.avg24hPrice > 0) {
            target.Inlines.Add(new Run($"Avg {Fmt(item.avg24hPrice)} ₽") {
                Foreground = Brushes.LimeGreen
            });
            target.Inlines.Add(new Run(Environment.NewLine));
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
                target.Inlines.Add(new Run($"{bestOffer.source}: {Fmt(bestOffer.price)} ₽") {
                    Foreground = Brushes.Gold
                });
                target.Inlines.Add(new Run(Environment.NewLine));
            }
        }

        if (settings.ShowProfit) {
            var bestTrader = traderOffers.FirstOrDefault();
            if (bestTrader != null && item.avg24hPrice > 0) {
                var profit = bestTrader.price - item.avg24hPrice;
                target.Inlines.Add(new Run($"Profit {Fmt(profit)} ₽") {
                    Foreground = Brushes.Gold
                });
            }
        }
    }

    private static string Fmt(long v) => v == 0 ? "–" : v.ToString("N0");
}
