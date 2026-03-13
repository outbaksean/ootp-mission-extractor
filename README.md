# mission-extractor

A .NET 10 web app that extracts OOTP 27 mission data from screenshots of the running game client using region-based OCR, then serializes the results to `missions.json` for use by the ootp-missions-27 web app.

Run with `dotnet run` — a browser window opens automatically at the URL shown in the terminal output.

## Getting missions from the game

The tool reads pixel regions directly from the OOTP client window. Before capturing:

1. Open OOTP 27 and navigate to the **Missions** screen.
2. Click on the first mission to expand its detail card grid below the mission list.
3. Ensure the OOTP window is on the primary display at the expected resolution (matching the pixel boundaries in `appsettings.json`).
4. Run the extractor (`dotnet run`) — a browser tab opens automatically.
5. For each mission:
   - In OOTP, click the mission row so its detail cards are visible.
   - In the browser UI, click **Capture**. The tool screenshots the OOTP window and OCRs the selected row and its card grid.
   - If the mission has more cards below the initially visible area, scroll OOTP down and click **Capture Details (Bottom)** to append the lower card rows.
   - For `missions`-type missions (where the detail area lists sub-mission names rather than cards), use **Capture Mission Type Details** instead of the normal Capture.
6. Review OCR results in the browser. Correct any misread fields inline and click save.
7. Once all missions are captured, click **Validate** then **Transform** to produce the final `missions.json`.

> **Pixel calibration:** If captures are blank or misaligned, enable `DebugImages` in `appsettings.json` and inspect the saved crop images. Adjust `MissionRowBoundaries` values in the active profile until the crops align with the correct screen regions.

## How it works

1. The app takes a screenshot of the OOTP client window.
2. Pixel boundaries configured in `appsettings.json` define the screen regions for each data cell (category, title, reward, status, and mission detail cards).
3. Each region is cropped and passed through OCR (`OcrCaptureService`) to extract text.
4. The extracted text is assembled into `Mission` DTOs held in memory.
5. The browser UI lets you review and edit all OCR-extracted fields inline before running the next pipeline step.
6. Validate and Transform steps clean the data and map card names to IDs in memory. Use Save Verified to write the output file.

OCR runs cell-by-cell rather than on the full screen to improve accuracy. The tool stops scanning rows automatically when it encounters several consecutive empty captures.

## Browser UI

The UI is a single page served from `wwwroot/index.html`. It shows all missions currently in memory and lets you:

- Edit any field (name, category, reward, status, missionDetails) and save changes without touching JSON files.
- Run pipeline actions (Capture, Validate, Transform, Save/Load Unstructured) via buttons; captured console output appears in the log area.
- View debug images inline next to each field when `DebugImages` is enabled in config.

## Pipeline actions

| Button | Description |
|--------|-------------|
| Capture | Screenshots the OOTP window and OCRs the top mission row plus its detail card grid |
| Capture Mission Type Details | Like Capture, but reads sub-mission names (row-based regions) instead of card grid cells — use for `missions`-type missions |
| Capture Details (Bottom) | Appends the lower detail card rows to the last captured mission (for missions with cards below the fold) |
| Validate | Runs lightweight cleanup (dedup, field cleaning, status parsing) and reports validation errors |
| Transform | Runs full transformation (card lookup, mission cross-references, topological sort) and updates missions in memory |
| Save Working | Writes current in-memory missions to `missions-working.json` for quick resume |
| Load Working | Loads `missions-working.json` back into memory |
| Save Verified | Writes all verified missions to a timestamped `missions_verified_{timestamp}.json` in the output directory |
| Load Verified | Merges a previously saved verified file into memory, marking those missions as verified |
| Load Verified (Clean) | Same as Load Verified but clears existing missions first |
| Delete Debug Images | Deletes all files in the `debugImages/` directory |

## API endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/missions` | Returns all missions in memory |
| GET | `/api/cards` | Returns the card catalog loaded from `shop_cards.csv` |
| PATCH | `/api/missions/{id}` | Updates fields on a single mission |
| POST | `/api/missions/{id}/verify` | Marks a mission as verified/unverified |
| DELETE | `/api/missions/{id}` | Removes a single mission from memory |
| DELETE | `/api/missions` | Clears all missions from memory |
| POST | `/api/capture` | OCRs the top mission row and its detail card grid |
| POST | `/api/capture-mission-type-details` | OCRs the top mission row with sub-mission name regions |
| POST | `/api/capture-details-bottom` | Appends lower detail card rows to the last mission |
| POST | `/api/validate` | Runs lightweight validation |
| POST | `/api/transform` | Runs full transformation and writes output JSON |
| POST | `/api/save-working` | Saves missions to `missions-working.json` |
| POST | `/api/load-working` | Loads `missions-working.json` into memory |
| POST | `/api/save-verified` | Writes verified missions to a timestamped output file |
| POST | `/api/load-verified` | Merges a verified missions file into memory |
| POST | `/api/load-verified-clean` | Replaces memory with a verified missions file |
| POST | `/api/load-final-format` | Loads a final-format missions file into memory |
| POST | `/api/load-and-clean-final-format` | Replaces memory with a final-format missions file |
| POST | `/api/missions/import` | Replaces all missions in memory with the provided list |
| GET | `/api/boundaries` | Returns the active pixel boundary config |
| POST | `/api/boundaries` | Updates the active pixel boundary config in memory |
| DELETE | `/api/debug-images` | Deletes all debug images |

