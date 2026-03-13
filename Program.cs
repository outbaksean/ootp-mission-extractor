using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using MissionExtractor.dto;
using mission_extractor.Models;
using mission_extractor.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuration is auto-loaded from appsettings.json by the Web SDK
var config = builder.Configuration;

var selectedProfileName = config["SelectedOOTPProfile"];
var selectedProfileSection = config
    .GetSection("OotpProfiles")
    .GetChildren()
    .FirstOrDefault(profile =>
        string.Equals(profile["Name"], selectedProfileName, StringComparison.OrdinalIgnoreCase));

var missionRowBoundries = selectedProfileSection?
    .GetSection("MissionRowBoundaries")
    .Get<MissionRowBoundries>() ?? new();

var debugImagesEnabled = config.GetValue<bool>("DebugImages");
var allowBoundryEdits = config.GetValue<bool>("AllowBoundryEdits");
var workingFilePath = Path.GetFullPath("missions-working.json", AppContext.BaseDirectory);
var outputDirectory = Path.GetFullPath(
    config["OutputSettings:OutputDirectory"] ?? "./output",
    AppContext.BaseDirectory);

var cardCsvPath = Path.GetFullPath("data/shop_cards.csv", AppContext.BaseDirectory);
var cardMappingService = new CardMappingService(cardCsvPath);

builder.Services.AddSingleton<MissionState>();
builder.Services.AddSingleton(missionRowBoundries);
builder.Services.AddSingleton(_ => new OcrCaptureService(debugImagesEnabled));
builder.Services.AddSingleton<MissionBoundryService>();
builder.Services.AddSingleton<MissionEtractionService>();
builder.Services.AddSingleton<LightweightValidationService>();
builder.Services.AddSingleton(cardMappingService);
builder.Services.AddSingleton<RewardMappingService>();
builder.Services.AddSingleton<FullTransformationService>();
builder.Services.AddSingleton<LoadVerifiedService>();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();

app.UseDefaultFiles(); // rewrites / to /index.html
app.UseStaticFiles(); // wwwroot/

var debugImagesDir = Path.Combine(AppContext.BaseDirectory, "debugImages");
Directory.CreateDirectory(debugImagesDir);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(debugImagesDir),
    RequestPath = "/debug-images"
});

var state = app.Services.GetRequiredService<MissionState>();
var extractionService = app.Services.GetRequiredService<MissionEtractionService>();
var validationService = app.Services.GetRequiredService<LightweightValidationService>();
var fullTransformService = app.Services.GetRequiredService<FullTransformationService>();
var loadVerifiedService = app.Services.GetRequiredService<LoadVerifiedService>();

// GET /api/cards
app.MapGet("/api/cards", () =>
    cardMappingService.Cards
        .Select(kvp => new { title = kvp.Key, cardId = kvp.Value.CardId, cardValue = kvp.Value.CardValue })
        .OrderBy(c => c.title)
        .ToList());

// GET /api/missions
app.MapGet("/api/missions", () =>
    new { count = state.Count, missions = state.Missions });

// PATCH /api/missions/{id}
app.MapMethods("/api/missions/{id}", ["PATCH"], (int id, MissionUpdateRequest req) =>
{
    var updated = state.TryUpdate(id, req.Name, req.Category, req.Reward, req.Status, req.MissionDetails, req.Rewards);
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});

// POST /api/capture
app.MapPost("/api/capture", async (CaptureRequest? req) =>
{
    int captureRow = req?.CaptureRow ?? 0;
    bool noImageOffsets = req?.NoImageOffsets ?? false;
    int before = state.Count;
    var log = await CaptureConsole(async () =>
        await extractionService.ExtractTopMissionStructureAndDetails(captureRow, noImageOffsets));
    int after = state.Count;
    object? addedMission = after > before ? (object)state.Missions[^1] : null;
    return new { log, missionCount = state.Count, addedMission };
});

