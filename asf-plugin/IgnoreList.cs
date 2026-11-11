using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CS2InstantSell;

// Items that sit at the market minimum price practically never get buy orders. They are remembered permanently
// (one market name per line) in ASF's config folder, shared by all bots, and survive restarts.
// The file has no .json extension on purpose, so ASF never mistakes it for a bot config.
internal static class IgnoreList {
	internal const string FileName = "CS2InstantSell.ignored.txt";

	private static readonly object Sync = new();
	private static HashSet<string>? Names;

	private static string FilePath => Path.Combine(Directory.GetCurrentDirectory(), "config", FileName);

	private static HashSet<string> Load() {
		if (Names != null) {
			return Names;
		}

		Names = new HashSet<string>(StringComparer.Ordinal);

		try {
			if (File.Exists(FilePath)) {
				foreach (string line in File.ReadAllLines(FilePath)) {
					if (!string.IsNullOrWhiteSpace(line)) {
						Names.Add(line.Trim());
					}
				}
			}
		} catch (Exception) {
			// Unreadable file: start empty, the list is only an optimisation.
		}

		return Names;
	}

	private static void Save() {
		try {
			File.WriteAllLines(FilePath, Load().OrderBy(static n => n, StringComparer.Ordinal));
		} catch (Exception) {
			// Not writable (read-only folder): the list then only lives until the next restart.
		}
	}

	internal static bool Contains(string name) {
		lock (Sync) {
			return Load().Contains(name);
		}
	}

	// True if the name was new.
	internal static bool Add(string name) {
		lock (Sync) {
			if (!Load().Add(name)) {
				return false;
			}

			Save();

			return true;
		}
	}

	// True if the name was on the list.
	internal static bool Remove(string name) {
		lock (Sync) {
			if (!Load().Remove(name)) {
				return false;
			}

			Save();

			return true;
		}
	}

	internal static int Count {
		get {
			lock (Sync) {
				return Load().Count;
			}
		}
	}
}
