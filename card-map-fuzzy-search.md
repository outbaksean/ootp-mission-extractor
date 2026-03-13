# Plan: Stripped-Title Fallback for Card Mapping

## Context

When `TransformPass1` maps OCR-extracted mission detail text to card IDs, it uses an
exact case-insensitive match against the full card title from `shop_cards.csv`
(e.g. `"Theme Week - Historical All-Star RP Jeff Zimmerman TEX 1999"`). The game's
mission detail grid does not show the event/set prefix, so OCR produces something like
`"RP Jeff Zimmerman TEX 1999"` — causing a "Card Not Found" error even though the
card exists. The fix is a secondary lookup keyed on the stripped title (everything from
the position abbreviation onwards), built at startup and tried when the exact match fails.

## Files to modify

- `Services/CardMappingService.cs`
- `Services/FullTransformationService.cs`

`shop_cards.csv` is **not changed** — stripping is computed at runtime.

---

## 1. CardMappingService.cs

### 1a. Regex constant (add near top of class)

```csharp
private static readonly Regex StrippedTitlePattern =
    new(@"\b(CL|SP|RP|1B|2B|3B|SS|LF|CF|RF|DH|C)\s.+$", RegexOptions.Compiled);
```

Order matters: `CL` before `C` so `CL Smith` matches `CL`, not `C`.

### 1b. Static helper

```csharp
private static string? ExtractStrippedTitle(string fullTitle)
{
    var match = StrippedTitlePattern.Match(fullTitle);
    return match.Success ? match.Value.TrimEnd() : null;
}
```

### 1c. New field (alongside `_cards` and `_titleById`)

```csharp
private readonly Dictionary<string, (CardEntry Entry, string FullTitle)> _cardsByStrippedTitle;
```

### 1d. Populate in constructor (after `_titleById` is built)

Track and exclude any stripped title that maps to more than one card (ambiguous):

```csharp
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
```

### 1e. New public method

```csharp
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
```

---

## 2. FullTransformationService.cs — TransformPass1

Add the fallback after each `TryLookup` failure, for both Count and Points branches.
A stripped-title hit logs a console warning (visible in the UI log panel via `CaptureConsole`)
but does **not** add to `errors`, so the mission is not marked invalid.

### Count branch (replace the `else` block)

```csharp
// before:
else
{
    cards.Add(new MissionCard { CardId = 0 });
    errors.Add(new ValidationError(mission, $"Card Not Found: {detail}", ...));
}

// after:
else if (_cardMapping.TryLookupByStrippedTitle(detail, out var strippedEntry, out var strippedTitle))
{
    cards.Add(new MissionCard { CardId = strippedEntry.CardId });
    Console.WriteLine($"[Stripped Title Match] '{detail}' -> '{strippedTitle}'");
}
else
{
    cards.Add(new MissionCard { CardId = 0 });
    errors.Add(new ValidationError(mission, $"Card Not Found: {detail}", DetailImage(detailImages, i)));
}
```

### Points branch (replace the `else` block that sets `cardId = 0`)

Same pattern, operating on `cleanTitle`:

```csharp
else if (_cardMapping.TryLookupByStrippedTitle(cleanTitle, out var strippedEntry, out var strippedTitle))
{
    cardId = strippedEntry.CardId;
    mappedPoints = CardValueToPoints(strippedEntry.CardValue);
    Console.WriteLine($"[Stripped Title Match] '{cleanTitle}' -> '{strippedTitle}'");
}
else
{
    cardId = 0;
    mappedPoints = 0;
    errors.Add(new ValidationError(mission, $"Card Not Found: {cleanTitle}", DetailImage(detailImages, i)));
}
```

The existing points-mismatch check that follows is unchanged — it still fires if
`extractedPoints != mappedPoints`.

---

## Out of scope

- `RewardMappingService` — has its own fuzzy lookup (`TryFuzzyLookupByValueAndName`)
  and handles a different input format; no change needed.
- `LoadVerifiedService` — uses reverse lookup (card ID → title); no change needed.
- `shop_cards.csv` — no new column for now; add later if manual override becomes necessary.

---

## Verification

1. `dotnet build mission-extractor.csproj` — should compile with 0 errors.
2. On startup, the console should print the stripped-title build summary line.
3. Run Transform on a mission set that previously had "Card Not Found" errors for
   event-prefixed cards (e.g. `"Theme Week - ..."` or `"T4 Ep. X - ..."`). Errors
   should be gone and `[Stripped Title Match]` lines should appear in the log.
4. Confirm that genuinely missing cards still produce "Card Not Found" errors.
5. Confirm that missions with only stripped-title hits are marked verified (no errors).
