using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam.Integration;
using ArchiSteamFarm.Steam.Security;
using ArchiSteamFarm.Web.Responses;
using SteamKit2;

namespace CS2InstantSell;

// Optional: turn wallet funds into TF2 keys by instantly buying the cheapest market listings.
internal sealed partial class SellerRunner {
	private const uint KeyAppID = 440;
	private const string KeyName = "Mann Co. Supply Crate Key";

	private sealed record KeyListing(string ListingID, long Subtotal, long Fee) {
		internal long Total => Subtotal + Fee;
	}

	private Uri KeyPage => new(ArchiWebHandler.SteamCommunityURL, $"/market/listings/{KeyAppID}/{Uri.EscapeDataString(KeyName)}");

	// Returns a short text for the run summary ("" if nothing was bought or the feature is off).
	private async Task<string> BuyKeysAsync(bool dryRun, WalletInfo wallet, Func<long, string> money, CancellationToken ct) {
		SellerConfig cfg = Config;

		if (!cfg.BuyTf2Keys) {
			return "";
		}

		const string tag = "TF2 keys: ";

		if (cfg.Tf2KeyMaxPrice <= 0) {
			Warn(tag + "BuyTf2Keys is on, but Tf2KeyMaxPrice is not set (the most you accept per key, e.g. 2.50). Nothing will be bought.");

			return "";
		}

		long maxPrice = (long) Math.Round(cfg.Tf2KeyMaxPrice * 100m);
		long keep = (long) Math.Round(cfg.Tf2KeyKeepBalance * 100m);
		int maxKeys = cfg.Tf2KeyMaxPerRun > 0 ? cfg.Tf2KeyMaxPerRun : int.MaxValue;
		string maxKeysText = cfg.Tf2KeyMaxPerRun > 0 ? $"at most {cfg.Tf2KeyMaxPerRun} keys" : "no cap on the number of keys";

		if (!dryRun) {
			// Give Steam a moment to credit the money of the sales that just filled.
			await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
		}

		long balance = Bot.WalletBalance;
		Info(tag + $"wallet balance {money(balance)}, keeping {money(keep)}, price limit {money(maxPrice)} per key, {maxKeysText}.");

		int bought = 0;
		long spent = 0;

		while (bought < maxKeys) {
			ct.ThrowIfCancellationRequested();

			KeyListing? listing = await GetCheapestKeyAsync(wallet, ct).ConfigureAwait(false);

			if (listing == null) {
				Warn(tag + "could not read the key listings, stopping.");

				break;
			}

			if (listing.Total > maxPrice) {
				Info(tag + $"cheapest key costs {money(listing.Total)}, above your limit of {money(maxPrice)}. Not buying.");

				break;
			}

			long available = balance - keep;

			if (available < listing.Total) {
				Info(tag + $"cheapest key costs {money(listing.Total)}, only {money(Math.Max(0, available))} available above the kept balance. Done.");

				break;
			}

			if (dryRun) {
				long count = Math.Min(maxKeys, available / listing.Total);
				Info(tag + $"DRY RUN: cheapest key {money(listing.Total)}; with the current balance about {count} key(s) would be bought (price checked again for every key).");

				break;
			}

			long before = Bot.WalletBalance;
			(bool ok, string message) = await BuyOneKeyAsync(listing, wallet, ct).ConfigureAwait(false);

			if (!ok) {
				// Do not retry blindly: a wrong guess here would cost money.
				Warn(tag + $"purchase failed, stopping. {message}");

				break;
			}

			// Steam's balance update is the ground truth that the key was really paid.
			bool paid = false;

			for (int i = 0; i < 15; i++) {
				if (Bot.WalletBalance < before) {
					paid = true;

					break;
				}

				await Task.Delay(1000, ct).ConfigureAwait(false);
			}

			if (!paid) {
				Warn(tag + "Steam reported success but the wallet balance did not change yet. Stopping to be safe; check the market history.");

				break;
			}

			bought++;
			spent += before - Bot.WalletBalance;
			balance = Bot.WalletBalance;
			Info(tag + $"BOUGHT [{bought}] Mann Co. Supply Crate Key for {money(before - balance)}. Wallet now {money(balance)}.");

			await Task.Delay(2000, ct).ConfigureAwait(false);
		}

		if (bought > 0) {
			Info(tag + $"bought {bought} key(s) for {money(spent)} in total. They are in the bot's TF2 inventory.");

			return $", {bought} TF2 key(s) bought for {money(spent)}";
		}

		return "";
	}

	private async Task<KeyListing?> GetCheapestKeyAsync(WalletInfo wallet, CancellationToken ct) {
		Uri url = new(ArchiWebHandler.SteamCommunityURL, $"/market/listings/{KeyAppID}/{Uri.EscapeDataString(KeyName)}/render/?query=&start=0&count=10&country={Uri.EscapeDataString(wallet.Country)}&language=english&currency={wallet.CurrencyId}");

		ObjectResponse<JsonElement>? response = await Bot.ArchiWebHandler.UrlGetToJsonObjectWithSession<JsonElement>(url, referer: KeyPage, requestOptions: KeepErrors, maxTries: 2, cancellationToken: ct).ConfigureAwait(false);

		if (response == null) {
			return null;
		}

		if (response.StatusCode == HttpStatusCode.TooManyRequests) {
			Warn("TF2 keys: Steam rate limit reached, waiting 30 s...");
			await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

			return null;
		}

		JsonElement root = response.Content;

		if ((root.ValueKind != JsonValueKind.Object) || !root.TryGetProperty("listinginfo", out JsonElement info) || (info.ValueKind != JsonValueKind.Object)) {
			Warn($"TF2 keys: unexpected listing reply (HTTP {(int) response.StatusCode}): {root.ToString()[..Math.Min(200, root.ToString().Length)]}");

			return null;
		}

		return PickCheapest(info, wallet.CurrencyId);
	}