// POST /api/capture-mission-type-details
app.MapPost("/api/capture-mission-type-details", async (CaptureRequest? req) =>
{
    int captureRow = req?.CaptureRow ?? 0;
    int before = state.Count;
    var log = await CaptureConsole(async () =>
        await extractionService.ExtractTopMissionStructureAndTypeDetails(captureRow));
    int after = state.Count;
    object? addedMission = after > before ? (object)state.Missions[^1] : null;
    return new { log, missionCount = state.Count, addedMission };
});

// POST /api/capture-details-bottom
app.MapPost("/api/capture-details-bottom", async (CaptureRequest? req) =>
{
    if (state.Count == 0)
        return Results.BadRequest(new { error = "No missions in memory." });

    int captureRow = req?.CaptureRow ?? 0;
    var lastMission = state.Missions[^1];
    var log = await CaptureConsole(async () =>
        await extractionService.ExtractMissionDetailsBottom(lastMission.Id, captureRow));

    return Results.Ok(new { log, missionCount = state.Count, updatedMission = state.Missions[^1] });
});

// POST /api/validate
app.MapPost("/api/validate", async () =>
{
    if (state.Count == 0)
        return Results.BadRequest(new { error = "No missions in memory to validate." });

    List<mission_extractor.Models.ValidationError> errors = null!;
    var log = await CaptureConsole(() =>
    {
        errors = validationService.Run();
        return Task.CompletedTask;
    });

    var errorDtos = errors.Select(e => new
    {
        missionId = e.Mission.Id,
        missionName = e.Mission.Name,
        errorType = e.ErrorType,
        imagePaths = e.ImagePaths?.Select(Path.GetFileName).ToList() ?? []
    }).ToList();

    return Results.Ok(new { log, missionCount = state.Count, errors = errorDtos });
});

// POST /api/transform
app.MapPost("/api/transform", async () =>
{
    if (state.Count == 0)
        return Results.BadRequest(new { error = "No missions in memory to transform." });

    List<mission_extractor.Models.ValidationError> errors = null!;
    var log = await CaptureConsole(() =>
    {
        errors = fullTransformService.Run();
        return Task.CompletedTask;
    });

    var errorDtos = errors.Select(e => new
    {
        missionId = e.Mission.Id,
        missionName = e.Mission.Name,
        errorType = e.ErrorType,
        imagePaths = e.ImagePaths?.Select(Path.GetFileName).ToList() ?? []
    }).ToList();

    return Results.Ok(new { log, missionCount = state.Count, errors = errorDtos });
});

// POST /api/reorder
app.MapPost("/api/reorder", () =>
{
    if (state.Count == 0)
        return Results.BadRequest(new { error = "No missions in memory to reorder." });

    var reordered = validationService.ReorderMissions(state.Missions.ToList());
    validationService.RegenerateIds(reordered);
    state.Replace(reordered);

    return Results.Ok(new { log = $"Reordered {reordered.Count} mission(s).", errors = Array.Empty<object>() });
});

// POST /api/load-verified
app.MapPost("/api/load-verified", async (HttpRequest req) =>
{
    JsonElement root;
    try
    {
        root = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" });
    }

    if (root.ValueKind != JsonValueKind.Object ||
        !root.TryGetProperty("missions", out var missionsEl) ||
        missionsEl.ValueKind != JsonValueKind.Array)
        return Results.BadRequest(new { error = "Expected { \"missions\": [...] } format." });

    List<Mission> missions;
    try
    {
        missions = missionsEl.Deserialize<List<Mission>>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? [];
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Failed to parse missions: {ex.Message}" });
    }

    if (missions.Count == 0)
        return Results.BadRequest(new { error = "No missions in file." });

    var result = loadVerifiedService.Load(missions);

    return Results.Ok(new
    {
        errors = result.Errors,
        loadedCount = result.LoadedCount,
        markedVerifiedCount = result.MarkedVerifiedCount,
        missionCount = state.Count
    });
});

