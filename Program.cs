using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// The API will now look for a secure Environment Variable inside Render
var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL");

if (string.IsNullOrEmpty(connectionString))
{
    throw new Exception("CRITICAL: Database connection string is missing!");
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString, o => o.UseNetTopologySuite()));

var app = builder.Build();
app.UseCors("AllowAll");

// ENDPOINT 1: The Spatial Engine (Unchanged)
app.MapGet("/api/distance", async (string guess, string target, AppDbContext db) =>
{
    var guessCountry = await db.Countries.FirstOrDefaultAsync(c => c.Name.ToLower() == guess.ToLower());
    var targetCountry = await db.Countries.FirstOrDefaultAsync(c => c.Name.ToLower() == target.ToLower());

    if (guessCountry == null || targetCountry == null)
        return Results.BadRequest(new { error = "Country not found in database." });

    if (guessCountry.Id == targetCountry.Id)
        return Results.Ok(new { distanceKm = 0 });

    var query = @"
        SELECT (ST_Distance(c1.geom::geography, c2.geom::geography) / 1000.0) AS ""Value""
        FROM countries c1, countries c2
        WHERE c1.id = {0} AND c2.id = {1}";

    var distance = await db.Database.SqlQueryRaw<double>(query, guessCountry.Id, targetCountry.Id).FirstOrDefaultAsync();
    return Results.Ok(new { distanceKm = Math.Round(distance) });
});

// ENDPOINT 2: The Telemetry Catcher (NEW!)
app.MapPost("/api/telemetry", async (RunTelemetry runData, AppDbContext db) =>
{
    // PostgreSQL handles the 'played_at' timestamp automatically, so we let the DB handle the time
    runData.PlayedAt = DateTime.UtcNow; 
    
    db.PlayerRuns.Add(runData);
    await db.SaveChangesAsync();
    
    return Results.Ok(new { message = "Run logged successfully!" });
});

// --- GLOBAL LEADERBOARD ENDPOINT ---
app.MapGet("/api/leaderboard", async (AppDbContext db, string mode = "endless") =>
{
    try
    {
        var topRuns = await db.PlayerRuns
            // Endless runs end on a loss. 
            // We ensure RemainingHealth (Depth) is > 0 so we don't pull your old test data!
            .Where(r => r.GameMode == mode && r.IsWin == false && r.RemainingHealth > 0)
            // 1st Priority: Highest Depth (Hijacked Health Column)
            .OrderByDescending(r => r.RemainingHealth)
            // Tiebreaker: Fewest Total Guesses
            .ThenBy(r => r.GuessCount)
            .Take(10)
            .Select(r => new {
                depth = r.RemainingHealth,
                guesses = r.GuessCount,
                date = r.PlayedAt
            })
            .ToListAsync();

        return Results.Ok(topRuns);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to fetch leaderboard: {ex.Message}");
    }
});

// --- GLOBAL STATS ENDPOINT (TODAY ONLY) ---
app.MapGet("/api/globalstats", async (AppDbContext db) =>
{
    try
    {
        // Get midnight today (UTC)
        var today = DateTime.UtcNow.Date;

        // Calculate the average guess count for successful Daily runs that happened TODAY
        var todayRuns = db.PlayerRuns
            .Where(r => r.GameMode == "daily" && r.IsWin == true && r.PlayedAt >= today);

        // We have to check if anyone has actually played today yet to avoid a divide-by-zero error!
        var count = await todayRuns.CountAsync();
        
        double globalAverage = 0.0;
        if (count > 0)
        {
            globalAverage = await todayRuns.AverageAsync(r => (double?)r.GuessCount) ?? 0.0;
        }

        return Results.Ok(new { 
            averageGuesses = Math.Round(globalAverage, 2) 
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to fetch global stats: {ex.Message}");
    }
});

// --- GET PLAYER PROFILE ---
app.MapGet("/api/profile/{userId}", async (Guid userId, AppDbContext db) =>
{
    try
    {
        var profile = await db.PlayerProfiles.FindAsync(userId);
        return profile is not null 
            ? Results.Ok(new { statsJson = profile.StatsJson, storyJson = profile.StoryJson }) 
            : Results.NotFound();
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to fetch profile: {ex.Message}");
    }
});

// --- SAVE PLAYER PROFILE ---
app.MapPost("/api/profile", async (PlayerProfile incomingProfile, AppDbContext db) =>
{
    try
    {
        var existingProfile = await db.PlayerProfiles.FindAsync(incomingProfile.UserId);
        
        if (existingProfile is null)
        {
            // First time saving, create new row
            db.PlayerProfiles.Add(incomingProfile);
        }
        else
        {
            // Returning player, update existing stats
            existingProfile.StatsJson = incomingProfile.StatsJson;
            existingProfile.StoryJson = incomingProfile.StoryJson; // <-- NEW: Save the Story!
        }
        
        await db.SaveChangesAsync();
        return Results.Ok(new { message = "Cloud sync successful" });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to sync profile: {ex.Message}");
    }
});

app.Run();

// --- DATABASE MODELS ---
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    
    public DbSet<Country> Countries { get; set; }
    public DbSet<RunTelemetry> PlayerRuns { get; set; } // Added the new table
    public DbSet<PlayerProfile> PlayerProfiles { get; set; } // New table for player profiles

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("postgis"); 
        
        // Map Countries
        modelBuilder.Entity<Country>().ToTable("countries");
        modelBuilder.Entity<Country>().Property(c => c.Id).HasColumnName("id");
        modelBuilder.Entity<Country>().Property(c => c.Name).HasColumnName("name");
        modelBuilder.Entity<Country>().Property(c => c.Geom).HasColumnName("geom");

        // Map Player Runs (NEW!)
        modelBuilder.Entity<RunTelemetry>().ToTable("player_runs");
        modelBuilder.Entity<RunTelemetry>().Property(r => r.Id).HasColumnName("id");
        modelBuilder.Entity<RunTelemetry>().Property(r => r.GameMode).HasColumnName("game_mode");
        modelBuilder.Entity<RunTelemetry>().Property(r => r.IsWin).HasColumnName("is_win");
        modelBuilder.Entity<RunTelemetry>().Property(r => r.GuessCount).HasColumnName("guess_count");
        modelBuilder.Entity<RunTelemetry>().Property(r => r.RemainingHealth).HasColumnName("remaining_health");
        modelBuilder.Entity<RunTelemetry>().Property(r => r.PlayedAt).HasColumnName("played_at");
    }
}

public class Country
{
    public int Id { get; set; }
    public string Name { get; set; }
    public MultiPolygon Geom { get; set; }
}

// The new Telemetry Model
public class RunTelemetry
{
    public int Id { get; set; }
    public string GameMode { get; set; }
    public bool IsWin { get; set; }
    public int GuessCount { get; set; }
    public int RemainingHealth { get; set; }
    public DateTime PlayedAt { get; set; }
}

// Add this right below RunTelemetry
[Table("player_profiles")]
public class PlayerProfile
{
    [Key]
    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("stats_json", TypeName = "jsonb")]
    public string StatsJson { get; set; } = "{}";

    // --- NEW: The Campaign Vault ---
    [Column("story_json", TypeName = "jsonb")]
    public string StoryJson { get; set; } = "{}";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}