using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Planetwalker.Persistence;

namespace Planetwalker.Library
{
    public enum WorldSortMode
    {
        RecentlyModified,
        OldestModified,
        NameAscending,
        NameDescending,
        RecentlyCreated,
    }

    /// <summary>
    /// The library's in-memory list (V0.1.3B): archive entries read once when the library opens (and after an
    /// archive operation), then searched and sorted in memory. No disk access or JSON parsing per keystroke.
    /// </summary>
    public sealed class WorldLibraryModel
    {
        static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

        readonly List<WorldArchiveEntry> all = new List<WorldArchiveEntry>();
        readonly List<WorldArchiveEntry> view = new List<WorldArchiveEntry>();

        public string Query { get; private set; } = "";
        public WorldSortMode Sort { get; private set; } = WorldSortMode.RecentlyModified;

        /// <summary>Every archive (readable or not), in the current sort order of the last rebuild.</summary>
        public IReadOnlyList<WorldArchiveEntry> All => all;

        /// <summary>Entries matching the query, sorted.</summary>
        public IReadOnlyList<WorldArchiveEntry> View => view;

        /// <summary>Time of the last <see cref="Reload"/> (archive listing included), milliseconds.</summary>
        public double LastReloadMilliseconds { get; private set; }

        /// <summary>Time of the last search / sort rebuild, milliseconds.</summary>
        public double LastRebuildMilliseconds { get; private set; }

        /// <summary>Reads the archive folder (metadata only, no planet generated) and rebuilds the view.</summary>
        public void Reload(WorldArchiveStore store)
        {
            var watch = Stopwatch.StartNew();
            all.Clear();
            all.AddRange(store.List());
            Rebuild();
            LastReloadMilliseconds = watch.Elapsed.TotalMilliseconds;
        }

        /// <summary>Uses a given list (tests).</summary>
        public void SetEntries(IEnumerable<WorldArchiveEntry> entries)
        {
            all.Clear();
            all.AddRange(entries);
            Rebuild();
        }

        public void SetQuery(string query)
        {
            query = (query ?? "").Trim();
            if (query != Query)
            {
                Query = query;
                Rebuild();
            }
        }

        public void SetSort(WorldSortMode sort)
        {
            if (sort != Sort)
            {
                Sort = sort;
                Rebuild();
            }
        }

        public int IndexOf(Guid planetId)
        {
            for (int i = 0; i < view.Count; i++)
            {
                if (view[i].PlanetId == planetId)
                {
                    return i;
                }
            }
            return -1;
        }

        public WorldArchiveEntry Find(Guid planetId)
        {
            foreach (WorldArchiveEntry entry in all)
            {
                if (entry.PlanetId == planetId)
                {
                    return entry;
                }
            }
            return null;
        }

        /// <summary>
        /// Case-insensitive (culture-invariant, any Unicode script) match on the display name, or on the seed /
        /// planet ID digits. Unreadable archives match on their ID only.
        /// </summary>
        public static bool Matches(WorldArchiveEntry entry, string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                return true;
            }
            if (entry.IsValid)
            {
                if (Compare.IndexOf(entry.DisplayName, query, CompareOptions.IgnoreCase) >= 0)
                {
                    return true;
                }
                if (entry.PlanetSeed.ToString(CultureInfo.InvariantCulture).IndexOf(query, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return PlanetArchive.FormatId(entry.PlanetId).StartsWith(query.ToLowerInvariant(), StringComparison.Ordinal);
        }

        void Rebuild()
        {
            var watch = Stopwatch.StartNew();
            view.Clear();
            foreach (WorldArchiveEntry entry in all)
            {
                if (Matches(entry, Query))
                {
                    view.Add(entry);
                }
            }
            view.Sort(Comparison);
            LastRebuildMilliseconds = watch.Elapsed.TotalMilliseconds;
        }

        /// <summary>Total order: the sort key, then the planet ID (deterministic for equal names or dates). Unreadable archives last.</summary>
        int Comparison(WorldArchiveEntry a, WorldArchiveEntry b)
        {
            if (a.IsValid != b.IsValid)
            {
                return a.IsValid ? -1 : 1;
            }
            int c = 0;
            switch (Sort)
            {
                case WorldSortMode.RecentlyModified:
                    c = b.ModifiedUtc.CompareTo(a.ModifiedUtc);
                    break;
                case WorldSortMode.OldestModified:
                    c = a.ModifiedUtc.CompareTo(b.ModifiedUtc);
                    break;
                case WorldSortMode.RecentlyCreated:
                    c = b.CreatedUtc.CompareTo(a.CreatedUtc);
                    break;
                case WorldSortMode.NameAscending:
                case WorldSortMode.NameDescending:
                    c = Compare.Compare(a.DisplayName ?? "", b.DisplayName ?? "", CompareOptions.IgnoreCase);
                    if (c == 0)
                    {
                        c = string.CompareOrdinal(a.DisplayName ?? "", b.DisplayName ?? "");
                    }
                    if (Sort == WorldSortMode.NameDescending)
                    {
                        c = -c;
                    }
                    break;
            }
            return c != 0 ? c : a.PlanetId.CompareTo(b.PlanetId);
        }

        public static string SortLabel(WorldSortMode sort)
        {
            switch (sort)
            {
                case WorldSortMode.OldestModified: return "Oldest modified";
                case WorldSortMode.NameAscending: return "Name A-Z";
                case WorldSortMode.NameDescending: return "Name Z-A";
                case WorldSortMode.RecentlyCreated: return "Recently created";
                default: return "Recently modified";
            }
        }
    }
}