// POST /api/load-final-format
app.MapPost("/api/load-final-format", async (HttpRequest req) =>
{
    JsonElement root;
    try
    {
        root = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" });
    }

    if (root.ValueKind != JsonValueKind.Object ||
        !root.TryGetProperty("missions", out var missionsEl) ||
        missionsEl.ValueKind != JsonValueKind.Array)
        return Results.BadRequest(new { error = "Expected { \"missions\": [...] } format." });

    List<Mission> missions;
    try
    {
        missions = missionsEl.Deserialize<List<Mission>>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? [];
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Failed to parse missions: {ex.Message}" });
    }

    if (missions.Count == 0)
        return Results.BadRequest(new { error = "No missions in file." });

    var result = loadVerifiedService.LoadFinalFormat(missions);

    return Results.Ok(new
    {
        errors = result.Errors,
        loadedCount = result.LoadedCount,
        markedVerifiedCount = result.MarkedVerifiedCount,
        missionCount = state.Count
    });
});

// POST /api/load-and-clean-final-format
app.MapPost("/api/load-and-clean-final-format", async (HttpRequest req) =>
{
    JsonElement root;
    try
    {
        root = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" });
    }

    if (root.ValueKind != JsonValueKind.Object ||
        !root.TryGetProperty("missions", out var missionsEl) ||
        missionsEl.ValueKind != JsonValueKind.Array)
        return Results.BadRequest(new { error = "Expected { \"missions\": [...] } format." });

    List<Mission> missions;
    try
    {
        missions = missionsEl.Deserialize<List<Mission>>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? [];
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Failed to parse missions: {ex.Message}" });
    }

    if (missions.Count == 0)
        return Results.BadRequest(new { error = "No missions in file." });

    var result = loadVerifiedService.LoadAndCleanFinalFormat(missions);

    return Results.Ok(new
    {
        errors = result.Errors,
        loadedCount = result.LoadedCount,
        markedVerifiedCount = result.MarkedVerifiedCount,
        missionCount = state.Count
    });
});

// POST /api/load-verified-clean
app.MapPost("/api/load-verified-clean", async (HttpRequest req) =>
{
    JsonElement root;
    try
    {
        root = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" });
    }

    if (root.ValueKind != JsonValueKind.Object ||
        !root.TryGetProperty("missions", out var missionsEl) ||
        missionsEl.ValueKind != JsonValueKind.Array)
        return Results.BadRequest(new { error = "Expected { \"missions\": [...] } format." });

    List<Mission> missions;
    try
    {
        missions = missionsEl.Deserialize<List<Mission>>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? [];
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Failed to parse missions: {ex.Message}" });
    }

    if (missions.Count == 0)
        return Results.BadRequest(new { error = "No missions in file." });

    var result = loadVerifiedService.Load(missions, clean: true);

    return Results.Ok(new
    {
        errors = result.Errors,
        loadedCount = result.LoadedCount,
        markedVerifiedCount = result.MarkedVerifiedCount,
        missionCount = state.Count
    });
});

// POST /api/save-verified
app.MapPost("/api/save-verified", async () =>
{
    var verifiedMissions = state.Missions.Where(m => m.Verified).ToList();
    if (verifiedMissions.Count == 0)
        return Results.BadRequest(new { error = "No verified missions to save." });

    Directory.CreateDirectory(outputDirectory);
    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    var filePath = Path.Combine(outputDirectory, $"missions_verified_{timestamp}.json");

    var missions = verifiedMissions.Select(m => new
    {
        id = m.Id,
        name = m.Name,
        type = m.Type?.ToString()?.ToLowerInvariant(),
        requiredCount = m.RequiredCount,
        reward = m.Reward,
        category = m.Category,
        cards = m.Cards,
        missionIds = m.MissionIds,
        totalPoints = m.TotalPoints,
        rewards = m.Rewards
    });

    var wrapper = new
    {
        version = DateTime.Now.ToString("yyyy-MM-dd"),
        missions
    };

    var options = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    var json = JsonSerializer.Serialize(wrapper, options);
    await File.WriteAllTextAsync(filePath, json);

    return Results.Ok(new { fileName = Path.GetFileName(filePath), count = verifiedMissions.Count });
});

// POST /api/save-working
app.MapPost("/api/save-working", async () =>
{
    if (state.Count == 0)
        return Results.BadRequest(new { error = "No missions in memory to save." });

    var log = await CaptureConsole(async () =>
        await extractionService.SaveToPath(workingFilePath));

    return Results.Ok(new { log });
});

