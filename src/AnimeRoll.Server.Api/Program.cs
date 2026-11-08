using System.Text.Json.Serialization;
using AnimeRoll.Server.Api.AniList;
using AnimeRoll.Server.Api.Auth;
using AnimeRoll.Server.Api.Data;
using AnimeRoll.Server.Api.Downloads;
using AnimeRoll.Server.Api.Library;
using AnimeRoll.Server.Api.Options;
using AnimeRoll.Server.Api.Plugins;
using AnimeRoll.Server.Api.Providers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var section = builder.Configuration.GetSection(AnimeRollOptions.Section);
builder.Services.Configure<AnimeRollOptions>(section);
var cfg = section.Get<AnimeRollOptions>() ?? new AnimeRollOptions();

if (cfg.Jwt.Key.Length < 32)
    throw new InvalidOperationException("Imposta AnimeRoll__Jwt__Key con almeno 32 caratteri casuali.");
AnimeRoll.Server.Api.Infrastructure.DiskSpace.Configure(cfg);
Directory.CreateDirectory(cfg.DataPath);
Directory.CreateDirectory(cfg.MediaPath);

// Big uploads of local files (up to 20 GB).
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 20L * 1024 * 1024 * 1024);
builder.Services.Configure<FormOptions>(f => f.MultipartBodyLengthLimit = 20L * 1024 * 1024 * 1024);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={Path.Combine(cfg.DataPath, "animeroll.db")}"));
builder.Services.AddMemoryCache();

// Behind the reverse proxy (see proxy/): the real client address comes from X-Forwarded-For.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear(); o.KnownProxies.Clear();
});
// Login endpoints are reachable from the internet: slow down password guessing per client address.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = TokenService.Issuer,
        ValidAudience = TokenService.Issuer,
        IssuerSigningKey = TokenService.SigningKey(cfg.Jwt.Key),
        NameClaimType = "unique_name",
    };
});
builder.Services.AddAuthorization();

builder.Services.AddFido2(o =>
{
    o.RPID = cfg.Passkey.ServerDomain;
    o.RPName = cfg.Passkey.ServerName;
    o.Origins = cfg.Passkey.Origins.ToHashSet();
});

builder.Services.AddSingleton<TokenService>();

builder.Services.AddHttpClient<AniListClient>();
builder.Services.AddHttpClient<QBittorrentClient>();
builder.Services.AddHttpClient("plugins", c => { c.Timeout = Timeout.InfiniteTimeSpan; c.DefaultRequestHeaders.UserAgent.ParseAdd("AnimeRoll-Plugin/1.0"); });
builder.Services.AddSingleton<TelegramService>();
builder.Services.AddSingleton<TelegramProvider>();
builder.Services.AddSingleton<PluginHost>();

LocalProviders.Register(builder.Services);
builder.Services.AddTransient<ISourceProvider>(sp => sp.GetRequiredService<TelegramProvider>());
builder.Services.AddTransient<ProviderRegistry>();

builder.Services.AddScoped<MediaPipeline>();
builder.Services.AddSingleton<DownloadQueue>();
builder.Services.AddSingleton<QueueSettingsStore>();
builder.Services.AddHostedService<DownloadWorker>();
builder.Services.AddHostedService<SubtitleBackfill>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(cfg.Passkey.Origins.ToArray()).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.EnsureCreated();
    SchemaUpgrade.Apply(db, cfg.MediaPath);
}
await app.Services.GetRequiredService<PluginHost>().LoadAsync(CancellationToken.None);

app.UseForwardedHeaders();
app.UseRateLimiter();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { ok = true }));

// Needed for passkeys in the iOS/tvOS apps (Associated Domains: webcredentials:<domain>).
app.MapGet("/.well-known/apple-app-site-association", (IOptions<AnimeRollOptions> o) =>
    Results.Json(new { webcredentials = new { apps = o.Value.Apple.AppIds } }));

// Needed for passkeys in the Android app (Credential Manager).
app.MapGet("/.well-known/assetlinks.json", (IOptions<AnimeRollOptions> o) => Results.Json(new[]
{
    new
    {
        relation = new[] { "delegate_permission/common.handle_all_urls", "delegate_permission/common.get_login_creds" },
        target = new { @namespace = "android_app", package_name = o.Value.Android.PackageName,
                       sha256_cert_fingerprints = o.Value.Android.Sha256CertFingerprints },
    },
}));

app.MapAuth();
app.MapAniList();
app.MapLibrary();
app.MapMedia();
app.MapSourcesAndDownloads();
app.MapRepair();
app.MapSubtitleRefresh();
app.MapMoveEpisodes();
app.MapEditEpisode();
app.MapEditSeries();
app.MapPlugins();

// Web client (AnimeRoll.Client/Web build) is copied to wwwroot in the Docker image.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

// Series created while AniList was down have no poster: complete them in the background once it answers.
_ = Task.Run(async () =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        var n = await AnimeRoll.Server.Api.Library.EditSeries.FillMissingAsync(scope.ServiceProvider.GetRequiredService<AppDb>(),
            scope.ServiceProvider.GetRequiredService<AnimeRoll.Server.Api.AniList.AniListClient>(),
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("EditSeries"), CancellationToken.None);
        if (n > 0) app.Logger.LogInformation("Metadati completati per {Count} serie", n);
    }
    catch (Exception e) { app.Logger.LogWarning(e, "Completamento metadati non riuscito"); }
});

app.Run();
