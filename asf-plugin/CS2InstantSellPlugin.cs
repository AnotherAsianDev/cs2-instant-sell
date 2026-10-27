using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;

namespace CS2InstantSell;

[Export(typeof(IPlugin))]
internal sealed class CS2InstantSellPlugin : IBot, IBotCommand2, IBotModules {
	private readonly ConcurrentDictionary<string, SellerRunner> Runners = new(StringComparer.OrdinalIgnoreCase);

	public string Name => "CS2InstantSell";

	public Version Version => typeof(CS2InstantSellPlugin).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"[CS2 Instant Sell] v{Version} loaded. Commands: ISELL <bot> (dry run), ISELLGO <bot> (sell), ISELLSTOP <bot>, ISELLSTATUS <bot>. Enable it per bot with \"{SellerConfig.PropertyName}\": {{ \"Enabled\": true }} in the bot's config.");

		return Task.CompletedTask;
	}

	public Task OnBotInit(Bot bot) => Task.CompletedTask;

	public Task OnBotDestroy(Bot bot) {
		if (Runners.TryRemove(bot.BotName, out SellerRunner? runner)) {
			runner.Dispose();
		}

		return Task.CompletedTask;
	}

	public Task OnBotInitModules(Bot bot, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		SellerConfig? config = SellerConfig.Parse(additionalConfigProperties, out string? error);

		if (error != null) {
			bot.ArchiLogger.LogGenericError($"[CS2 Instant Sell] Could not read the \"{SellerConfig.PropertyName}\" block of this bot's config: {error}. The plugin stays disabled for this bot.");
			config = null;
		}

		if (config == null) {
			// No (valid) config block: make sure nothing runs for this bot.
			if (Runners.TryRemove(bot.BotName, out SellerRunner? old)) {
				old.Dispose();
			}

			return Task.CompletedTask;
		}

		SellerRunner runner = Runners.GetOrAdd(bot.BotName, _ => new SellerRunner(bot));
		runner.Apply(config);

		return Task.CompletedTask;
	}

	public Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		string command = args[0].ToUpperInvariant();

		if (command is not ("ISELL" or "ISELLGO" or "ISELLSTOP" or "ISELLSTATUS")) {
			return Task.FromResult<string?>(null);
		}

		if (access < EAccess.Master) {
			return Task.FromResult<string?>("Access denied: Master permission is required.");
		}

		// With no bot name given, the command applies to the bot that received it.
		HashSet<Bot>? bots = args.Length > 1 ? Bot.GetBots(args[1]) : [bot];

		if ((bots == null) || (bots.Count == 0)) {
			return Task.FromResult<string?>($"No bot found for \"{args[1]}\".");
		}

		List<string> lines = [];

		foreach (Bot target in bots.OrderBy(b => b.BotName, StringComparer.OrdinalIgnoreCase)) {
			string prefix = $"<{target.BotName}> ";

			if (!Runners.TryGetValue(target.BotName, out SellerRunner? runner) || !runner.Config.Enabled) {
				lines.Add(prefix + $"CS2 Instant Sell is not enabled for this bot (add \"{SellerConfig.PropertyName}\": {{ \"Enabled\": true }} to its config).");

				continue;
			}

			switch (command) {
				case "ISELL":
					lines.Add(prefix + (runner.TryStart(true, "dry run command") ? "Dry run started. Nothing will be sold. Follow the ASF log for the list." : "A run is already in progress. Use ISELLSTATUS or ISELLSTOP."));

					break;
				case "ISELLGO":
					lines.Add(prefix + (runner.TryStart(false, "sell command") ? "Real selling started. Follow the ASF log live. Use ISELLSTOP to stop." : "A run is already in progress. Use ISELLSTATUS or ISELLSTOP."));

					break;
				case "ISELLSTOP":
					runner.Stop();
					lines.Add(prefix + "Stop requested.");

					break;
				default:
					lines.Add(prefix + $"State: {runner.Status}. Last run: {runner.LastSummary}.");

					break;
			}
		}

		return Task.FromResult<string?>(string.Join(Environment.NewLine, lines));
	}
}
