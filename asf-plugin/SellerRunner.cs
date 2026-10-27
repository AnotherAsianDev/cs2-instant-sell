using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Data;
using ArchiSteamFarm.Steam.Integration;
using ArchiSteamFarm.Web;
using ArchiSteamFarm.Web.Responses;
using SteamKit2;

namespace CS2InstantSell;

// Does the actual work for one bot: reads the inventory, prices every item from the buy-order book, lists them at the buy-order price
// (which makes them sell instantly) and optionally confirms the listings with ASF's own 2FA. Everything is written to the bot's ASF log.
internal sealed partial class SellerRunner : IDisposable {
	private const uint AppID = 730;
	private const ulong ContextID = 2;

	private const WebBrowser.ERequestOptions KeepErrors = WebBrowser.ERequestOptions.ReturnClientErrors | WebBrowser.ERequestOptions.ReturnServerErrors | WebBrowser.ERequestOptions.AllowInvalidBodyOnErrors;

	private readonly Bot Bot;
	private readonly SemaphoreSlim Gate = new(1, 1);

	// Asset IDs that Steam refused to list (usually trade-locked), so later runs don't retry them right away.
	private readonly Dictionary<ulong, DateTime> Rejected = [];

	private CancellationTokenSource? Cts;
	private Timer? AutoTimer;

	internal SellerConfig Config { get; private set; } = new();
	internal string Status { get; private set; } = "idle";
	internal string LastSummary { get; private set; } = "no run yet";

	internal SellerRunner(Bot bot) => Bot = bot ?? throw new ArgumentNullException(nameof(bot));

	public void Dispose() {
		AutoTimer?.Dispose();
		Cts?.Cancel();
		Cts?.Dispose();
		Gate.Dispose();
	}

	private void Info(string message) => Bot.ArchiLogger.LogGenericInfo($"[CS2 Instant Sell] {message}");
	private void Warn(string message) => Bot.ArchiLogger.LogGenericWarning($"[CS2 Instant Sell] {message}");

	internal void Apply(SellerConfig config) {
		Config = config;

		AutoTimer?.Dispose();
		AutoTimer = null;

		List<TimeSpan> times = [];

		foreach (string text in config.AutoRunTimes) {
			if (TimeSpan.TryParseExact(text?.Trim(), @"h\:mm", System.Globalization.CultureInfo.InvariantCulture, out TimeSpan t) && (t >= TimeSpan.Zero) && (t < TimeSpan.FromHours(24))) {
				times.Add(t);
			} else {
				Warn($"Ignoring invalid AutoRunTimes entry \"{text}\" (use 24h format like \"04:00\").");
			}
		}

		if (config.Enabled && (times.Count > 0)) {
			times.Sort();
			ScheduleNext(times);
			Info($"Enabled. Automatic real selling daily at {string.Join(", ", times.Select(t => t.ToString(@"hh\:mm")))} (local time). Next run: {NextRun(times):yyyy-MM-dd HH:mm}.");
		} else if (config.Enabled && (config.AutoRunHours > 0)) {
			TimeSpan period = TimeSpan.FromHours(config.AutoRunHours);

			// First automatic run 10 minutes after the config is loaded, to give the bot time to log in.
			AutoTimer = new Timer(_ => {
					if (!TryStart(false, "automatic run")) {
						Info("Automatic run skipped (a run is already in progress).");
					}
				}, null, TimeSpan.FromMinutes(10), period
			);

			Info($"Enabled. Automatic real selling every {config.AutoRunHours} h (first run in 10 min).");
		} else {
			Info(config.Enabled ? "Enabled. Sells only when you use the ISELLGO command." : "Disabled in this bot's config.");
		}
	}

	private static DateTime NextRun(List<TimeSpan> times) {
		DateTime now = DateTime.Now;
		DateTime best = DateTime.MaxValue;

		foreach (TimeSpan t in times) {
			DateTime candidate = now.Date + t;

			if (candidate <= now) {
				candidate = candidate.AddDays(1);
			}

			if (candidate < best) {
				best = candidate;
			}
		}

		return best;
	}

