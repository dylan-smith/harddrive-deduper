using System.Globalization;
using System.Text;

namespace DupeHunter;

/// <summary>
/// Parses a YAML duplicate report back into a <see cref="DuplicateReport"/>. This is not a general
/// YAML parser — like <see cref="DuplicateYamlWriter"/> it carries no YAML dependency — it reads
/// exactly the dialect the writer emits (two-space indentation, double-quoted scalars, one
/// <c>key: value</c> per line) and throws <see cref="InvalidDataException"/> with a line number on
/// anything else. Keys within a list item may appear in any order, so a hand-edited file still loads
/// as long as the shape is preserved. <c>copyCount</c> and <c>wastedBytes</c> are accepted but
/// ignored: they derive from the location list, which is the source of truth.
/// </summary>
public static class DuplicateYamlReader
{
    public static async Task<DuplicateReport> LoadAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var lines = await File.ReadAllLinesAsync(path, ct);
        return new Parser(lines).Parse();
    }

    private sealed class Parser(string[] lines)
    {
        private int _i;

        /// <summary>
        /// The line <see cref="Error"/> blames: the one most recently handed to a value parser. The
        /// cursor itself (<see cref="_i"/>) has usually advanced past it by the time a bad value throws.
        /// </summary>
        private int _errorLine;

        public DuplicateReport Parse()
        {
            var report = new DuplicateReport();
            while (TryCurrent(out var line))
            {
                var (key, value) = SplitKeyValue(line, indent: 0);
                _i++;
                switch (key)
                {
                    case "generatedUtc":
                        report.GeneratedUtc = ParseTimestamp(value);
                        break;
                    case "wastedSpaceThresholdBytes":
                        report.ThresholdBytes = ParseLong(value);
                        break;
                    case "totalWastedBytes":
                        report.TotalWastedBytes = ParseLong(value);
                        break;
                    case "scans":
                        if (value != "[]")
                        {
                            RequireEmpty(key, value);
                            ParseScans(report.Scans);
                        }
                        break;
                    case "duplicateFileSets":
                        if (value != "[]")
                        {
                            RequireEmpty(key, value);
                            ParseSets(report.FileSets);
                        }
                        break;
                    case "duplicateFolderSets":
                        if (value != "[]")
                        {
                            RequireEmpty(key, value);
                            ParseSets(report.FolderSets);
                        }
                        break;
                    default:
                        throw Error($"unexpected top-level key '{key}'");
                }
            }
            return report;
        }

        private void ParseScans(ICollection<ScanRef> scans)
        {
            while (TryStartItem(out var firstKey, out var firstValue))
            {
                var fields = ReadItemFields(firstKey, firstValue, locations: null);
                scans.Add(new ScanRef(
                    ParseString(Require(fields, "drive")),
                    ParseString(Require(fields, "scanRunId")),
                    ParseTimestamp(Require(fields, "completedUtc"))));
            }
        }

        private void ParseSets(ICollection<DuplicateReportSet> sets)
        {
            while (TryStartItem(out var firstKey, out var firstValue))
            {
                var locations = new List<string>();
                var fields = ReadItemFields(firstKey, firstValue, locations);

                var set = new DuplicateReportSet
                {
                    ContentHash = ParseString(Require(fields, "contentHash")),
                    SizeBytes = ParseLong(Require(fields, "sizeBytes")),
                    Name = ParseNullableString(Require(fields, "name")),
                    NamesDiffer = ParseBool(Require(fields, "namesDiffer")),
                };
                foreach (var location in locations)
                {
                    set.Locations.Add(location);
                }
                sets.Add(set);
            }
        }

        /// <summary>
        /// If the current line opens a list item (<c>  - key: value</c>), consume it and return its
        /// first pair; otherwise leave the cursor for the caller (the list has ended).
        /// </summary>
        private bool TryStartItem(out string key, out string value)
        {
            key = value = "";
            if (!TryCurrent(out var line) || !line.StartsWith("  - ", StringComparison.Ordinal))
            {
                return false;
            }

            (key, value) = SplitKeyValue(line, indent: 4);
            _i++;
            return true;
        }

        /// <summary>
        /// The rest of one list item: <c>key: value</c> pairs at four-space indent, in any order. A
        /// <c>locations:</c> key switches to reading its six-space <c>- "…"</c> entries into
        /// <paramref name="locations"/> (null when the item type has no such list, e.g. scans).
        /// </summary>
        private Dictionary<string, string> ReadItemFields(string firstKey, string firstValue, List<string>? locations)
        {
            var fields = new Dictionary<string, string> { [firstKey] = firstValue };
            while (TryCurrent(out var line)
                   && line.StartsWith("    ", StringComparison.Ordinal)
                   && !line.StartsWith("      ", StringComparison.Ordinal))
            {
                var (key, value) = SplitKeyValue(line, indent: 4);
                _i++;
                if (key == "locations" && locations is not null)
                {
                    RequireEmpty(key, value);
                    while (TryCurrent(out var loc) && loc.StartsWith("      - ", StringComparison.Ordinal))
                    {
                        _errorLine = _i;
                        locations.Add(ParseString(loc["      - ".Length..]));
                        _i++;
                    }
                }
                else
                {
                    fields[key] = value;
                }
            }
            return fields;
        }

        /// <summary>Advance past blank and comment lines to the next content line, if any.</summary>
        private bool TryCurrent(out string line)
        {
            while (_i < lines.Length)
            {
                line = lines[_i];
                var trimmed = line.TrimStart();
                if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                {
                    return true;
                }
                _i++;
            }
            line = "";
            return false;
        }

        /// <summary>
        /// Split <c>key: value</c> at the given indent (a list item's leading <c>- </c> counts toward
        /// it). The colon search is safe because keys never contain one; values are quoted so theirs
        /// don't matter.
        /// </summary>
        private (string Key, string Value) SplitKeyValue(string line, int indent)
        {
            _errorLine = _i;
            var body = line.Length > indent ? line[indent..] : "";
            var colon = body.IndexOf(':');
            return colon <= 0 ? throw Error("expected 'key: value'") : ((string Key, string Value))(body[..colon], body[(colon + 1)..].Trim());
        }

        private string Require(Dictionary<string, string> fields, string key) =>
            fields.TryGetValue(key, out var value) ? value : throw Error($"missing '{key}'");

        private void RequireEmpty(string key, string value)
        {
            if (value.Length != 0)
            {
                throw Error($"expected a nested list under '{key}'");
            }
        }

        private string ParseString(string value)
        {
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            {
                throw Error("expected a double-quoted string");
            }

            var sb = new StringBuilder(value.Length - 2);
            for (var i = 1; i < value.Length - 1; i++)
            {
                var c = value[i];
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (++i >= value.Length - 1)
                {
                    throw Error("dangling escape in string");
                }
                sb.Append(value[i] switch
                {
                    '\\' => '\\',
                    '"' => '"',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => throw Error($"unknown escape '\\{value[i]}'"),
                });
            }
            return sb.ToString();
        }

        private string? ParseNullableString(string value) => value == "null" ? null : ParseString(value);

        private bool ParseBool(string value) => value switch
        {
            "true" => true,
            "false" => false,
            _ => throw Error("expected 'true' or 'false'"),
        };

        private long ParseLong(string value) =>
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? n
                : throw Error("expected a non-negative integer");

        private DateTime ParseTimestamp(string value) =>
            DateTime.TryParseExact(ParseString(value), "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)
                ? utc
                : throw Error("expected an ISO-8601 UTC timestamp");

        private InvalidDataException Error(string message) =>
            new($"Not a dupehunter duplicate report, or it has been corrupted: {message} at line {_errorLine + 1}.");
    }
}
