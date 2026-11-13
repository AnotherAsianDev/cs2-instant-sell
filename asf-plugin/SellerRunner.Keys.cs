using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam.Integration;
using ArchiSteamFarm.Web.Responses;
using SteamKit2;

namespace CS2InstantSell;

// Optional: turn wallet funds into TF2 keys. For each key a buy order is placed at exactly the current lowest sell price,
// which Steam fills immediately. The wallet balance is checked to confirm the key was really paid, otherwise the order is cancelled.
internal sealed partial class SellerRunner {
	private const uint KeyAppID = 440;
	private const string KeyName = "Mann Co. Supply Crate Key";

	private Uri KeyPage => new(ArchiWebHandler.SteamCommunityURL, $"/market/listings/{KeyAppID}/{Uri.EscapeDataString(KeyName)}");

	// Returns a short text for the run summary ("" if nothing was bought or the feature is off).
	private async Task<string> BuyKeysAsync(bool dryRun, bool waitForSales, WalletInfo wallet, Func<long, string> money, CancellationToken ct) {
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

		if (!dryRun && waitForSales) {
			// Give Steam a moment to credit the money of the sales that just filled.
			await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
		}

		// Read the balance fresh from Steam. ASF's cached balance does not notice when pending funds are released.
		(long balance, long pending) = await ReadBalanceAsync(ct).ConfigureAwait(false);
		Info(tag + $"wallet balance {money(balance)}{(pending > 0 ? $" (plus {money(pending)} pending, not spendable yet)" : "")}, keeping {money(keep)}, price limit {money(maxPrice)} per key, {maxKeysText}.");

		int bought = 0;
		long spent = 0;

		while (bought < maxKeys) {
			ct.ThrowIfCancellationRequested();

			long ask = await GetKeyAskAsync(wallet, ct).ConfigureAwait(false);

			if (ask <= 0) {
				Warn(tag + "could not read the current key price, stopping.");

				break;
			}

			if (ask > maxPrice) {
				Info(tag + $"cheapest key costs {money(ask)}, above your limit of {money(maxPrice)}. Not buying.");

				break;
			}

			long available = balance - keep;

			if (available < ask) {
				Info(tag + $"cheapest key costs {money(ask)}, only {money(Math.Max(0, available))} available above the kept balance. Done.");

				break;
			}

			if (dryRun) {
				long count = Math.Min(maxKeys, available / ask);
				Info(tag + $"DRY RUN: cheapest key {money(ask)}; with the current balance about {count} key(s) would be bought (price checked again for every key).");

				break;
			}

			long before = balance;
			(bool ok, string message) = await BuyOneKeyAsync(ask, wallet, before, ct).ConfigureAwait(false);

			if (!ok) {
				// Do not retry blindly: a wrong guess here would cost money.
				Warn(tag + $"purchase failed, stopping. {message}");

				break;
			}

			bought++;
			balance = LastBalance;
			spent += before - balance;
			Info(tag + $"BOUGHT [{bought}] {KeyName} for {money(before - balance)}. Wallet now {money(balance)}.");

			await Task.Delay(2000, ct).ConfigureAwait(false);
		}

		if (bought > 0) {
			Info(tag + $"bought {bought} key(s) for {money(spent)} in total. They are in the bot's TF2 inventory.");

			return $", {bought} TF2 key(s) bought for {money(spent)}";
		}

		return "";
	}

	// Lowest sell price of the key (what a buyer pays, in minor units), or 0 if it could not be read.
	private async Task<long> GetKeyAskAsync(WalletInfo wallet, CancellationToken ct) {
		string qp = JsonSerializer.Serialize(new object[] { KeyAppID, KeyName });
		Uri url = new(ArchiWebHandler.SteamCommunityURL, $"/market/orderbook?q=Load&qp={Uri.EscapeDataString(qp)}&cc={Uri.EscapeDataString(wallet.Country)}&l=english&currency={wallet.CurrencyId}");
		KeyValuePair<string, string>[] headers = [new("x-valve-request-type", "queryAction")];

		for (int attempt = 1; attempt <= 3; attempt++) {
			ct.ThrowIfCancellationRequested();

			ObjectResponse<JsonElement>? response = await Bot.ArchiWebHandler.UrlGetToJsonObjectWithSession<JsonElement>(url, headers, KeyPage, KeepErrors, cancellationToken: ct).ConfigureAwait(false);

			if (response == null) {
				await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct).ConfigureAwait(false);

				continue;
			}

			if (response.StatusCode == HttpStatusCode.TooManyRequests) {
				Warn("TF2 keys: Steam rate limit reached, waiting 30 s...");
				await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

				continue;
			}

			long ask = ParseLowestAsk(response.Content);

			if (ask <= 0) {
				string raw = response.Content.ToString();
				Warn($"TF2 keys: unexpected price reply (HTTP {(int) response.StatusCode}): {(raw.Length > 200 ? raw[..200] + "..." : raw)}");
			}

			return ask;
		}