// POST /api/load-working
app.MapPost("/api/load-working", async () =>
{
    if (!File.Exists(workingFilePath))
        return Results.BadRequest(new { error = $"Working missions file not found: {workingFilePath}" });

    var log = await CaptureConsole(async () =>
        await extractionService.LoadUnstructuredMissions(workingFilePath));

    return Results.Ok(new { log, missionCount = state.Count });
});

// POST /api/missions/import
app.MapPost("/api/missions/import", (List<Mission> missions) =>
{
    state.Replace(missions);
    return new { missionCount = state.Count };
});

// POST /api/missions/{id}/verify
app.MapPost("/api/missions/{id}/verify", (int id, VerifyRequest req) =>
{
    var updated = state.SetVerified(id, req.Verified);
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});

// DELETE /api/missions/{id}
app.MapDelete("/api/missions/{id}", (int id) =>
{
    var removed = state.Remove(id);
    return removed
        ? Results.Ok(new { missionCount = state.Count })
        : Results.NotFound();
});

// DELETE /api/missions
app.MapDelete("/api/missions", () =>
{
    state.Clear();
    return new { missionCount = state.Count };
});

// GET /api/boundaries
app.MapGet("/api/boundaries", () => new { allowBoundryEdits, boundaries = missionRowBoundries });

// POST /api/boundaries
app.MapPost("/api/boundaries", ([FromBody] MissionRowBoundries updated) =>
{
    missionRowBoundries.TopRowOffset = updated.TopRowOffset;
    missionRowBoundries.TopRow = updated.TopRow;
    missionRowBoundries.RowHeight = updated.RowHeight;
    missionRowBoundries.NumRows = updated.NumRows;
    missionRowBoundries.CategoryLeft = updated.CategoryLeft;
    missionRowBoundries.CategoryRight = updated.CategoryRight;
    missionRowBoundries.TitleLeft = updated.TitleLeft;
    missionRowBoundries.TitleRight = updated.TitleRight;
    missionRowBoundries.RewardLeft = updated.RewardLeft;
    missionRowBoundries.RewardRight = updated.RewardRight;
    missionRowBoundries.StatusLeft = updated.StatusLeft;
    missionRowBoundries.StatusRight = updated.StatusRight;
    missionRowBoundries.DetailUpperOffsetY = updated.DetailUpperOffsetY;
    missionRowBoundries.DetailSkipY = updated.DetailSkipY;
    missionRowBoundries.DetailNoImagesHeight = updated.DetailNoImagesHeight;
    missionRowBoundries.DetailHeight = updated.DetailHeight;
    missionRowBoundries.DetailLowerOffsetY = updated.DetailLowerOffsetY;
    missionRowBoundries.DetailLeft = updated.DetailLeft;
    missionRowBoundries.DetailWidth = updated.DetailWidth;
    missionRowBoundries.DetailColumns = updated.DetailColumns;
    return missionRowBoundries;
});

// DELETE /api/debug-images
app.MapDelete("/api/debug-images", () =>
{
    if (!Directory.Exists(debugImagesDir))
        return new { deletedCount = 0 };

    var files = Directory.GetFiles(debugImagesDir);
    foreach (var file in files)
        File.Delete(file);

    return new { deletedCount = files.Length };
});

app.Start();
var appUrl = app.Urls.FirstOrDefault() ?? "http://localhost:60564";
Process.Start(new ProcessStartInfo { FileName = appUrl, UseShellExecute = true });
await app.WaitForShutdownAsync();

static async Task<string> CaptureConsole(Func<Task> action)
{
    var sw = new StringWriter();
    var prev = Console.Out;
    Console.SetOut(sw);
    try { await action(); }
    finally { Console.SetOut(prev); }
    return sw.ToString();
}

record CaptureRequest(int CaptureRow = 0, bool NoImageOffsets = false);
record MissionUpdateRequest(string? Name, string? Category, string? Reward,
                             string? Status, List<string>? MissionDetails, List<MissionReward>? Rewards);
record VerifyRequest(bool Verified);
