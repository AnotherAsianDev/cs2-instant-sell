# How the selling works

Both tools use the same method. This page documents it, including the Steam behaviour discovered while building it.

## Instant sell on the Steam market

Steam has no "instant sell" API. A sell listing whose price is at or below the highest **buy order** is matched immediately. So the tools:

1. read the buy-order ladder of an item,
2. compute what the seller receives for a given buyer price (after Steam's fees),
3. create a listing with exactly that "seller receives" amount, which makes the buyer pay the buy-order price.

## Steam requests used

All requests go to `steamcommunity.com` with the logged-in session.

| Purpose | Request |
|---|---|
| Inventory | `GET /inventory/<steamid64>/730/2?l=english&count=2000` (paged with `start_assetid`) |
| Wallet currency, country, fee settings | `GET /market/`, parse `g_rgWalletInfo` from the page |
| Buy-order ladder | `GET /market/orderbook?q=Load&qp=[730,"<market_hash_name>"]&cc=<country>&l=english&currency=<id>` with header `x-valve-request-type: queryAction` and the item's listing page as `Referer` |
| Create listing | `POST /market/sellitem/` with `sessionid, appid=730, contextid=2, assetid, amount=1, price=<seller receives>` |
| Confirmations | Steam mobile confirmations (ASF plugin: ASF's 2FA, type *market listing*) |

### The orderbook reply

```json
{"success":true,"data":{"amtMaxBuyOrder":4124,"amtMinSellOrder":4132,"eCurrency":1,"cBuyOrders":53678,"cSellOrders":1120,
 "rgCompactBuyOrders":[4124,27,4106,26],"rgCompactSellOrders":[4132,2,4146,1]}}
```

`rgCompactBuyOrders` is a flat list of `price, quantity` pairs (price in the smallest currency unit, **per level, not cumulative**), highest price first. Steam sometimes wraps the object in one more `data`; both tools accept both forms. Format described in [this write-up](https://cs2.sh/resources/how-to-scrape-steam-market-orderbook).

### Why not `itemordershistogram`?

The older route needs an `item_nameid` that was scraped from the listing page (`Market_LoadOrderSpread(<id>)`). Steam's listing pages for CS2 now redirect to bucket IDs (like `G181820970A3004`) and no longer contain that call, so the name-based `orderbook` route is used. Both routes are undocumented.

## Fee math

Steam's wallet info provides `wallet_fee_percent` (0.05), `wallet_fee_minimum` (1), `wallet_fee_base` (0) and the publisher fee `wallet_publisher_fee_percent_default` (0.10). For an amount the seller receives:

```
steam_fee     = floor(max(amount * 0.05, 1) + base)
publisher_fee = floor(max(amount * 0.10, 1))
buyer_pays    = amount + steam_fee + publisher_fee
```

To go from the buyer's price back to the seller's amount, the tools start from `floor(price / 1.15)` and adjust by single units until `amount + fees <= price` and no larger amount fits. Examples: buyer 5 → seller 3, 115 → 100, 10000 → 8697. The minimum fees make very cheap items pay out only about 55-60% of the buyer price.

## Planning how many items can be sold

For an item with `n` copies and the ladder `(price, quantity)` sorted from the highest price:

- Copy number `k` (0-based) is assigned the first level where the cumulative quantity is greater than `k`.
- Planning stops when there are no more buy orders, when the level price falls below `top × (1 − MaxDropPercent/100)` (not applied if the top price is below `NoDropLimitBelow`), or when the seller payout is below `MinPayout`.

So you only ever list as many copies as buy orders exist inside your limits. Copies for which no buy order exists are not listed.

Right before selling an item, the ladder is read again and planned again, because buy orders can disappear while a scan or run is in progress. If fewer remain, fewer are listed.

## Rate limits

Steam answers HTTP 429 when requests come too fast. The extension uses 3 parallel lookups with about 0.4 s spacing and slows down on 429. The ASF plugin goes through ASF's own web limiter and waits and retries on 429. Listing requests are never retried blindly (a repeated `sellitem` could create the listing twice).

## Trade locks

Items from drops and trades can be trade-protected for several days. Steam's inventory JSON for CS2 does not contain any field that marks this (all items report `tradable: 1`, there are no `owner_descriptions` or `cache_expiration` fields), so locked items cannot be filtered out in advance. They are attempted, Steam refuses with a message, and the tools report them. The ASF plugin remembers refused asset IDs for 12 hours so repeated runs do not retry them each time.

## Known unknowns

- Steam's endpoints are undocumented and may change.
- Behaviour was confirmed on real accounts for the browser extension and the ASF plugin (CS2 items, CHF wallet), but fee settings and market rules differ per country and currency; the tools read the fee settings from the market page for that reason.
