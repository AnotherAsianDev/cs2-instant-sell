# CS2 Instant Sell

Sell your CS2 inventory on the Steam Community Market **instantly**: each item is listed at the price of the highest buy order, so it sells right away instead of waiting for a buyer.

This repository contains two tools that share the same selling logic:

| Tool | For | What it does |
|---|---|---|
| [`extension/`](extension) | Brave, Chrome, Edge (Chromium) | Adds a panel to your Steam inventory page. Scan, review the list, press Sell. |
| [`asf-plugin/`](asf-plugin) | [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) (ASF) | Sells for a bot account by command or on a schedule, accepts the confirmations with ASF's 2FA, switches on/off per account. |

Documentation:

- [Browser extension guide](docs/extension.md)
- [ASF plugin guide](docs/asf-plugin.md)
- [How the selling works](docs/how-it-works.md) (Steam endpoints, fee math, price rules, known limits)

## Quick start

**Browser extension**

1. Open `brave://extensions` (or `chrome://extensions`, `edge://extensions`), turn on *Developer mode*.
2. *Load unpacked* and select the [`extension/`](extension) folder.
3. Open your CS2 inventory on steamcommunity.com and click **⚡ Instant Sell**.

**ASF plugin**

1. Copy [`asf-plugin/dist/CS2InstantSell.dll`](asf-plugin/dist) to `ASF/plugins/CS2InstantSell/CS2InstantSell.dll`.
2. Add the `CS2InstantSell` block from [`example-bot-config.json`](asf-plugin/example-bot-config.json) to the config of the bot that should sell.
3. Restart ASF, then run `!isell <bot>` (dry run) and later `!isellgo <bot>`.

## Read this first

- **Filled sales cannot be undone.** Always look at the preview (extension list, or `!isell` dry run) before selling, and start small (`MaxItemsPerRun: 3` for the plugin).
- This relies on Steam web endpoints that are **not an official API** (`/market/orderbook`, `/market/sellitem`, the inventory JSON). Steam has changed the market pages before and can again, which may break the tools until they are updated.
- **Trade-locked items cannot be detected in advance.** The inventory data Steam sends does not mark them. They are tried anyway, refused by Steam and reported as failed (the plugin then skips them for 12 hours).
- Not affiliated with or endorsed by Valve. Use at your own risk.

## Tested against

- ASF **6.3.11.7** on .NET 10 (the plugin is compiled against this version).
- Brave (Chromium) with Manifest V3.

## Repository layout

```
extension/              Browser extension (Manifest V3, plain JavaScript, no build step)
asf-plugin/             ASF plugin source (C#, .NET 10)
asf-plugin/dist/        Ready-to-use CS2InstantSell.dll built from that source
tests/PricingTests/     Tests for the fee and price-ladder logic
docs/                   Guides
```

## Build the plugin yourself

See [docs/asf-plugin.md](docs/asf-plugin.md#building-from-source).

## License

MIT, see [LICENSE](LICENSE).
