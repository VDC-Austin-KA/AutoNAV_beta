using System;
using System.Collections.Generic;
using System.Linq;
using NavApp = Autodesk.Navisworks.Api.Application;
using Autodesk.Navisworks.Api;

namespace AutoNAVMCP
{
    // Property-suggestion engine: for a given discipline, probe its models
    // across the property locations that commonly hold a system identifier
    // (a duct's "system classification" can live under Element/System
    // Classification, Element/System Abbreviation, Element Properties/System
    // Abbreviation, …) and rank the ones that actually carry usable values.
    //
    // Ranking favours, in order of weight:
    //   * coverage   — how many elements actually have a value there,
    //   * brevity    — SHORTER values score higher, because the value ends up
    //                  inside the clash-group name and "less is more" there,
    //   * granularity— enough distinct values to separate systems, but not so
    //                  many that every element becomes its own group.
    public partial class SearchSetGenerator
    {
        public sealed class PropertySuggestion
        {
            public string Category;
            public string Property;
            public double CoveragePercent;   // % of scanned property-bearing items with a value
            public int DistinctValues;
            public int ItemsWithValue;
            public double AverageValueLength;
            public int ShortestValueLength;
            public int LongestValueLength;
            public List<string> ExampleValues = new List<string>(); // shortest-first
            public double Score;             // 0-100
            public bool Recommended;
            public string Reason;
        }

        // Property paths worth checking even before we've seen the model, in
        // rough priority order. The scan also auto-discovers any other property
        // whose name looks like a system identifier (see _identifierKeywords).
        private static readonly (string Category, string Property)[] _candidatePaths =
        {
            ("Element", "System Classification"),
            ("Element", "System Abbreviation"),
            ("Element Properties", "System Abbreviation"),
            ("Element", "System Name"),
            ("Element", "System Type"),
            ("System Type", "Name"),
            ("Element", "Type"),
            ("Element", "Workset"),
            ("Element", "Category"),
            ("Item", "Type"),
            ("Item", "Layer"),
        };

        private static readonly string[] _identifierKeywords =
        {
            "system", "abbrev", "classif", "workset", "service", "discipline", "mark",
        };

        // Scans a discipline's models and returns ranked property suggestions.
        public static List<PropertySuggestion> SuggestSystemProperties(string discipline, int maxItemsToScan = 15000)
        {
            var stats = new Dictionary<string, PropAccumulator>(StringComparer.OrdinalIgnoreCase);
            int propertyBearingItems = 0;

            Document doc = NavApp.ActiveDocument;
            if (doc == null) return new List<PropertySuggestion>();

            var candidateSet = new HashSet<string>(
                _candidatePaths.Select(c => Key(c.Category, c.Property)), StringComparer.OrdinalIgnoreCase);

            foreach (Model model in FindModelsForDiscipline(doc, discipline))
            {
                int scanned = 0;
                ScanItem(model.RootItem, candidateSet, stats, ref scanned, ref propertyBearingItems, maxItemsToScan);
                if (scanned >= maxItemsToScan) break;
            }

            var suggestions = new List<PropertySuggestion>();
            foreach (var kv in stats)
            {
                PropAccumulator a = kv.Value;
                if (a.ItemsWithValue == 0) continue;

                double coverage = propertyBearingItems > 0
                    ? (double)a.ItemsWithValue / propertyBearingItems : 0.0;
                double avgLen = a.ItemsWithValue > 0 ? (double)a.TotalLength / a.ItemsWithValue : 0.0;

                var s = new PropertySuggestion
                {
                    Category = a.Category,
                    Property = a.Property,
                    CoveragePercent = Math.Round(coverage * 100.0, 1),
                    DistinctValues = a.DistinctValues.Count,
                    ItemsWithValue = a.ItemsWithValue,
                    AverageValueLength = Math.Round(avgLen, 1),
                    ShortestValueLength = a.MinLength == int.MaxValue ? 0 : a.MinLength,
                    LongestValueLength = a.MaxLength,
                    ExampleValues = a.DistinctValues
                        .OrderBy(v => v.Length).ThenBy(v => v, StringComparer.OrdinalIgnoreCase)
                        .Take(5).ToList(),
                };
                ScoreAndExplain(s, coverage, avgLen);
                suggestions.Add(s);
            }

            suggestions = suggestions
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.AverageValueLength)
                .ToList();

