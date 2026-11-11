# ASF plugin

A plugin for [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) that sells a bot's CS2 items at the highest buy order, on command or on a schedule.

- Compiled against **ASF 7.0.0.5** (.NET 11). ASF changes its plugin interfaces between major versions, so a plugin build only loads in the matching ASF generation. If ASF logs `Could not load type 'ArchiSteamFarm.Plugins.Interfaces...'` at startup, the plugin needs a rebuild for your ASF version.
- Using ASF 6.x? Use the plugin from the older commit/tag (`git checkout` the commit before "Port to ASF 7"), which was built for ASF 6.3.11.7.
- Per-bot on/off switch in each bot's own config.
- Everything is logged through ASF's log, so you can follow it live in the ASF console and in the ASF-ui Log page.
- Accepts the market confirmations itself using ASF's 2FA (optional).

## Install

1. Copy `asf-plugin/dist/CS2InstantSell.dll` to a folder `plugins/CS2InstantSell/` inside your ASF folder (next to `ArchiSteamFarm.dll`; create `plugins` if it does not exist). With Docker, use the folder you mount as `/app/plugins`.
2. Add the `CS2InstantSell` block to the config of every bot that should be allowed to sell (see below).
3. Restart ASF. The log must show `CS2InstantSell has been loaded successfully`.

## Enable per bot

Add the block **inside the outer braces** of the bot's config file, with a comma after the previous entry:

```json
{
	"Enabled": true,
	"SteamLogin": "...",
	"SteamPassword": "...",

	"CS2InstantSell": {
		"Enabled": true,
		"MinPayout": 0.01,
		"MaxDropPercent": 20,
		"NoDropLimitBelow": 0.50,
		"AutoConfirm": true,
		"MaxItemsPerRun": 3,
		"Keep": [ "Dragon Lore", "Souvenir" ],
		"AutoRunHours": 6,
		"AutoRunTimes": [],
		"BuyTf2Keys": false,
		"Tf2KeyMaxPrice": 2.50,
		"Tf2KeyKeepBalance": 0,
		"Tf2KeyMaxPerRun": 0
	}
}
```

Bots without this block, or with `"Enabled": false` inside it, are never touched. Changes take effect when you save the file (ASF reloads the config and the plugin logs its status again). A run that is already in progress keeps the settings it started with.

If you edit bot configs with ASF-ui, check the file afterwards: this plugin has not been verified against ASF-ui's config editor.

### Settings

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Master switch for this bot. |
| `MinPayout` | `0.01` | Skip items that would pay out less than this after fees (wallet currency). |
| `MaxDropPercent` | `20` | Do not sell further down the buy-order list than this many percent below the highest buy order. |
| `NoDropLimitBelow` | `0.50` | Items whose top buy order is below this price ignore `MaxDropPercent`. |
| `AutoConfirm` | `true` | Accept the market confirmations with ASF's 2FA. Needs 2FA imported into ASF for that bot. |
| `MaxItemsPerRun` | `0` | Safety cap of items sold per run. `0` means no cap. |
| `Keep` | `[]` | Texts; an item whose market name contains one of them is never sold (case-insensitive). |
| `AutoRunHours` | `0` | Real-sell automatically every N hours (minimum 1, `0` = off). Good values are 6 to 12: new items (drops, unlocked trade holds) appear slowly, and hourly runs only add Steam requests. |
| `AutoRunTimes` | `[]` | Real-sell automatically every day at these fixed times, 24h `"HH:mm"` in the local time of the machine running ASF, e.g. `[ "04:00" ]` or `[ "04:00", "16:30" ]`. Invalid entries are ignored with a warning. Can be combined with `AutoRunHours`; runs never overlap. |
| `BuyTf2Keys` | `false` | After each real run, spend the wallet balance on TF2 keys (see below). |
| `Tf2KeyMaxPrice` | `0` | **Required for buying.** The most you accept to pay per key, fees included, in the wallet currency (e.g. `2.50`). If the cheapest key costs more, nothing is bought. `0` = nothing is bought. |
| `Tf2KeyKeepBalance` | `0` | Wallet balance that is never spent on keys. |
| `Tf2KeyMaxPerRun` | `0` | Optional cap of keys per run. `0` = no cap, buy as many as the balance allows. |

With `AutoRunTimes`, runs happen at those clock times every day (the log shows the next run time when the config loads). With `AutoRunHours`, the first run happens 10 minutes after ASF starts (or after the bot config is reloaded), then every N hours. A run is skipped if the bot is not logged in, or if another run is still going.

**Docker and time zones:** `AutoRunTimes` uses the clock of the machine or container running ASF, which in Docker is usually UTC. Set the time zone on the ASF service, for example `environment: - TZ=Europe/Zurich` in `docker-compose.yml`, then recreate the container. The log line "Next run: ..." shows the time the plugin expects.

## Buying TF2 keys

With `BuyTf2Keys: true` and `Tf2KeyMaxPrice` set, every real run (command or scheduled) ends like this: the CS2 items are sold and confirmed, the plugin waits 10 seconds for the money to arrive, then it repeatedly buys the **cheapest "Mann Co. Supply Crate Key" listing** on the Steam Community Market (TF2, app 440) as long as

