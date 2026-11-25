using System.Text;

namespace CloudServices.Services.Appointment;

public sealed class BegegnungenCsvRow
{
    public string Home { get; init; } = "";
    public string Guest { get; init; } = "";
    public string Location { get; init; } = "";
}

public static class BegegnungenCsv
{
    public static TermineResult TryParse(byte[] bytes, out List<BegegnungenCsvRow> rows)
    {
        rows = [];
        var text = Decode(bytes);
        return TryParse(text, out rows);
    }

    public static TermineResult TryParse(string text, out List<BegegnungenCsvRow> rows)
    {
        rows = [];
        var normalized = (text ?? "").Replace("\uFEFF", "").Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalized.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        if (lines.Count < 2)
        {
            return TermineResult.Fail("CSV enthält keine Begegnungen.");
        }

        var delimiter = lines[0].Count(ch => ch == ';') >= lines[0].Count(ch => ch == ',') ? ';' : ',';
        var header = ParseLine(lines[0], delimiter).Select(cell => cell.ToLowerInvariant()).ToList();
        var homeIndex = HeaderIndex(header, ["heim", "home", "heimverein"]);
        var guestIndex = HeaderIndex(header, ["gast", "away", "gastverein"]);
        var locationIndex = HeaderIndex(header, ["nr", "nummer", "gruppe", "group", "ort", "spielort"]);
        if (homeIndex < 0 || guestIndex < 0)
        {
            return TermineResult.Fail("CSV braucht die Spalten Heim und Gast.");
        }

        foreach (var line in lines.Skip(1))
        {
            var cells = ParseLine(line, delimiter);
            var home = Cell(cells, homeIndex);
            var guest = Cell(cells, guestIndex);
            if (home.Length == 0 && guest.Length == 0)
            {
                continue;
            }

            if (home.Length == 0 || guest.Length == 0)
            {
                return TermineResult.Fail("Jede Begegnung braucht Heim- und Gastverein.");
            }

            if (string.Equals(home, guest, StringComparison.CurrentCultureIgnoreCase))
            {
                return TermineResult.Fail("Heim und Gast dürfen nicht derselbe Verein sein: " + home);
            }

            rows.Add(new BegegnungenCsvRow
            {
                Home = home,
                Guest = guest,
                Location = locationIndex >= 0 ? Cell(cells, locationIndex) : ""
            });
        }

        if (rows.Count == 0)
        {
            return TermineResult.Fail("CSV enthält keine Begegnungen.");
        }

        return TermineResult.Success();
    }

    public static void ApplyToTenant(TermineTenantDocument tenant, IReadOnlyList<BegegnungenCsvRow> rows)
    {
        var known = tenant.Clubs
            .Where(club => !string.IsNullOrWhiteSpace(club.Name))
            .GroupBy(club => club.Name.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.CurrentCultureIgnoreCase);

        TermineClub Club(string name)
        {
            var trimmed = name.Trim();
            if (known.TryGetValue(trimmed, out var existing))
            {
                existing.Name = trimmed;
                return existing;
            }

            var created = new TermineClub
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = trimmed
            };
            known[trimmed] = created;
            return created;
        }

        // Keep the ids of pairings that already exist, so a re-import does not
        // detach their booked Termine.
        var existingIds = (tenant.Matches ?? [])
            .GroupBy(match => match.HomeClubId + "|" + match.GuestClubId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new Queue<string>(group.Select(match => match.Id)),
                StringComparer.OrdinalIgnoreCase);

        var matches = new List<TermineMatch>();
        var clubs = new Dictionary<string, TermineClub>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var home = Club(row.Home);
            var guest = Club(row.Guest);
            clubs[home.Id] = home;
            clubs[guest.Id] = guest;
            var pairKey = home.Id + "|" + guest.Id;
            var id = existingIds.TryGetValue(pairKey, out var queue) && queue.Count > 0
                ? queue.Dequeue()
                : Guid.NewGuid().ToString("N");
            matches.Add(new TermineMatch
            {
                Id = id,
                HomeClubId = home.Id,
                GuestClubId = guest.Id,
                Location = row.Location.Trim()
            });
        }

        tenant.Clubs = clubs.Values.ToList();
        tenant.HomeClubs = null;
        tenant.GuestClubs = null;
        tenant.Matches = matches;
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static int HeaderIndex(IReadOnlyList<string> header, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            var index = header.ToList().FindIndex(cell => cell == name);
            if (index >= 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static string Cell(IReadOnlyList<string> cells, int index) =>
        index >= 0 && index < cells.Count ? cells[index].Trim() : "";

    private static List<string> ParseLine(string line, char delimiter)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i += 1;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == delimiter && !inQuotes)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        result.Add(current.ToString().Trim());
        return result;
    }
}