            // Recommend the top scorer(s) that actually discriminate systems.
            var best = suggestions.FirstOrDefault(s => s.DistinctValues > 1 && s.CoveragePercent >= 25);
            if (best != null) best.Recommended = true;

            return suggestions;
        }

        // Weighted score in 0-100. Brevity is weighted heavily on purpose:
        // the chosen value is baked into every clash-group name.
        private static void ScoreAndExplain(PropertySuggestion s, double coverage, double avgLen)
        {
            // Brevity: 1.0 at <=2 chars, tapering to 0 at ~22 chars.
            double brevity = Clamp(1.0 - (avgLen - 2.0) / 20.0, 0.0, 1.0);

            // Granularity: best when there are a handful of systems (2-40).
            double granularity;
            int d = s.DistinctValues;
            if (d <= 1) granularity = 0.05;
            else if (d <= 40) granularity = 1.0;
            else if (d <= 120) granularity = 0.6;
            else granularity = 0.3;

            double score = 0.45 * coverage + 0.35 * brevity + 0.20 * granularity;
            s.Score = Math.Round(score * 100.0, 1);

            var reasons = new List<string>();
            reasons.Add(coverage >= 0.75 ? "populated on nearly all elements"
                       : coverage >= 0.4 ? "populated on most elements"
                       : "populated on some elements");
            reasons.Add(avgLen <= 6 ? "short values — ideal for group names"
                       : avgLen <= 12 ? "moderate value length"
                       : "long values — will crowd group names");
            reasons.Add(d <= 1 ? "does not separate systems (only one value)"
                       : d <= 40 ? "cleanly separates systems"
                       : "very granular — may over-split");
            s.Reason = string.Join("; ", reasons);
        }

        private sealed class PropAccumulator
        {
            public string Category, Property;
            public int ItemsWithValue;
            public long TotalLength;
            public int MinLength = int.MaxValue;
            public int MaxLength;
            public HashSet<string> DistinctValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static void ScanItem(
            ModelItem item, HashSet<string> candidateSet,
            Dictionary<string, PropAccumulator> stats,
            ref int scanned, ref int propertyBearingItems, int maxItems)
        {
            if (item == null || scanned >= maxItems) return;

            try
            {
                bool itemHadAnyProperty = false;
                foreach (PropertyCategory cat in item.PropertyCategories)
                {
                    string catName = cat.DisplayName;
                    if (string.IsNullOrEmpty(catName)) continue;
                    foreach (DataProperty prop in cat.Properties)
                    {
                        string propName = prop.DisplayName;
                        if (string.IsNullOrEmpty(propName)) continue;

                        string key = Key(catName, propName);
                        bool relevant = candidateSet.Contains(key) || LooksLikeIdentifier(propName);
                        if (!relevant) continue;

                        string val;
                        try { val = prop.Value != null ? prop.Value.ToDisplayString() : null; }
                        catch { continue; }
                        if (string.IsNullOrWhiteSpace(val)) continue;
                        val = val.Trim();

                        itemHadAnyProperty = true;
                        PropAccumulator acc;
                        if (!stats.TryGetValue(key, out acc))
                        {
                            acc = new PropAccumulator { Category = catName, Property = propName };
                            stats[key] = acc;
                        }
                        acc.ItemsWithValue++;
                        acc.TotalLength += val.Length;
                        if (val.Length < acc.MinLength) acc.MinLength = val.Length;
                        if (val.Length > acc.MaxLength) acc.MaxLength = val.Length;
                        if (acc.DistinctValues.Count < 500) acc.DistinctValues.Add(val);
                    }
                }
                if (itemHadAnyProperty) propertyBearingItems++;
                scanned++;
            }
            catch { }

            try
            {
                foreach (ModelItem child in item.Children)
                {
                    ScanItem(child, candidateSet, stats, ref scanned, ref propertyBearingItems, maxItems);
                    if (scanned >= maxItems) return;
                }
            }
            catch { }
        }

        private static bool LooksLikeIdentifier(string propName)
        {
            string lower = propName.ToLowerInvariant();
            foreach (string kw in _identifierKeywords)
                if (lower.Contains(kw)) return true;
            return false;
        }

        private static string Key(string category, string property)
        {
            // U+241F can't appear in a property/category display name, so it's a
            // safe compound-key joiner. Written as an escape to keep source ASCII.
            return (category ?? "") + "␟" + (property ?? "");
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