	// One-shot timer that re-arms itself after each firing, so DST changes and long sleeps never drift.
	private void ScheduleNext(List<TimeSpan> times) {
		TimeSpan wait = NextRun(times) - DateTime.Now;

		if (wait < TimeSpan.FromSeconds(1)) {
			wait = TimeSpan.FromSeconds(1);
		}

		AutoTimer?.Dispose();

		AutoTimer = new Timer(_ => {
				if (!TryStart(false, "scheduled run")) {
					Info("Scheduled run skipped (a run is already in progress).");
				}

				// Next occurrence is always later than now, so this cannot fire twice for the same slot.
				ScheduleNext(times);
			}, null, wait, Timeout.InfiniteTimeSpan
		);
	}

	internal void Stop() => Cts?.Cancel();

	// Starts a run in the background. Returns false if one is already running.
	internal bool TryStart(bool dryRun, string reason) {
		if (!Gate.Wait(0)) {
			return false;
		}

		Cts = new CancellationTokenSource();
		CancellationToken token = Cts.Token;

		_ = Task.Run(async () => {
				try {
					await RunAsync(dryRun, reason, token).ConfigureAwait(false);
				} catch (OperationCanceledException) {
					Status = "stopped";
					LastSummary = "stopped by command";
					Warn("Run stopped by command.");
				} catch (Exception e) {
					Status = "failed";
					LastSummary = "failed: " + e.Message;
					Bot.ArchiLogger.LogGenericException(e);
				} finally {
					Gate.Release();
				}
			}
		);

		return true;
	}

	private sealed record WalletInfo(int CurrencyId, string Country, FeeInfo Fee);

	private sealed class Group {
		internal required string Name { get; init; }
		internal required List<ulong> AssetIDs { get; init; }
		internal List<SaleUnit> Plan { get; set; } = [];
		internal string? Skip { get; set; }
	}

