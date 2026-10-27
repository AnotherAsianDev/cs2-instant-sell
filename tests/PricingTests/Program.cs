using System;
using System.Collections.Generic;
using CS2InstantSell;

int failures = 0;
void Check(string name, bool ok, string detail = "") { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {detail}"); if (!ok) failures++; }

FeeInfo fee = new(0.05, 1, 0, 0.10);

// Same values the browser extension produced (buyer pays -> seller receives)
foreach ((long pays, long expect) in new (long, long)[] { (3, 1), (5, 3), (10, 8), (115, 100), (230, 200), (1150, 1000), (10000, 8697) }) {
	long got = Pricing.SellerReceives(pays, fee);
	Check($"SellerReceives({pays})", got == expect, $"expected {expect}, got {got}");
}

// total with fees must never exceed what the buyer pays, and must be maximal
for (long p = 3; p <= 20000; p++) {
	long r = Pricing.SellerReceives(p, fee);
	long fees(long a) => (long)Math.Floor(Math.Max(a * 0.05, 1)) + (long)Math.Floor(Math.Max(a * 0.1, 1));
	if (r + fees(r) > p || r + 1 + fees(r + 1) <= p) { Check($"fee invariant at {p}", false); break; }
}
Check("fee invariant 3..20000", true);

// Ladder: 4 orders at 100, 26 at 99, 10 at 80, 100 at 50
var book = new List<(long, long)> { (100, 4), (99, 26), (80, 10), (50, 100) };

var p31 = Pricing.Plan(book, 31, 5, 1, 0, fee);
Check("drop 5%: 30 sellable (levels 100 and 99)", p31.Count == 30, $"got {p31.Count}");
Check("first four at 100, then 99", p31[3].BuyerPays == 100 && p31[4].BuyerPays == 99);

var p31b = Pricing.Plan(book, 31, 20, 1, 0, fee);
Check("drop 20%: 31 sellable", p31b.Count == 31, $"got {p31b.Count}");
Check("31st unit at 80", p31b[30].BuyerPays == 80);

var p50 = Pricing.Plan(book, 100, 100, 1, 0, fee);
Check("only 40+100 buy orders exist, 100 items -> 100", p50.Count == 100, $"got {p50.Count}");

var few = Pricing.Plan(book, 500, 100, 1, 0, fee);
Check("more items than buy orders -> capped at 140", few.Count == 140, $"got {few.Count}");

// cheap item ignores the drop limit (user's rule: below 0.50 ignore the limit)
var cheap = new List<(long, long)> { (20, 2), (10, 5) };
Check("cheap item with limit active stops", Pricing.Plan(cheap, 7, 20, 1, 0, fee).Count == 2);
Check("cheap item ignoring limit sells all 7", Pricing.Plan(cheap, 7, 20, 1, 50, fee).Count == 7);

// user's Sealed Genesis Terminal case: buyer 5 -> net 3
Check("net 3 skipped with min payout 5", Pricing.Plan(new List<(long, long)> { (5, 10) }, 3, 20, 5, 50, fee).Count == 0);
Check("net 3 sold with min payout 1", Pricing.Plan(new List<(long, long)> { (5, 10) }, 3, 20, 1, 50, fee).Count == 3);

Check("empty book", Pricing.Plan(new List<(long, long)>(), 5, 20, 1, 0, fee).Count == 0);
Check("zero items", Pricing.Plan(book, 0, 20, 1, 0, fee).Count == 0);

Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} FAILED");
return failures;