		return 0;
	}

	private static long ParseLowestAsk(JsonElement root) {
		JsonElement data = root;

		for (int i = 0; i < 2; i++) {
			if ((data.ValueKind == JsonValueKind.Object) && data.TryGetProperty("data", out JsonElement inner) && (inner.ValueKind == JsonValueKind.Object)) {
				data = inner;
			}
		}

		if (data.ValueKind != JsonValueKind.Object) {
			return 0;
		}

		long best = 0;

		if (data.TryGetProperty("rgCompactSellOrders", out JsonElement array) && (array.ValueKind == JsonValueKind.Array)) {
			int index = 0;

			foreach (JsonElement item in array.EnumerateArray()) {
				// Flat list: price, quantity, price, quantity, ...
				if ((index++ % 2) != 0) {
					continue;
				}

				long price = item.ValueKind == JsonValueKind.Number ? item.GetInt64() : (item.ValueKind == JsonValueKind.String) && long.TryParse(item.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;

				if ((price > 0) && ((best == 0) || (price < best))) {
					best = price;
				}
			}
		}

		return best > 0 ? best : (long) ReadNumber(data, "amtMinSellOrder", 0);
	}

	private async Task<(bool Success, string Message)> BuyOneKeyAsync(long price, WalletInfo wallet, long balanceBefore, CancellationToken ct) {
		IDictionary<string, string> data = new Dictionary<string, string> {
			["currency"] = wallet.CurrencyId.ToString(CultureInfo.InvariantCulture),
			["appid"] = KeyAppID.ToString(CultureInfo.InvariantCulture),
			["market_hash_name"] = KeyName,
			["price_total"] = price.ToString(CultureInfo.InvariantCulture),
			["quantity"] = "1",
			["billing_state"] = "",
			["save_my_address"] = "0"
		};

		bool confirmedRound = false;
		(JsonElement root, string raw) = await PostBuyOrderAsync(data, ct).ConfigureAwait(false);

		// Steam first answers with success 22 + a confirmation id: the purchase must be confirmed in the mobile app,
		// then the same request is sent again together with that id.
		if ((root.ValueKind == JsonValueKind.Object) && (ReadNumber(root, "success", 0) == 22) && root.TryGetProperty("confirmation", out JsonElement conf) && (conf.ValueKind == JsonValueKind.Object) && conf.TryGetProperty("confirmation_id", out JsonElement confId)) {
			if (!Config.AutoConfirm) {
				return (false, "The purchase needs a mobile confirmation, but AutoConfirm is off.");
			}

			string? confirmationId = confId.ValueKind == JsonValueKind.String ? confId.GetString() : confId.ToString();

			await ConfirmAsync(EMobileConfirmationType.MarketPurchase).ConfigureAwait(false);
			await Task.Delay(3000, ct).ConfigureAwait(false);

			data["confirmation"] = confirmationId ?? "0";
			confirmedRound = true;
			(root, raw) = await PostBuyOrderAsync(data, ct).ConfigureAwait(false);
		}

		if ((root.ValueKind != JsonValueKind.Object) || (ReadNumber(root, "success", 0) != 1)) {
			// After a confirmation Steam's reply code is not reliable, so the wallet balance decides.
			if (confirmedRound && await WaitForPaymentAsync(balanceBefore, 12, ct).ConfigureAwait(false)) {
				return (true, "");
			}

			return (false, "Steam's reply: " + raw);
		}

		string? orderId = null;

		if (root.TryGetProperty("buy_orderid", out JsonElement id)) {
			orderId = id.ValueKind == JsonValueKind.String ? id.GetString() : id.ToString();
		}

		// The order is placed exactly at the lowest sell price, so it should fill at once. The wallet balance is the proof.
		if (await WaitForPaymentAsync(balanceBefore, 12, ct).ConfigureAwait(false)) {
			return (true, "");
		}

		// Some accounts need the purchase confirmed in the mobile app.
		if (Config.AutoConfirm) {
			await ConfirmAsync(EMobileConfirmationType.MarketPurchase).ConfigureAwait(false);

			if (await WaitForPaymentAsync(balanceBefore, 12, ct).ConfigureAwait(false)) {
				return (true, "");
			}
		}

		// Not filled (for example the price moved up in the meantime): do not leave an open order behind.
		if (!string.IsNullOrEmpty(orderId)) {
			IDictionary<string, string> cancel = new Dictionary<string, string> { ["buy_orderid"] = orderId };
			await Bot.ArchiWebHandler.UrlPostToJsonObjectWithSession<JsonElement>(new Uri(ArchiWebHandler.SteamCommunityURL, "/market/cancelbuyorder/"), data: cancel, referer: KeyPage, requestOptions: KeepErrors, maxTries: 1, cancellationToken: ct).ConfigureAwait(false);

			return (false, $"The buy order (id {orderId}) was accepted but the wallet balance did not drop, so it was cancelled. Steam's reply: {raw}");
		}

		return (false, "The buy order was accepted but the wallet balance did not drop. Check your market history. Steam's reply: " + raw);
	}

	private async Task<(JsonElement Root, string Raw)> PostBuyOrderAsync(IDictionary<string, string> data, CancellationToken ct) {
		Uri url = new(ArchiWebHandler.SteamCommunityURL, "/market/createbuyorder/");

		// maxTries: 1 on purpose, a blind retry could buy twice.
		ObjectResponse<JsonElement>? response = await Bot.ArchiWebHandler.UrlPostToJsonObjectWithSession<JsonElement>(url, data: data, referer: KeyPage, requestOptions: KeepErrors, maxTries: 1, cancellationToken: ct).ConfigureAwait(false);

		if (response == null) {
			return (default, "no response from Steam");
		}

		JsonElement root = response.Content;
		string raw = root.ValueKind == JsonValueKind.Undefined ? $"HTTP {(int) response.StatusCode}" : root.ToString();

		return (root, raw.Length > 300 ? raw[..300] : raw);
	}

	private long LastBalance;

	// Spendable wallet balance and pending (delayed) balance in minor units, read from the market page. Falls back to ASF's cached value.
	private async Task<(long Balance, long Pending)> ReadBalanceAsync(CancellationToken ct) {
		using HtmlDocumentResponse? response = await Bot.ArchiWebHandler.UrlGetToHtmlDocumentWithSession(new Uri(ArchiWebHandler.SteamCommunityURL, "/market/"), cancellationToken: ct).ConfigureAwait(false);
		string? html = response?.Content?.Source.Text;
		Match match = string.IsNullOrEmpty(html) ? Match.Empty : WalletRegex().Match(html);

		if (match.Success) {
			try {
				using JsonDocument doc = JsonDocument.Parse(match.Groups[1].Value);

				if (doc.RootElement.TryGetProperty("wallet_balance", out _)) {
					return ((long) ReadNumber(doc.RootElement, "wallet_balance", 0), (long) ReadNumber(doc.RootElement, "wallet_delayed_balance", 0));
				}
			} catch (JsonException) {
				// Fall through to the cached value.
			}
		}

		Warn("TF2 keys: could not read the wallet balance from the market page, using ASF's cached balance (it may be out of date).");

		return (Bot.WalletBalance, 0);
	}

	// True once the (fresh) wallet balance is below balanceBefore, i.e. the purchase was really paid.
	private async Task<bool> WaitForPaymentAsync(long balanceBefore, int seconds, CancellationToken ct) {
		for (int waited = 0; waited <= seconds; waited += 3) {
			(long balance, _) = await ReadBalanceAsync(ct).ConfigureAwait(false);
			LastBalance = balance;

			if (balance < balanceBefore) {
				return true;
			}

			await Task.Delay(3000, ct).ConfigureAwait(false);
		}

		return false;
	}
}
