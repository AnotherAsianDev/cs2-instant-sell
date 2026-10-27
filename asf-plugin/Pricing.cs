using System;
using System.Collections.Generic;

namespace CS2InstantSell;

// Steam wallet fee settings (read from the market page, so they follow Steam's current values).
internal sealed record FeeInfo(double SteamPercent, long SteamMinimum, long SteamBase, double PublisherPercent);

// One planned sale: what the buyer pays and what the seller receives after fees (both in the smallest currency unit, e.g. cents).
internal readonly record struct SaleUnit(long BuyerPays, long SellerReceives);

internal static class Pricing {
	// Same fee logic as Steam's own sell dialog.
	private static long FeesFor(long amount, FeeInfo fee) {
		long steam = (long) Math.Floor(Math.Max(amount * fee.SteamPercent, fee.SteamMinimum) + fee.SteamBase);
		long publisher = fee.PublisherPercent > 0 ? (long) Math.Floor(Math.Max(amount * fee.PublisherPercent, 1)) : 0;

		return steam + publisher;
	}

	// Given what the buyer pays, returns what the seller receives (largest amount whose total with fees fits).
	internal static long SellerReceives(long buyerPays, FeeInfo fee) {
		long received = (long) Math.Floor((buyerPays - fee.SteamBase) / (fee.SteamPercent + fee.PublisherPercent + 1));

		if (received < 0) {
			received = 0;
		}

		while (received + 1 + FeesFor(received + 1, fee) <= buyerPays) {
			received++;
		}

		while ((received > 0) && (received + FeesFor(received, fee) > buyerPays)) {
			received--;
		}

		return received;
	}

	// Walks down the buy-order book (levels sorted from the highest price) and plans one sale per item that still has a buy order behind it.
	// Stops when the price drops more than maxDropPercent below the top bid (ignored for cheap items), or the payout gets below minPayout.
	internal static List<SaleUnit> Plan(IReadOnlyList<(long Price, long Quantity)> levels, int itemCount, int maxDropPercent, long minPayout, long noDropLimitBelow, FeeInfo fee) {
		List<SaleUnit> units = [];

		if ((levels.Count == 0) || (itemCount <= 0)) {
			return units;
		}

		long top = levels[0].Price;
		double floor = top < noDropLimitBelow ? 0 : top * (1 - (maxDropPercent / 100.0));

		int level = 0;
		long cumulative = levels[0].Quantity;

		for (int i = 0; i < itemCount; i++) {
			while (cumulative <= i) {
				level++;

				if (level >= levels.Count) {
					return units;
				}

				cumulative += levels[level].Quantity;
			}

			long buyerPays = levels[level].Price;

			if (buyerPays < floor) {
				break;
			}

			long receives = SellerReceives(buyerPays, fee);

			if ((receives < minPayout) || (receives < 1)) {
				break;
			}

			units.Add(new SaleUnit(buyerPays, receives));
		}

		return units;
	}
}
