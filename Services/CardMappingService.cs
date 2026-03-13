namespace mission_extractor.Services;

public record CardEntry(int CardId, int CardValue);

public class CardMappingService
{
    private readonly Dictionary<string, CardEntry> _cards;
    private readonly Dictionary<int, string> _titleById;

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
    }

    public IReadOnlyDictionary<string, CardEntry> Cards => _cards;

    public bool TryLookup(string title, out CardEntry entry) =>
        _cards.TryGetValue(title.Trim(), out entry!);

    public bool TryLookupById(int cardId, out string title) =>
        _titleById.TryGetValue(cardId, out title!);

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