## Configuration

`appsettings.json` controls all pixel boundaries and behavior. A profile named `SelectedOOTPProfile` is selected at startup; add additional profiles for different screen resolutions or window sizes.

### Key settings

| Setting | Description |
|---------|-------------|
| `DebugImages` | When true, saves cropped region images to `debugImages/` and serves them at `/debug-images/{filename}` |
| `SelectedOOTPProfile` | Name of the active profile |
| `MissionRowBoundaries` | Pixel coordinates for mission list rows and detail card grid |
| `OutputSettings:OutputDirectory` | Directory for saved JSON and report files; set to `./in-progress-missions` to keep output in the tracked folder |

### MissionRowBoundaries fields

| Field | Description |
|-------|-------------|
| `TopRow` | Y pixel of the first mission row |
| `TopRowOffset` | Y offset applied to the first row (used when the top row differs from subsequent rows) |
| `RowHeight` | Pixel height of each row |
| `NumRows` | Maximum number of rows to scan |
| `CategoryLeft/Right` | X bounds of the category column |
| `TitleLeft/Right` | X bounds of the mission title column |
| `RewardLeft/Right` | X bounds of the reward column |
| `StatusLeft/Right` | X bounds of the status column |
| `DetailUpperOffsetY` | Y offset from the mission row to the top of the detail card area |
| `DetailSkipY` | Y gap between detail card rows |
| `DetailNoImagesHeight` | Height of each detail cell when debug images are disabled |
| `DetailHeight` | Height of each detail cell when debug images are enabled |
| `DetailLowerOffsetY` | Y offset from the mission row to the bottom detail area (used by Capture Details Bottom) |
| `DetailLeft` | X start of the first detail card column |
| `DetailWidth` | Width of each detail card cell |
| `DetailColumns` | Number of detail card columns per row |

## DTOs

The intended output shape mirrors the `missions.json` schema consumed by the web app.

**Mission**

| Field | Type | Description |
|-------|------|-------------|
| `id` | int | Mission identifier |
| `name` | string | Mission display name |
| `type` | MissionType | `Count`, `Points`, or `Missions` |
| `requiredCount` | int | Number required to complete |
| `reward` | string | Reward description text |
| `category` | string | Mission category label |
| `cards` | MissionCard[] | Cards that count toward the mission |
| `missionIds` | int[]? | Refs to sub-mission IDs (missions-type only) |
| `totalPoints` | int | Point target for points-type missions |
| `rewards` | MissionReward[] | Structured reward list |
| `status` | string | Raw OCR status text (pre-transform only) |
| `missionDetails` | string[] | Raw OCR detail card text (pre-transform only) |
| `debugImages` | object | OCR crop paths keyed by field name (pre-transform only) |

**MissionCard**

| Field | Type | Description |
|-------|------|-------------|
| `cardId` | int | Card identifier |
| `points` | int? | Points value (omitted for count-type missions) |

**MissionReward**

| Field | Type | Description |
|-------|------|-------------|
| `type` | string | Reward type label |
| `packType` | PackType? | Pack type (omitted when not applicable) |
| `count` | int | Quantity rewarded |

## Project structure

```
mission-extractor/
  Program.cs                      Entry point, web host, API endpoints
  appsettings.json                Pixel boundary profiles and settings
  data/
    shop_cards.csv                Card catalog used for name-to-ID mapping (required)
  in-progress-missions/           Tracked in git; use this as the output directory to persist
                                  work-in-progress across machines and sessions
  wwwroot/
    index.html                    Single-page browser UI
  Enums/
    MissionType.cs
    PackType.cs
    RewardType.cs
  Models/
    CaptureRegionConfig.cs        Defines a screen region to capture
    CaptureResult.cs              OCR result for a single region
    MissionRowBoundries.cs        Deserialization target for appsettings profile
    MissionState.cs               In-memory mission list singleton
    ValidationError.cs            Error record used by validation services
    DTO/
      Mission.cs
      MissionCard.cs
      MissionReward.cs
  Services/
    MissionBoundryService.cs      Computes capture regions from boundary config
    OcrCaptureService.cs          Takes screenshots and runs OCR on regions
    ScreenshotService.cs          Raw screenshot capture
    MissionEtractionService.cs    Orchestrates row and detail extraction
    LightweightValidationService.cs  Dedup, cleaning, field validation
    FullTransformationService.cs  Card lookup, mission cross-references, output
    CardMappingService.cs         Loads shop_cards.csv and maps names to card IDs
    RewardMappingService.cs       Parses reward text and maps to structured MissionReward list
    LoadVerifiedService.cs        Merges or replaces in-memory missions from a saved file
```
