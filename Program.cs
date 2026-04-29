using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var connectionString = "Host=db.sdemibvbmwigtzsnlglu.supabase.co;Port=5432;Database=postgres;Username=postgres;Password=t-C!J%e$uvX69R6;"; 
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

app.Run();

// --- DATABASE MODELS ---
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    
    public DbSet<Country> Countries { get; set; }
    public DbSet<RunTelemetry> PlayerRuns { get; set; } // Added the new table

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