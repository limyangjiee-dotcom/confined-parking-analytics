using Microsoft.EntityFrameworkCore;
using ParkingApiPg.Data;
using ParkingApiPg.Services;

// Be lenient about DateTime kinds so DateTime.Now works with PostgreSQL 'timestamp' columns
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);
// local, gitignored config for secrets (e.g. the Gemini API key: { "Ai": { "ApiKey": "..." } })
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<ParkingDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddHttpClient();   // for pulling from an external parking-system REST API

// connector + platform settings + scheduled sync
builder.Services.AddTransient<IngestionService>();      // transient: resolvable from the background service's root provider
builder.Services.AddTransient<AggregationService>();    // rebuilds summary tables from imported data
builder.Services.AddTransient<ForecastService>();       // auto-runs the ML forecast after a sync
builder.Services.AddTransient<EventFeedService>();      // imports planned events from an iCal feed
builder.Services.AddTransient<AiService>();             // AI summaries / Q&A over aggregated analytics (Gemini)
builder.Services.AddSingleton<SettingsService>();       // cached platform settings
builder.Services.AddHostedService<SyncBackgroundService>();
builder.Services.AddHostedService<EventFeedBackgroundService>();   // auto-imports the iCal event feed

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// API-key gate for /api/* — everything under /api requires the master X-Api-Key
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        var master = app.Configuration["ApiKey"];
        var hasMaster = ctx.Request.Headers.TryGetValue("X-Api-Key", out var key) && key == master;
        if (!hasMaster)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "Missing or invalid X-Api-Key header" });
            return;
        }
    }
    await next();
});

app.MapControllers();

// create the Live_Parking table on first run (historical tables already exist);
// the connector/settings tables are created explicitly because EnsureCreated skips an existing DB
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ParkingDbContext>();
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw("""
        -- saved connection + field mapping to an external parking system's API
        CREATE TABLE IF NOT EXISTS "Data_Source_Config" (
            "Id"          int PRIMARY KEY DEFAULT 1,
            "Config_Json" text NOT NULL DEFAULT '',
            "Last_Sync"   timestamptz NULL,
            "Last_Status" text NOT NULL DEFAULT 'never synced'
        );
        -- platform-side settings (capacity, auto-sync interval)
        CREATE TABLE IF NOT EXISTS "App_Settings" (
            "Key"   text PRIMARY KEY,
            "Value" text NOT NULL
        );
        -- Event calendar feed connected by default (demo source = the mock vendor's
        -- iCal endpoint). Change the URL on the Data Source page to a real Google
        -- Calendar .ics. DO NOTHING so a user's chosen URL is never overwritten.
        INSERT INTO "App_Settings" ("Key","Value")
        VALUES ('ical_url','http://localhost:8900/calendar.ics'),
               ('ical_refresh_seconds','3600')
        ON CONFLICT ("Key") DO NOTHING;
        """);
    // load platform settings (capacity, auto-sync interval) into the cache at startup
    scope.ServiceProvider.GetRequiredService<SettingsService>().Reload();
}

app.Run();