- the wallet balance minus `Tf2KeyKeepBalance` covers the price,
- the price is at most `Tf2KeyMaxPrice`,
- `Tf2KeyMaxPerRun` (if set) is not reached.

The price is read again before every key. After each purchase the plugin checks that the wallet balance really dropped, and it stops at the first anything-unexpected (it never retries a purchase blindly). The keys land in the bot's TF2 inventory. `!isell <bot>` (dry run) also shows the cheapest key price and how many keys the current balance would buy, without buying.

Notes:

- Money already in the wallet is spent too, not only the proceeds of this run. Use `Tf2KeyKeepBalance` to hold some back.
- Steam may ask for a mobile confirmation of the purchase. With `AutoConfirm` on and ASF 2FA imported, the plugin accepts it.
- Steam's purchase endpoint is undocumented. Test it first: dry run, then `Tf2KeyMaxPerRun: 1` for the first real run.

## Commands

Need **Master** access. They work in the ASF console, the ASF-ui command box, the IPC API and Steam chat. `<bot>` can be a bot name, several separated by commas, or omitted (then the bot that received the command is used).

| Command | Effect |
|---|---|
| `!isell <bot>` | **Dry run.** Reads inventory and buy orders and logs what it would sell and the expected total. Sells nothing. |
| `!isellgo <bot>` | **Real sell.** |
| `!isellstop <bot>` | Stops a running job after the current item. |
| `!isellstatus <bot>` | Shows the state and the result of the last run. |

The commands answer immediately ("started"). The work runs in the background and reports in the log.

## Recommended first run

1. Run `!isell <bot>` and read the log.
2. Keep `"MaxItemsPerRun": 3`, run `!isellgo <bot>`, check the three sales on Steam.
3. Set `MaxItemsPerRun` to `0` for full runs.

## What the log shows

```
[CS2 Instant Sell] === DRY RUN (nothing will be sold) started (dry run command) ===
[CS2 Instant Sell] Inventory: 332 items. Skipped: 0 not marketable/tradable, 2 on your Keep list, 0 recently refused by Steam. 162 different items to price.
[CS2 Instant Sell] [1/162] Kilowatt Case: 28/31 sellable now, you get about 24.10 CHF (first 0.86 CHF each).
[CS2 Instant Sell] [2/162] Some Skin (Field-Tested): no buy orders, skipped.
[CS2 Instant Sell] Plan: 410 items from 120 different items for about 312.40 CHF (after Steam fees).
```

During a real run: `SOLD [n] <item> for <price>`, `FAILED <item>: <reason>`, `buy orders changed, only X of Y can still sell instantly`, and the confirmation result.

## How many requests it makes

- Per run: 1 inventory request (per 2000 items), 1 market page request for the wallet settings, then **one buy-order request per different item** (not per copy), plus **one listing request per sold item** (1.5 s apart).
- A real run prices and sells item by item, so each item costs one buy-order request, not two. The dry run (`!isell`) prices everything first to show the plan.
- Items with no usable buy order are remembered for 3 hours and skipped without any request by real runs. A dry run always checks them again.
- Key buying adds one listing request per key bought (plus one more for the final check).

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Plugin not listed at startup | The DLL must be in `plugins/CS2InstantSell/` (a subfolder). If ASF was updated, the plugin may need a rebuild against the new version. |
| `Could not read the "CS2InstantSell" block` | JSON error in the block (wrong type, missing comma, block outside the outer braces). The message names the property. |
| `is not enabled for this bot` | Missing block or `"Enabled": false`. |
| `The bot is not connected to Steam` | The bot is not logged in (offline, waiting for 2FA, paused login). |
| `Could not read the wallet/market page` | The market is unavailable for the account (limits, no market access). With a known wallet currency the plugin falls back to standard fees and warns. |
| `Unexpected buy-order reply for …` | Steam changed the orderbook reply. The first 200 characters are logged; compare with [how it works](how-it-works.md). |
| `FAILED … trad…` / `hold` | Usually a trade-locked item. It is skipped for 12 hours. |
| `rate limit` | Normal; the plugin waits and continues. |
| `Could not confirm automatically` | The bot has no ASF 2FA. Confirm in the Steam app or use ASF's `2FAOK` command. |

## Building from source

Requirements: .NET 11 SDK and a folder with the ASF files of the version you run (the generic ASF release zip is enough: it contains `ArchiSteamFarm.dll`, `SteamKit2.dll`, `AngleSharp.dll`, `System.Composition.AttributedModel.dll`).

```bash
cd asf-plugin
dotnet build -c Release -p:AsfDir=/path/to/ASF -o out
# result: out/CS2InstantSell.dll
```

Build against the same ASF version you run. After an ASF update that changes plugin interfaces, adjust the code and rebuild.

Run the logic tests:

```bash
cd tests/PricingTests
dotnet run -c Release
```

## Source overview

- `CS2InstantSellPlugin.cs`: plugin entry, per-bot config handling, commands.
- `SellerConfig.cs`: the `CS2InstantSell` config block.
- `SellerRunner.cs`: one run: wallet info, inventory, pricing, selling, confirmations, logging.
- `Pricing.cs`: fee math and price-ladder planning (pure functions, covered by the tests).
