using System.Text.RegularExpressions;

namespace mission_extractor.Services;

public record CardEntry(int CardId, int CardValue);

public class CardMappingService
{
    private static readonly Regex StrippedTitlePattern =
        new(@"\b(CL|SP|RP|1B|2B|3B|SS|LF|CF|RF|DH|C)\s.+$", RegexOptions.Compiled);

    private readonly Dictionary<string, CardEntry> _cards;
    private readonly Dictionary<int, string> _titleById;
    private readonly Dictionary<string, (CardEntry Entry, string FullTitle)> _cardsByStrippedTitle;

    public CardMappingService(string csvPath)
    {
        if (!File.Exists(csvPath))
            throw new FileNotFoundException($"shop_cards.csv not found at: {csvPath}");

        _cards = new Dictionary<string, CardEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in File.ReadAllLines(csvPath))
        {
            if (line.TrimStart().StartsWith("//"))
                continue;

            var parts = line.Split(',');
            if (parts.Length < 3)
                continue;

            var title = parts[0].Trim();
            if (!int.TryParse(parts[1].Trim(), out int cardId))
                continue;
            if (!int.TryParse(parts[2].Trim(), out int cardValue))
                continue;

            _cards[title] = new CardEntry(cardId, cardValue);
        }

        Console.WriteLine($"Loaded {_cards.Count} card entries from shop_cards.csv.");

        _titleById = new Dictionary<int, string>();
        foreach (var (title, entry) in _cards)
            _titleById.TryAdd(entry.CardId, title);

        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _cardsByStrippedTitle = new Dictionary<string, (CardEntry, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (title, entry) in _cards)
        {
            var stripped = ExtractStrippedTitle(title);
            if (stripped == null) continue;
            if (ambiguous.Contains(stripped)) continue;
            if (_cardsByStrippedTitle.ContainsKey(stripped))
            {
                _cardsByStrippedTitle.Remove(stripped);
                ambiguous.Add(stripped);
            }
            else
            {
                _cardsByStrippedTitle[stripped] = (entry, title);
            }
        }
        Console.WriteLine($"Built {_cardsByStrippedTitle.Count} stripped-title entries " +
                          $"({ambiguous.Count} ambiguous skipped).");
    }

    private static string? ExtractStrippedTitle(string fullTitle)
    {
        var match = StrippedTitlePattern.Match(fullTitle);
        return match.Success ? match.Value.TrimEnd() : null;
    }

    public IReadOnlyDictionary<string, CardEntry> Cards => _cards;

    public bool TryLookup(string title, out CardEntry entry) =>
        _cards.TryGetValue(title.Trim(), out entry!);

    public bool TryLookupById(int cardId, out string title) =>
        _titleById.TryGetValue(cardId, out title!);

    public bool TryLookupByStrippedTitle(string ocrText, out CardEntry entry, out string fullTitle)
    {
        if (_cardsByStrippedTitle.TryGetValue(ocrText.Trim(), out var found))
        {
            entry = found.Entry;
            fullTitle = found.FullTitle;
            return true;
        }
        entry = default!;
        fullTitle = default!;
        return false;
    }

    // Parses "{cardValue} {position} {playerName}" and finds a card by player name substring + card value.
    // Returns true only if exactly one card matches.
    private static readonly System.Text.RegularExpressions.Regex RewardCardTokenPattern =
        new(@"^(\d+)\s+\S+\s+(.+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public bool TryFuzzyLookupByValueAndName(string token, out CardEntry entry)
    {
        var match = RewardCardTokenPattern.Match(token.Trim());
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int cardValue))
        {
            entry = default!;
            return false;
        }

        var playerName = match.Groups[2].Value.Trim();
        var matches = _cards
            .Where(kvp => kvp.Value.CardValue == cardValue &&
                          kvp.Key.Contains(playerName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
        {
            entry = matches[0].Value;
            return true;
        }

        entry = default!;
        return false;
    }
}
