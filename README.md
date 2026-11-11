# CS2 Instant Sell

Sell your CS2 inventory on the Steam Community Market **instantly**: each item is listed at the price of the highest buy order, so it sells right away instead of waiting for a buyer. The ASF plugin can also turn the money into TF2 keys automatically.

This repository contains two tools that share the same selling logic:

| Tool | For | What it does |
|---|---|---|
| [`extension/`](extension) | Brave, Chrome, Edge (Chromium) | Adds a panel to your Steam inventory page. Scan, review the list, press Sell. |
| [`asf-plugin/`](asf-plugin) | [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) (ASF) | Sells for a bot account by command or on a schedule, accepts the confirmations with ASF's 2FA, switches on/off per account, optionally buys TF2 keys. |

## Features

**Both tools**

- Instant sell at the highest buy order, taking Steam's fees into account.
- Sells as many copies of an item as there are buy orders within your price limits (`MaxDropPercent`, `NoDropLimitBelow`, `MinPayout`).
- Re-reads the buy orders right before each item, so it never lists into orders that are already gone.
- Slows down by itself when Steam rate-limits (HTTP 429).

**Browser extension**

- Panel on the inventory page: scan, review, untick items, sell, copy log.
- Settings that update the list live without a new scan.

**ASF plugin**

- Per-bot on/off switch in the bot's own config. Bots without the block are never touched.
- Automatic runs at fixed daily times (`AutoRunTimes`, e.g. `["02:00", "08:00", "14:00", "20:00"]`), every N hours (`AutoRunHours`), or both.
- Commands: `!isell` (dry run), `!isellgo` (sell), `!isellstop`, `!isellstatus`.
- Everything is logged live in the ASF log with the prefix `[CS2 Instant Sell]`.
- Accepts market confirmations with ASF's 2FA.
- Optional: after selling, buys as many **Mann Co. Supply Crate Keys** (TF2) as the wallet balance allows, with a hard price limit.
- Fewer requests: one order-book request per item and run, items without instant sale are skipped for 3 hours, and items at the market minimum price (0.03) are ignored permanently.
- `Keep` list for items that must never be sold.

## Quick start

**Browser extension**

1. Download this repository (green **Code** button → **Download ZIP**) and unzip it. The extension is the [`extension/`](extension) folder.
2. Open `brave://extensions` (or `chrome://extensions`, `edge://extensions`), turn on *Developer mode*.
3. *Load unpacked* and select the `extension` folder (the one containing `manifest.json`).
4. Open your CS2 inventory on steamcommunity.com and click **⚡ Instant Sell**.

**ASF plugin**

1. Download `CS2InstantSell.dll` from the [latest release](../../releases/latest) (or [`asf-plugin/dist/`](asf-plugin/dist)) and copy it to `ASF/plugins/CS2InstantSell/CS2InstantSell.dll`.
2. Add the `CS2InstantSell` block to the config of the bot that should sell (inside the outer braces, with a comma before it):

   ```json
   "CS2InstantSell": {
   	"Enabled": true,
   	"MinPayout": 0.01,
   	"MaxDropPercent": 20,
   	"NoDropLimitBelow": 0.50,
   	"IgnoreMarketPriceUpTo": 0.03,
   	"AutoConfirm": true,
   	"MaxItemsPerRun": 3,
   	"Keep": [ "Dragon Lore", "Souvenir" ],
   	"AutoRunTimes": [ "02:00", "08:00", "14:00", "20:00" ],
   	"AutoRunHours": 0,
   	"BuyTf2Keys": false,
   	"Tf2KeyMaxPrice": 2.50,
   	"Tf2KeyKeepBalance": 0,
   	"Tf2KeyMaxPerRun": 0
   }
   ```

   A complete example is in [`example-bot-config.json`](asf-plugin/example-bot-config.json). Every setting is explained in the [plugin guide](docs/asf-plugin.md#settings).
3. Restart ASF. The log must show `CS2InstantSell has been loaded successfully`.
4. Run `!isell <bot>` (dry run, sells nothing) and read the log. Then `!isellgo <bot>`. With `MaxItemsPerRun: 3` the first real run sells only three items. Set it to `0` afterwards.
5. Running ASF in Docker? Set the time zone (`TZ=Europe/Zurich`) on the ASF service, or `AutoRunTimes` will use UTC.

**TF2 keys (optional):** set `"BuyTf2Keys": true` and `Tf2KeyMaxPrice` (the most you pay per key, in your wallet currency). Try it with `"Tf2KeyMaxPerRun": 1` first, then set it to `0`. Details in the [plugin guide](docs/asf-plugin.md#buying-tf2-keys).

## Documentation

- [Browser extension guide](docs/extension.md)
- [ASF plugin guide](docs/asf-plugin.md): install, all settings, commands, TF2 keys, ignore list, troubleshooting, building
- [How the selling works](docs/how-it-works.md): Steam endpoints, fee math, price rules, known limits
- [Changelog](CHANGELOG.md)

## Read this first

- **Filled sales cannot be undone.** Always look at the preview (extension list, or `!isell` dry run) before selling, and start small (`MaxItemsPerRun: 3`).
- **Key buying spends real wallet money.** It is off by default, needs `Tf2KeyMaxPrice`, and spends the whole wallet balance above `Tf2KeyKeepBalance`.
- This relies on Steam web endpoints that are **not an official API** (`/market/orderbook`, `/market/sellitem`, `/market/createbuyorder`, the inventory JSON). Steam has changed these before and can again, which may break the tools until they are updated.
- **Trade-locked items cannot be detected in advance.** The inventory data Steam sends does not mark them. They are tried anyway, refused by Steam and reported as failed (the plugin then skips them for 12 hours).
- ASF plugins only load in the ASF generation they were built for. After an ASF major update the plugin may need a rebuild.
- Not affiliated with or endorsed by Valve. Use at your own risk.

## Tested against

- ASF **7.0.0.5** on .NET 11 (the plugin is compiled against this version). For ASF 6.x use the repository state before the "Port plugin to ASF 7.0.0.5" commit (built for 6.3.11.7).
- Brave (Chromium) with Manifest V3.

## Repository layout

```
extension/              Browser extension (Manifest V3, plain JavaScript, no build step)
asf-plugin/             ASF plugin source (C#, .NET 11)
asf-plugin/dist/        Ready-to-use CS2InstantSell.dll built from that source
tests/PricingTests/     Tests for the fee and price-ladder logic
docs/                   Guides
CHANGELOG.md            What changed in which version
```

## Build the plugin yourself

See [docs/asf-plugin.md](docs/asf-plugin.md#building-from-source).

## License

MIT, see [LICENSE](LICENSE).
