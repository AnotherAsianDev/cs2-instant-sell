using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

		// Each round: read the price ladder, work out how many keys the balance covers, order them all at once,
		// then read the balance again and repeat until there is nothing left to buy.
		for (int round = 1; (bought < maxKeys) && (round <= 15); round++) {
			ct.ThrowIfCancellationRequested();

			if (round > 1) {
				(balance, _) = await ReadBalanceAsync(ct).ConfigureAwait(false);
			}

			List<(long Price, long Quantity)> asks = await GetKeyAsksAsync(wallet, ct).ConfigureAwait(false);

			if (asks.Count == 0) {
				Warn(tag + "could not read the current key price, stopping.");

				break;
			}

			long available = balance - keep;
			(int count, long limit) = PlanKeys(asks, maxPrice, available, maxKeys == int.MaxValue ? int.MaxValue : maxKeys - bought);

			if (count == 0) {
				Info(tag + (asks[0].Price > maxPrice ? $"cheapest key costs {money(asks[0].Price)}, above your limit of {money(maxPrice)}. Not buying." : $"cheapest key costs {money(asks[0].Price)}, only {money(Math.Max(0, available))} available above the kept balance. Done."));

				break;
			}

			if (dryRun) {
				Info(tag + $"DRY RUN: cheapest key {money(asks[0].Price)}; with the current balance {count} key(s) would be ordered at once (up to {money(limit)} each).");

				break;
			}

			Info(tag + $"round {round}: ordering {count} key(s), up to {money(limit)} each ({money(limit * count)} reserved).");

			long before = balance;
			(bool ok, string message, string? orderId) = await BuyBatchAsync(count, limit, wallet, before, ct).ConfigureAwait(false);

			if (!ok) {
				// Do not retry blindly: a wrong guess here would cost money.
				Warn(tag + $"purchase failed, stopping. {message}");

				break;
			}

			// Let Steam finish filling the order, then read the final balance.
			await Task.Delay(6000, ct).ConfigureAwait(false);
			(balance, _) = await ReadBalanceAsync(ct).ConfigureAwait(false);
			long paid = before - balance;

			int? got = string.IsNullOrEmpty(orderId) ? null : await GetBoughtAsync(orderId, ct).ConfigureAwait(false);

			// Close whatever is left of the order, otherwise Steam refuses the next one.
			if (!string.IsNullOrEmpty(orderId)) {
				await CancelOrderAsync(orderId, ct).ConfigureAwait(false);
			}

			int keys = got ?? (int) Math.Max(1, Math.Round((double) paid / asks[0].Price));
			bought += keys;
			spent += paid;
			Info(tag + $"BOUGHT {(got == null ? "about " : "")}{keys} key(s) for {money(paid)} in round {round}. Wallet now {money(balance)}.");

			if (got is 0) {
				Warn(tag + "Steam reports nothing filled although the balance dropped, stopping to be safe.");

				break;
			}

			await Task.Delay(2000, ct).ConfigureAwait(false);
		}

		if (bought > 0) {
			Info(tag + $"bought {bought} key(s) for {money(spent)} in total. They are in the bot's TF2 inventory.");

			return $", {bought} TF2 key(s) bought for {money(spent)}";
		}

		return "";
	}

	// Sell price ladder of the key (what a buyer pays, in minor units), cheapest first. Empty if it could not be read.
	private async Task<List<(long Price, long Quantity)>> GetKeyAsksAsync(WalletInfo wallet, CancellationToken ct) {
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

			List<(long Price, long Quantity)> asks = ParseAsks(response.Content);

			if (asks.Count == 0) {
				string raw = response.Content.ToString();
				Warn($"TF2 keys: unexpected price reply (HTTP {(int) response.StatusCode}): {(raw.Length > 200 ? raw[..200] + "..." : raw)}");
			}

			return asks;
		}

		return [];
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

	private static long ToLong(JsonElement item) => item.ValueKind == JsonValueKind.Number ? item.GetInt64() : (item.ValueKind == JsonValueKind.String) && long.TryParse(item.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;

	private static List<(long Price, long Quantity)> ParseAsks(JsonElement root) {
		JsonElement data = root;

		for (int i = 0; i < 2; i++) {
			if ((data.ValueKind == JsonValueKind.Object) && data.TryGetProperty("data", out JsonElement inner) && (inner.ValueKind == JsonValueKind.Object)) {
				data = inner;
			}
		}

		SortedDictionary<long, long> levels = [];

		if ((data.ValueKind == JsonValueKind.Object) && data.TryGetProperty("rgCompactSellOrders", out JsonElement array) && (array.ValueKind == JsonValueKind.Array)) {
			long? price = null;

			// Flat list: price, quantity, price, quantity, ...
			foreach (JsonElement item in array.EnumerateArray()) {
				if (price == null) {
					price = ToLong(item);

					continue;
				}

				long quantity = ToLong(item);

				if ((price > 0) && (quantity > 0)) {
					levels[price.Value] = levels.GetValueOrDefault(price.Value) + quantity;
				}

				price = null;
			}
		}

		if ((levels.Count == 0) && (data.ValueKind == JsonValueKind.Object)) {
			long ask = (long) ReadNumber(data, "amtMinSellOrder", 0);

			if (ask > 0) {
				// No ladder in the reply: all we know is the lowest price, so plan for a single unit.
				levels[ask] = 1;
			}
		}

		return levels.Select(static x => (x.Key, x.Value)).ToList();
	}

	// How many keys to order at once and the highest price per key to allow. The order reserves limit * count, so it must fit the balance.
	internal static (int Count, long Limit) PlanKeys(List<(long Price, long Quantity)> asks, long maxPrice, long available, int maxCount) {
		int bestCount = 0;
		long bestLimit = 0;
		long cumulative = 0;

		foreach ((long price, long quantity) in asks) {
			if (price > maxPrice) {
				break;
			}

			cumulative += quantity;

			long count = Math.Min(Math.Min(cumulative, available / price), maxCount);

			if (count > bestCount) {
				bestCount = (int) count;
				bestLimit = price;
			}
		}

		return (bestCount, bestLimit);
	}

	private async Task<(bool Success, string Message, string? OrderID)> BuyBatchAsync(int quantity, long limit, WalletInfo wallet, long balanceBefore, CancellationToken ct) {
		IDictionary<string, string> data = new Dictionary<string, string> {
			["currency"] = wallet.CurrencyId.ToString(CultureInfo.InvariantCulture),
			["appid"] = KeyAppID.ToString(CultureInfo.InvariantCulture),
			["market_hash_name"] = KeyName,
			["price_total"] = (limit * quantity).ToString(CultureInfo.InvariantCulture),
			["quantity"] = quantity.ToString(CultureInfo.InvariantCulture),
			["billing_state"] = "",
			["save_my_address"] = "0"
		};

		bool confirmedRound = false;
		(JsonElement root, string raw) = await PostBuyOrderAsync(data, ct).ConfigureAwait(false);

		// Steam answers success 30 while the previous key's order is still being closed. Nothing was bought then, so waiting and trying again is safe.
		for (int wait = 1; (wait <= 6) && (root.ValueKind == JsonValueKind.Object) && (ReadNumber(root, "success", 0) == 30); wait++) {
			Info("TF2 keys: Steam still has the previous buy order open, waiting 5 s...");
			await Task.Delay(5000, ct).ConfigureAwait(false);
			(root, raw) = await PostBuyOrderAsync(data, ct).ConfigureAwait(false);
		}

		// Steam first answers with success 22 + a confirmation id: the purchase must be confirmed in the mobile app,
		// then the same request is sent again together with that id.
		if ((root.ValueKind == JsonValueKind.Object) && (ReadNumber(root, "success", 0) == 22) && root.TryGetProperty("confirmation", out JsonElement conf) && (conf.ValueKind == JsonValueKind.Object) && conf.TryGetProperty("confirmation_id", out JsonElement confId)) {
			if (!Config.AutoConfirm) {
				return (false, "The purchase needs a mobile confirmation, but AutoConfirm is off.", null);
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
				return (true, "", OrderIdOf(root));
			}

			return (false, "Steam's reply: " + raw, null);
		}

		string? orderId = OrderIdOf(root);

		// The order is placed exactly at the lowest sell price, so it should fill at once. The wallet balance is the proof.
		if (await WaitForPaymentAsync(balanceBefore, 12, ct).ConfigureAwait(false)) {
			return (true, "", orderId);
		}

		// Some accounts need the purchase confirmed in the mobile app.
		if (Config.AutoConfirm) {
			await ConfirmAsync(EMobileConfirmationType.MarketPurchase).ConfigureAwait(false);

			if (await WaitForPaymentAsync(balanceBefore, 12, ct).ConfigureAwait(false)) {
				return (true, "", orderId);
			}
		}

		// Not filled (for example the price moved up in the meantime): do not leave an open order behind.
		if (!string.IsNullOrEmpty(orderId)) {
			await CancelOrderAsync(orderId, ct).ConfigureAwait(false);

			return (false, $"The buy order (id {orderId}) was accepted but the wallet balance did not drop, so it was cancelled. Steam's reply: {raw}", null);
		}

		return (false, "The buy order was accepted but the wallet balance did not drop. Check your market history. Steam's reply: " + raw, null);
	}

	private static string? OrderIdOf(JsonElement root) => (root.ValueKind == JsonValueKind.Object) && root.TryGetProperty("buy_orderid", out JsonElement id) ? (id.ValueKind == JsonValueKind.String ? id.GetString() : id.ToString()) : null;

	private async Task CancelOrderAsync(string orderId, CancellationToken ct) {
		IDictionary<string, string> cancel = new Dictionary<string, string> { ["buy_orderid"] = orderId };

		// Best effort: a fully filled order cannot be cancelled, which is fine.
		await Bot.ArchiWebHandler.UrlPostToJsonObjectWithSession<JsonElement>(new Uri(ArchiWebHandler.SteamCommunityURL, "/market/cancelbuyorder/"), data: cancel, referer: KeyPage, requestOptions: KeepErrors, maxTries: 1, cancellationToken: ct).ConfigureAwait(false);
	}

	// Number of keys the order has really filled so far, or null if Steam's status could not be read.
	private async Task<int?> GetBoughtAsync(string orderId, CancellationToken ct) {
		string? sessionId = Bot.ArchiWebHandler.WebBrowser.CookieContainer.GetAllCookies().Cast<Cookie>().FirstOrDefault(static c => c.Name == "sessionid" && c.Domain.Contains("steamcommunity", StringComparison.Ordinal))?.Value;

		if (string.IsNullOrEmpty(sessionId)) {
			return null;
		}

		Uri url = new(ArchiWebHandler.SteamCommunityURL, $"/market/getbuyorderstatus/?sessionid={Uri.EscapeDataString(sessionId)}&buy_orderid={Uri.EscapeDataString(orderId)}");
		ObjectResponse<JsonElement>? response = await Bot.ArchiWebHandler.UrlGetToJsonObjectWithSession<JsonElement>(url, referer: KeyPage, requestOptions: KeepErrors, maxTries: 1, cancellationToken: ct).ConfigureAwait(false);

		if ((response == null) || (response.Content.ValueKind != JsonValueKind.Object) || (ReadNumber(response.Content, "success", 0) != 1) || !response.Content.TryGetProperty("quantity", out _)) {
			return null;
		}

		return (int) (ReadNumber(response.Content, "quantity", 0) - ReadNumber(response.Content, "quantity_remaining", 0));
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
