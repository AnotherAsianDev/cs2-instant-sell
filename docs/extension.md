# Browser extension

A Manifest V3 extension for Brave, Chrome and Edge. It runs only on your own Steam inventory page and sells CS2 items at the highest buy order.

## Install

1. Open `brave://extensions` (Chrome: `chrome://extensions`, Edge: `edge://extensions`).
2. Turn on **Developer mode**.
3. Click **Load unpacked** and select the `extension/` folder.
4. Open `https://steamcommunity.com/id/<you>/inventory/` (or the `/profiles/<id>/inventory/` form) while logged in.
5. Click **⚡ Instant Sell** at the bottom right.

After changing the extension files, press the reload button on the extension card and refresh the inventory page.

Edge may show a notice about developer-mode extensions at startup. It can be dismissed.

## Use

1. **Scan inventory** reads your CS2 inventory, then looks up the buy orders of every different item (3 lookups at a time, about 0.4 s apart, slowing down automatically if Steam answers with HTTP 429).
2. The list shows, per item: how many can be sold instantly (`4/31` means 4 of 31), what you get for the first one after fees, and the total.
3. Untick items you want to keep.
4. **Sell selected** asks for a confirmation and then lists the items one by one (1.5 s apart).
5. Unless your account is exempt, accept the listings in the Steam mobile app (Confirmations).

Other buttons:

- **Stop**: aborts a scan or a sell run after the current request.
- **Copy log**: copies the whole log to the clipboard.
- **Dump item**: prints Steam's raw data of an item (search by part of the name). Useful for debugging.

## Settings

All three update the list immediately, without a new scan.

| Setting | Default | Meaning |
|---|---|---|
| Min. payout per item | `0.01` | Items that would pay out less than this **after fees** are skipped. A buyer price of 0.05 pays out about 0.03 (Steam takes a fixed minimum of 1 cent plus 1 cent publisher fee), so a minimum of 0.05 would skip it. |
| Max. price drop % | `20` | Walking down the buy-order list, stop when the price is this many percent below the highest buy order. |
| No drop limit below | `0.50` | Items whose top buy order is cheaper than this ignore the drop limit. |

## What protects you from bad listings

- Only items for which a buy order actually exists are planned. If you own 31 copies but there are 12 buy orders in range, the list shows `12/31`.
- Right before selling each item, the buy orders are read again and the plan is recomputed. If other sellers used them up in the meantime, fewer items are listed (the log says so). This avoids listings that nobody will fill.
- Items without any buy order are skipped ("no buyers").

## Limits

- **Trade-locked items are not detected.** Steam's inventory data contains no lock information for them. They appear in the list, Steam refuses them when you press Sell, and the log shows `✗`. Untick them beforehand if you know which they are.
- The mobile confirmation step is done by you.
- The panel only works on the inventory page of the logged-in account.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Nothing happens after "Loading CS2 inventory…" | Update to the current extension; older builds retried failed lookups for minutes. Check the log. |
| `orderbook HTTP …` or `unexpected reply` lines | Steam changed or throttled the endpoint. Press **Copy log** and compare with [how it works](how-it-works.md). |
| An item shows `too low` | Its payout or price is below *Min. payout*, or further down the ladder than *Max. price drop*. Lower the minimum or raise the drop. |
| An item shows `no buyers` | There is no buy order for it at the moment. |
| Rate-limit messages | Normal. The extension slows down and retries by itself. |

## Files

- `manifest.json`: runs `content.js` and `content.css` on inventory pages only. No special permissions.
- `content.js`: all logic.
- `content.css`: panel styling.
