using System;
using System.Collections.Generic;
using System.Text.Json;

namespace CS2InstantSell;

// Read from each bot's own config file, under the "CS2InstantSell" property. Bots without it are never touched.
internal sealed class SellerConfig {
	internal const string PropertyName = "CS2InstantSell";

	// Master switch for this bot. Commands and the automatic run do nothing unless this is true.
	public bool Enabled { get; init; }

	// Items that would pay out less than this (after fees, in the wallet currency, e.g. 0.01 CHF) are skipped.
	public decimal MinPayout { get; init; } = 0.01m;

	// Stop going down the buy-order list when the price is this many percent below the highest buy order.
	public int MaxDropPercent { get; init; } = 20;

	// Items whose top buy order is below this price ignore the drop limit above.
	public decimal NoDropLimitBelow { get; init; } = 0.50m;

	// Items whose lowest market price is this or less (wallet currency, e.g. 0.03 CHF = the market minimum) are ignored:
	// they practically never have buy orders. Checked on the order-book request that is made anyway, then remembered for 24 h. 0 = off.
	public decimal IgnoreMarketPriceUpTo { get; init; } = 0.03m;

	// Let ASF's own 2FA accept the market confirmations (needs the bot to have ASF 2FA imported).
	public bool AutoConfirm { get; init; } = true;

	// Safety cap for one run. 0 means no cap.
	public int MaxItemsPerRun { get; init; }

	// Items whose market name contains one of these texts are never sold, e.g. "Dragon Lore".
	public List<string> Keep { get; init; } = [];

	// Run a real sell automatically every N hours. 0 means never (only the commands sell).
	// Minimum is 1 (smaller values are raised to 1). Can be combined with AutoRunTimes; runs never overlap.
	public int AutoRunHours { get; init; }

	// Run a real sell automatically at these fixed times of day (24h "HH:mm", local time of the machine running ASF),
	// e.g. [ "04:00" ] or [ "04:00", "16:30" ].
	public List<string> AutoRunTimes { get; init; } = [];

	// After selling, use the wallet balance to buy TF2 "Mann Co. Supply Crate Key" from the Steam market (instant buy of the cheapest listings).
	// Off by default. It does nothing unless Tf2KeyMaxPrice is also set.
	public bool BuyTf2Keys { get; init; }

	// Hard limit: never pay more than this per key (wallet currency, fees included). 0 = not set, so nothing is bought.
	public decimal Tf2KeyMaxPrice { get; init; }

	// Wallet balance that is never spent on keys.
	public decimal Tf2KeyKeepBalance { get; init; }

	// Optional cap of keys bought per run. 0 = no cap: buy as many as the balance allows.
	public int Tf2KeyMaxPerRun { get; init; }

	private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

	internal static SellerConfig? Parse(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties, out string? error) {
		error = null;

		if ((additionalConfigProperties == null) || !additionalConfigProperties.TryGetValue(PropertyName, out JsonElement element)) {
			return null;
		}

		try {
			return element.Deserialize<SellerConfig>(Options);
		} catch (JsonException e) {
			error = e.Message;

			return null;
		}
	}
}
