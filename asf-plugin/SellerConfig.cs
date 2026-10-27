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

	// Let ASF's own 2FA accept the market confirmations (needs the bot to have ASF 2FA imported).
	public bool AutoConfirm { get; init; } = true;

	// Safety cap for one run. 0 means no cap.
	public int MaxItemsPerRun { get; init; }

	// Items whose market name contains one of these texts are never sold, e.g. "Dragon Lore".
	public List<string> Keep { get; init; } = [];

	// Run a real sell automatically every N hours. 0 means never (only the commands sell).
	public int AutoRunHours { get; init; }

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