	private async Task RunAsync(bool dryRun, string reason, CancellationToken ct) {
		string mode = dryRun ? "DRY RUN (nothing will be sold)" : "REAL SELL";
		Status = dryRun ? "dry run" : "selling";
		Info($"=== {mode} started ({reason}) ===");

		if (!Bot.IsConnectedAndLoggedOn) {
			Status = "idle";
			LastSummary = "bot not logged in";
			Warn("The bot is not connected to Steam, aborting.");

			return;
		}

		SellerConfig cfg = Config;

		WalletInfo? wallet = await GetWalletAsync(ct).ConfigureAwait(false);

		if (wallet == null) {
			Status = "failed";
			LastSummary = "could not read wallet/market info";
			Warn("Could not read the wallet/market page. Is the market available for this account?");

			return;
		}

		string currency = Bot.WalletCurrency != ECurrencyCode.Invalid ? Bot.WalletCurrency.ToString() : $"currency #{wallet.CurrencyId}";
		string Money(long minorUnits) => (minorUnits / 100m).ToString("0.00", CultureInfo.InvariantCulture) + " " + currency;

		long minPayout = (long) Math.Round(cfg.MinPayout * 100m);
		long noDropBelow = (long) Math.Round(cfg.NoDropLimitBelow * 100m);

		Info("Reading CS2 inventory...");
		List<Asset> assets = [];

		await foreach (Asset asset in Bot.ArchiWebHandler.GetInventoryAsync(appID: AppID, contextID: ContextID).WithCancellation(ct).ConfigureAwait(false)) {
			assets.Add(asset);
		}

		DateTime now = DateTime.UtcNow;

		foreach (ulong expired in Rejected.Where(x => x.Value < now).Select(x => x.Key).ToList()) {
			Rejected.Remove(expired);
		}

		int notMarketable = 0, kept = 0, rejected = 0;
		Dictionary<string, Group> groups = new(StringComparer.Ordinal);

		foreach (Asset asset in assets) {
			if ((asset.Description == null) || !asset.Marketable || !asset.Tradable) {
				notMarketable++;

				continue;
			}

			string name = asset.Description.MarketHashName;

			if (cfg.Keep.Any(k => !string.IsNullOrWhiteSpace(k) && name.Contains(k, StringComparison.OrdinalIgnoreCase))) {
				kept++;

				continue;
			}

			if (Rejected.ContainsKey(asset.AssetID)) {
				rejected++;

				continue;
			}

			if (!groups.TryGetValue(name, out Group? group)) {
				group = new Group { Name = name, AssetIDs = [] };
				groups[name] = group;
			}

			group.AssetIDs.Add(asset.AssetID);
		}

		Info($"Inventory: {assets.Count} items. Skipped: {notMarketable} not marketable/tradable, {kept} on your Keep list, {rejected} recently refused by Steam. {groups.Count} different items to price.");

		// Phase 1: price everything.
		int priced = 0;

		foreach (Group group in groups.Values) {
			ct.ThrowIfCancellationRequested();

			List<(long Price, long Quantity)>? book = await GetBookAsync(group.Name, wallet, ct).ConfigureAwait(false);
			priced++;

			if (book == null) {
				group.Skip = "could not read buy orders";
				Warn($"[{priced}/{groups.Count}] {group.Name}: could not read buy orders, skipped.");

				continue;
			}

			if (book.Count == 0) {
				group.Skip = "no buy orders";
				Info($"[{priced}/{groups.Count}] {group.Name}: no buy orders, skipped.");

				continue;
			}

			group.Plan = Pricing.Plan(book, group.AssetIDs.Count, cfg.MaxDropPercent, minPayout, noDropBelow, wallet.Fee);

			if (group.Plan.Count == 0) {
				group.Skip = "payout too low";
				Info($"[{priced}/{groups.Count}] {group.Name}: top bid {Money(book[0].Price)} is too low or too far down, skipped.");

				continue;
			}

			long sum = group.Plan.Sum(u => u.SellerReceives);
			Info($"[{priced}/{groups.Count}] {group.Name}: {group.Plan.Count}/{group.AssetIDs.Count} sellable now, you get about {Money(sum)} (first {Money(group.Plan[0].SellerReceives)} each).");
		}

		List<Group> toSell = groups.Values.Where(g => g.Plan.Count > 0).ToList();
		int totalItems = toSell.Sum(g => g.Plan.Count);
		long totalMoney = toSell.Sum(g => g.Plan.Sum(u => u.SellerReceives));

		if (cfg.MaxItemsPerRun > 0 && totalItems > cfg.MaxItemsPerRun) {
			Info($"Note: MaxItemsPerRun is {cfg.MaxItemsPerRun}, only that many will be sold in this run.");
		}

		Info($"Plan: {totalItems} items from {toSell.Count} different items for about {Money(totalMoney)} (after Steam fees).");

		if (dryRun) {
			Status = "idle";
			LastSummary = $"dry run: {totalItems} items, about {Money(totalMoney)}";
			Info("=== DRY RUN finished. Use ISELLGO to sell for real. ===");

			return;
		}

		// Phase 2: sell. Buy orders are re-read right before each item so we never list into orders that are already gone.
		int sold = 0, failed = 0, budget = cfg.MaxItemsPerRun > 0 ? cfg.MaxItemsPerRun : int.MaxValue;
		long earned = 0;
		int pendingConfirm = 0;

		foreach (Group group in toSell) {
			if (budget <= 0) {
				break;
			}

			ct.ThrowIfCancellationRequested();

			List<(long Price, long Quantity)>? book = await GetBookAsync(group.Name, wallet, ct).ConfigureAwait(false);

			if (book == null) {
				Warn($"{group.Name}: could not re-check buy orders, skipped.");

				continue;
			}

			List<SaleUnit> units = Pricing.Plan(book, group.Plan.Count, cfg.MaxDropPercent, minPayout, noDropBelow, wallet.Fee);

			if (units.Count < group.Plan.Count) {
				Warn($"{group.Name}: buy orders changed, only {units.Count} of {group.Plan.Count} can still sell instantly.");
			}

			for (int i = 0; i < units.Count; i++) {
				if (budget <= 0) {
					break;
				}

				ct.ThrowIfCancellationRequested();

				ulong assetId = group.AssetIDs[i];
				(bool ok, bool needsConfirm, string? message) = await SellAsync(assetId, units[i].SellerReceives, ct).ConfigureAwait(false);

				if (ok) {
					sold++;
					budget--;
					earned += units[i].SellerReceives;

					if (needsConfirm) {
						pendingConfirm++;
					}

					Info($"SOLD [{sold}] {group.Name} for {Money(units[i].SellerReceives)}");
				} else {
					failed++;
					Warn($"FAILED {group.Name}: {message}");

					// Steam refuses locked items with a message about trading/holds. Remember them so later runs skip them.
					if (message != null && Regex.IsMatch(message, "trad|hold|restrict|cannot|not .*(sell|market)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) {
						Rejected[assetId] = DateTime.UtcNow.AddHours(12);
					}
				}

				if (cfg.AutoConfirm && (pendingConfirm >= 20)) {
					await ConfirmAsync().ConfigureAwait(false);
					pendingConfirm = 0;
				}

				await Task.Delay(1500, ct).ConfigureAwait(false);
			}
		}

		if (cfg.AutoConfirm && (pendingConfirm > 0)) {
			await ConfirmAsync().ConfigureAwait(false);
		} else if (!cfg.AutoConfirm && (sold > 0)) {
			Warn("AutoConfirm is off: confirm the listings in the Steam mobile app (or use the 2FAOK command).");
		}

		Status = "idle";
		LastSummary = $"{sold} listed, {failed} failed, about {Money(earned)}";
		Info($"=== REAL SELL finished: {LastSummary} ===");
	}

	private async Task ConfirmAsync() {
		Info("Accepting market confirmations with ASF 2FA...");

		(bool success, _, string message) = await Bot.Actions.HandleTwoFactorAuthenticationConfirmations(true, EMobileConfirmationType.MarketListing, null, true).ConfigureAwait(false);

		if (success) {
			Info("Confirmations: " + message);
		} else {
			Warn("Could not confirm automatically: " + message);
		}
	}

	[GeneratedRegex("g_rgWalletInfo\\s*=\\s*(\\{.*?\\})\\s*;", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
	private static partial Regex WalletRegex();

	private static double ReadNumber(JsonElement e, string name, double fallback) {
		if (!e.TryGetProperty(name, out JsonElement v)) {
			return fallback;
		}

		return v.ValueKind switch {
			JsonValueKind.Number => v.GetDouble(),
			JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => d,
			_ => fallback
		};
	}

	private async Task<WalletInfo?> GetWalletAsync(CancellationToken ct) {
		using HtmlDocumentResponse? response = await Bot.ArchiWebHandler.UrlGetToHtmlDocumentWithSession(new Uri(ArchiWebHandler.SteamCommunityURL, "/market/"), cancellationToken: ct).ConfigureAwait(false);

		string? html = response?.Content?.Source.Text;

		Match match = string.IsNullOrEmpty(html) ? Match.Empty : WalletRegex().Match(html);

		if (!match.Success) {
			// Fall back to the currency ASF already knows and Steam's standard fees, but say so clearly.
			if (Bot.WalletCurrency != ECurrencyCode.Invalid) {
				Warn("Could not read the wallet settings from the market page, using standard Steam fees (5% + 10%) and the bot's wallet currency instead.");

				return new WalletInfo((int) Bot.WalletCurrency, "US", new FeeInfo(0.05, 1, 0, 0.10));
			}

			return null;
		}

		using JsonDocument doc = JsonDocument.Parse(match.Groups[1].Value);
		JsonElement root = doc.RootElement;

		int currency = (int) ReadNumber(root, "wallet_currency", 0);
		string country = root.TryGetProperty("wallet_country", out JsonElement c) && (c.ValueKind == JsonValueKind.String) ? c.GetString() ?? "US" : "US";

		if (currency <= 0) {
			return null;
		}

		FeeInfo fee = new(ReadNumber(root, "wallet_fee_percent", 0.05), (long) ReadNumber(root, "wallet_fee_minimum", 1), (long) ReadNumber(root, "wallet_fee_base", 0), ReadNumber(root, "wallet_publisher_fee_percent_default", 0.1));

		return new WalletInfo(currency, country, fee);
	}

	// Returns the buy-order levels (highest price first), an empty list if there are none, or null if the request failed.
	private async Task<List<(long Price, long Quantity)>?> GetBookAsync(string marketHashName, WalletInfo wallet, CancellationToken ct) {
		string qp = JsonSerializer.Serialize(new object[] { AppID, marketHashName });
		Uri url = new(ArchiWebHandler.SteamCommunityURL, $"/market/orderbook?q=Load&qp={Uri.EscapeDataString(qp)}&cc={Uri.EscapeDataString(wallet.Country)}&l=english&currency={wallet.CurrencyId}");
		Uri referer = new(ArchiWebHandler.SteamCommunityURL, $"/market/listings/{AppID}/{Uri.EscapeDataString(marketHashName)}");

		KeyValuePair<string, string>[] headers = [new("x-valve-request-type", "queryAction")];

		for (int attempt = 1; attempt <= 4; attempt++) {
			ct.ThrowIfCancellationRequested();

			ObjectResponse<JsonElement>? response = await Bot.ArchiWebHandler.UrlGetToJsonObjectWithSession<JsonElement>(url, headers, referer, KeepErrors, cancellationToken: ct).ConfigureAwait(false);

			if (response == null) {
				await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct).ConfigureAwait(false);

				continue;
			}

			if (response.StatusCode == HttpStatusCode.TooManyRequests) {
				int wait = 20 * attempt;
				Warn($"Steam rate limit reached, waiting {wait} s...");
				await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);

				continue;
			}

			if ((int) response.StatusCode is < 200 or >= 300) {
				Warn($"Orderbook request for {marketHashName} returned HTTP {(int) response.StatusCode}.");

				return null;
			}

			await Task.Delay(250, ct).ConfigureAwait(false);

			List<(long Price, long Quantity)>? book = ParseBook(response.Content);

			if (book == null) {
				string raw = response.Content.ToString();
				Warn($"Unexpected buy-order reply for {marketHashName}: {(raw.Length > 200 ? raw[..200] + "..." : raw)}");
			}

			return book;
		}

		return null;
	}

	private static List<(long Price, long Quantity)>? ParseBook(JsonElement root) {
		JsonElement data = root;

		// Steam sometimes wraps the reply in an extra "data" object.
		for (int i = 0; i < 2; i++) {
			if ((data.ValueKind == JsonValueKind.Object) && data.TryGetProperty("data", out JsonElement inner) && (inner.ValueKind == JsonValueKind.Object)) {
				data = inner;
			}
		}

		if ((data.ValueKind != JsonValueKind.Object) || !data.TryGetProperty("rgCompactBuyOrders", out JsonElement array) || (array.ValueKind != JsonValueKind.Array)) {
			return null;
		}

		List<long> values = [];

		foreach (JsonElement item in array.EnumerateArray()) {
			if (item.ValueKind == JsonValueKind.Number) {
				values.Add(item.GetInt64());
			} else if ((item.ValueKind == JsonValueKind.String) && long.TryParse(item.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)) {
				values.Add(parsed);
			}
		}

		List<(long Price, long Quantity)> levels = [];

		for (int i = 0; i + 1 < values.Count; i += 2) {
			levels.Add((values[i], values[i + 1]));
		}

		levels.Sort((a, b) => b.Price.CompareTo(a.Price));

		return levels;
	}

	private async Task<(bool Success, bool NeedsConfirmation, string? Message)> SellAsync(ulong assetId, long price, CancellationToken ct) {
		IDictionary<string, string> data = new Dictionary<string, string> {
			["appid"] = AppID.ToString(CultureInfo.InvariantCulture),
			["contextid"] = ContextID.ToString(CultureInfo.InvariantCulture),
			["assetid"] = assetId.ToString(CultureInfo.InvariantCulture),
			["amount"] = "1",
			["price"] = price.ToString(CultureInfo.InvariantCulture)
		};

		Uri url = new(ArchiWebHandler.SteamCommunityURL, "/market/sellitem/");
		Uri referer = new(ArchiWebHandler.SteamCommunityURL, $"/profiles/{Bot.SteamID}/inventory");

		// maxTries: 1 on purpose, a blind retry of a listing could create it twice.
		ObjectResponse<JsonElement>? response = await Bot.ArchiWebHandler.UrlPostToJsonObjectWithSession<JsonElement>(url, data: data, referer: referer, requestOptions: KeepErrors, maxTries: 1, cancellationToken: ct).ConfigureAwait(false);

		if (response == null) {
			return (false, false, "no response from Steam");
		}

		if (response.StatusCode == HttpStatusCode.TooManyRequests) {
			Warn("Steam rate limit reached while listing, waiting 30 s...");
			await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

			return (false, false, "rate limited (HTTP 429), this item was not listed");
		}

		JsonElement root = response.Content;

		if (root.ValueKind != JsonValueKind.Object) {
			return (false, false, $"unexpected reply (HTTP {(int) response.StatusCode})");
		}

		bool success = root.TryGetProperty("success", out JsonElement s) && (s.ValueKind == JsonValueKind.True || (s.ValueKind == JsonValueKind.Number && s.GetInt32() != 0));
		string? message = root.TryGetProperty("message", out JsonElement m) && (m.ValueKind == JsonValueKind.String) ? m.GetString() : null;

		bool needsConfirmation = success && ((ReadNumber(root, "requires_confirmation", 0) > 0) || (ReadNumber(root, "needs_mobile_confirmation", 0) > 0) || root.TryGetProperty("needs_mobile_confirmation", out JsonElement n) && (n.ValueKind == JsonValueKind.True));

		return (success, needsConfirmation, success ? null : message ?? "refused by Steam");
	}
}