	private static KeyListing? PickCheapest(JsonElement info, int currencyId) {
		long walletCurrencyId = 2000 + currencyId;
		KeyListing? best = null;

		foreach (JsonProperty entry in info.EnumerateObject()) {
			JsonElement l = entry.Value;

			if (l.ValueKind != JsonValueKind.Object) {
				continue;
			}

			long subtotal, fee;

			if (l.TryGetProperty("converted_price", out _) && ((long) ReadNumber(l, "converted_currencyid", walletCurrencyId) == walletCurrencyId)) {
				subtotal = (long) ReadNumber(l, "converted_price", 0);
				fee = (long) ReadNumber(l, "converted_fee", 0);
			} else if ((long) ReadNumber(l, "currencyid", 0) == walletCurrencyId) {
				subtotal = (long) ReadNumber(l, "price", 0);
				fee = (long) ReadNumber(l, "fee", 0) + (long) ReadNumber(l, "publisher_fee", 0);
			} else {
				continue;
			}

			if ((subtotal <= 0) || (fee <= 0)) {
				continue;
			}

			string id = l.TryGetProperty("listingid", out JsonElement idElement) && (idElement.ValueKind == JsonValueKind.String) ? idElement.GetString() ?? entry.Name : entry.Name;
			KeyListing candidate = new(id, subtotal, fee);

			if ((best == null) || (candidate.Total < best.Total)) {
				best = candidate;
			}
		}

		return best;
	}

	private async Task<(bool Success, string Message)> BuyOneKeyAsync(KeyListing listing, WalletInfo wallet, CancellationToken ct) {
		(bool ok, string? confirmationId, string raw) = await PostBuyAsync(listing, wallet, null, ct).ConfigureAwait(false);

		if (ok) {
			return (true, "");
		}

		if (confirmationId == null) {
			return (false, "Steam's reply: " + raw);
		}

		// Steam asks for a mobile confirmation of the purchase first.
		if (!Config.AutoConfirm) {
			return (false, "The purchase needs a mobile confirmation, but AutoConfirm is off.");
		}

		await ConfirmAsync(EMobileConfirmationType.MarketPurchase).ConfigureAwait(false);
		await Task.Delay(3000, ct).ConfigureAwait(false);

		(ok, _, raw) = await PostBuyAsync(listing, wallet, confirmationId, ct).ConfigureAwait(false);

		return ok ? (true, "") : (false, "Steam's reply after confirming: " + raw);
	}

	private async Task<(bool Success, string? ConfirmationID, string Raw)> PostBuyAsync(KeyListing listing, WalletInfo wallet, string? confirmationId, CancellationToken ct) {
		IDictionary<string, string> data = new Dictionary<string, string> {
			["currency"] = wallet.CurrencyId.ToString(CultureInfo.InvariantCulture),
			["subtotal"] = listing.Subtotal.ToString(CultureInfo.InvariantCulture),
			["fee"] = listing.Fee.ToString(CultureInfo.InvariantCulture),
			["total"] = listing.Total.ToString(CultureInfo.InvariantCulture),
			["quantity"] = "1",
			["billing_state"] = "",
			["save_my_address"] = "0",
			["confirmation"] = confirmationId ?? "0"
		};

		Uri url = new(ArchiWebHandler.SteamCommunityURL, $"/market/buylisting/{listing.ListingID}");

		// maxTries: 1 on purpose, a blind retry of a purchase could buy twice.
		ObjectResponse<JsonElement>? response = await Bot.ArchiWebHandler.UrlPostToJsonObjectWithSession<JsonElement>(url, data: data, referer: KeyPage, requestOptions: KeepErrors, maxTries: 1, cancellationToken: ct).ConfigureAwait(false);

		if (response == null) {
			return (false, null, "no response from Steam");
		}

		JsonElement root = response.Content;
		string raw = root.ValueKind == JsonValueKind.Undefined ? $"HTTP {(int) response.StatusCode}" : root.ToString();

		if (raw.Length > 300) {
			raw = raw[..300];
		}

		if (root.ValueKind != JsonValueKind.Object) {
			return (false, null, raw);
		}

		JsonElement scope = root.TryGetProperty("data", out JsonElement d) && (d.ValueKind == JsonValueKind.Object) ? d : root;

		if (scope.TryGetProperty("confirmation", out JsonElement c) && (c.ValueKind == JsonValueKind.Object) && c.TryGetProperty("confirmation_id", out JsonElement id)) {
			string? text = id.ValueKind == JsonValueKind.String ? id.GetString() : id.ToString();

			if (!string.IsNullOrEmpty(text) && (confirmationId == null)) {
				return (false, text, raw);
			}
		}

		bool success = (ReadNumber(root, "success", 0) == 1) || (root.TryGetProperty("wallet_info", out JsonElement w) && (w.ValueKind == JsonValueKind.Object) && (ReadNumber(w, "success", 0) == 1));

		return (success, null, raw);
	}
}
