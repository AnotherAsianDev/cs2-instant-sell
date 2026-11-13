# Changelog

Every change is published as its own version tag and release.

## v1.3.0

- ASF plugin: the wallet balance for TF2 key buying is now read fresh from Steam's market page on every run (also the check that a key was paid). ASF's cached balance did not notice when pending funds became available. The log shows pending funds separately.

## v1.2.0

- ASF plugin: new setting `IgnoreMarketPriceUpTo` (default 0.03). Items at the market minimum price are ignored permanently and listed in `config/CS2InstantSell.ignored.txt`.
- ASF plugin: no wait before key buying when nothing was sold in the run.

## v1.1.0

- ASF plugin ported to **ASF 7.0.0.5** (.NET 11).
- ASF plugin: `AutoRunTimes` (fixed daily run times), combinable with `AutoRunHours` (minimum 1).
- ASF plugin: optional TF2 key buying (`BuyTf2Keys`, `Tf2KeyMaxPrice`, `Tf2KeyKeepBalance`, `Tf2KeyMaxPerRun`) using instant buy orders, including Steam's mobile confirmation step.
- ASF plugin: fewer requests (one order-book request per item and run, 3 h cache for items without instant sale).

## v1.0.0

- First release: browser extension and ASF plugin (for ASF 6.3.11.7), documentation, tests.
