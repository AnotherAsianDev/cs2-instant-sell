# ASF plugin

A plugin for [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) that sells a bot's CS2 items at the highest buy order, on command or on a schedule.

- Compiled against **ASF 6.3.11.7** (.NET 10).
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
		"AutoRunHours": 0
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
| `AutoRunHours` | `0` | Real-sell automatically every N hours. `0` means never. |

The first automatic run happens 10 minutes after ASF starts (or after the bot config is reloaded), then every `AutoRunHours` hours. A run is skipped if the bot is not logged in, or if another run is still going.

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

Requirements: .NET 10 SDK and a folder with the ASF files of the version you run (the generic ASF release zip is enough: it contains `ArchiSteamFarm.dll`, `SteamKit2.dll`, `AngleSharp.dll`, `System.Composition.AttributedModel.dll`).

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
