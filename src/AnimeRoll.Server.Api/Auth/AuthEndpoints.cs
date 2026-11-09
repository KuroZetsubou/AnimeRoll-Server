using System.Text;
using AnimeRoll.Server.Api.Data;
using AnimeRoll.Server.Api.Options;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AnimeRoll.Server.Api.Auth;

public sealed record PasswordLoginRequest(string Username, string Password);
public sealed record PasswordChange(string? Current, string NewPassword);
public sealed record AuthResponse(string AccessToken, UserDto User);
public sealed record UserDto(Guid Id, string Username, bool AniListConnected, string? AniListUserName, int PasskeyCount,
    int Avatar = 0, bool HasPasscode = false, string? Tracker = null, bool MalConnected = false, string? MalUserName = null);
public sealed record CeremonyStart(string CeremonyId, object Options);
public sealed record PasskeyRegisterFinish(string CeremonyId, AuthenticatorAttestationRawResponse Response, string? Name);
public sealed record PasskeyLoginStart(string? Username);
public sealed record PasskeyLoginFinish(string CeremonyId, AuthenticatorAssertionRawResponse Response);

/// Username + password login, plus passkeys (WebAuthn) for passwordless login.
/// Ceremony options are kept in memory for 5 minutes and referenced by an id,
/// so native apps don't need cookies.
public static class AuthEndpoints
{
    private static readonly PasswordHasher<User> Hasher = new();

    public static UserDto ToDto(this User u) =>
        new(u.Id, u.Username, u.AniListToken != null, u.AniListUserName, u.Passkeys.Count,
            u.Avatar, u.PasscodeHash != null, Tracking.TrackerService.ActiveId(u), u.MalAccessToken != null, u.MalUserName);

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("Auth");

        g.MapGet("/status", async (AppDb db, IOptions<AnimeRollOptions> o) => new
        {
            needsSetup = !await db.Users.AnyAsync(),
            registrationOpen = o.Value.AllowRegistration,
        });

        g.MapPost("/register", async (PasswordLoginRequest req, AppDb db, TokenService tokens, IOptions<AnimeRollOptions> o) =>
        {
            // The first account can always be created; afterwards only if registration is enabled.
            if (await db.Users.AnyAsync() && !o.Value.AllowRegistration) return Results.Forbid();
            if (req.Username.Length < 3 || req.Password.Length < 10)
                return Results.BadRequest(new { error = "Username min 3 caratteri, password min 10." });
            if (await db.Users.AnyAsync(u => u.Username == req.Username))
                return Results.Conflict(new { error = "Username già in uso." });

            var user = new User { Username = req.Username, PasswordHash = "" };
            user.PasswordHash = Hasher.HashPassword(user, req.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return Results.Ok(new AuthResponse(tokens.CreateAccessToken(user), user.ToDto()));
        }).RequireRateLimiting("auth");

        g.MapPost("/login", async (PasswordLoginRequest req, AppDb db, TokenService tokens) =>
        {
            var user = await db.Users.Include(u => u.Passkeys).FirstOrDefaultAsync(u => u.Username == req.Username);
            if (user is null || user.PasswordHash == "" || Hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password) == PasswordVerificationResult.Failed)
            {
                await Task.Delay(Random.Shared.Next(300, 700)); // slow down guessing
                return Results.Unauthorized();
            }
            return Results.Ok(new AuthResponse(tokens.CreateAccessToken(user), user.ToDto()));
        }).RequireRateLimiting("auth");

        // Set or change the password of the signed-in profile (profiles created from the app start without one).
        g.MapPut("/password", async (PasswordChange req, HttpContext ctx, AppDb db) =>
        {
            var user = await db.Users.FirstAsync(u => u.Id == ctx.User.UserId());
            if (req.NewPassword.Length < 10) return Results.BadRequest(new { error = "Password min 10 caratteri." });
            if (user.PasswordHash != "" && Hasher.VerifyHashedPassword(user, user.PasswordHash, req.Current ?? "") == PasswordVerificationResult.Failed)
            {
                await Task.Delay(Random.Shared.Next(300, 700));
                return Results.Unauthorized();
            }
            user.PasswordHash = Hasher.HashPassword(user, req.NewPassword);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("auth");

        g.MapGet("/me", async (HttpContext ctx, AppDb db) =>
        {
            var id = ctx.User.UserId();
            // Token of a deleted user (or from another database): ask the app to sign in again.
            var user = await db.Users.Include(u => u.Passkeys).FirstOrDefaultAsync(u => u.Id == id);
            return user is null ? Results.Unauthorized() : Results.Ok(user.ToDto());
        }).RequireAuthorization();

        // ---- Passkey registration (must be logged in with password first) ----
        g.MapPost("/passkeys/register/begin", async (HttpContext ctx, AppDb db, IFido2 fido2, IMemoryCache cache) =>
        {
            var id = ctx.User.UserId();
            var user = await db.Users.Include(u => u.Passkeys).FirstAsync(u => u.Id == id);
            var options = fido2.RequestNewCredential(new RequestNewCredentialParams
            {
                User = new Fido2User { Id = Encoding.UTF8.GetBytes(user.Id.ToString("N")), Name = user.Username, DisplayName = user.Username },
                ExcludeCredentials = user.Passkeys.Select(p => new PublicKeyCredentialDescriptor(p.CredentialId)).ToList(),
                AuthenticatorSelection = new AuthenticatorSelection
                {
                    ResidentKey = ResidentKeyRequirement.Required,
                    UserVerification = UserVerificationRequirement.Preferred,
                },
                AttestationPreference = AttestationConveyancePreference.None,
            });
            var ceremonyId = Guid.NewGuid().ToString("N");
            cache.Set($"reg:{ceremonyId}", (user.Id, options.ToJson()), TimeSpan.FromMinutes(5));
            return Results.Ok(new CeremonyStart(ceremonyId, options));
        }).RequireAuthorization();

        g.MapPost("/passkeys/register/finish", async (PasskeyRegisterFinish req, HttpContext ctx, AppDb db, IFido2 fido2, IMemoryCache cache, CancellationToken ct) =>
        {
            if (!cache.TryGetValue<(Guid UserId, string Json)>($"reg:{req.CeremonyId}", out var pending) || pending.UserId != ctx.User.UserId())
                return Results.BadRequest(new { error = "Sessione scaduta, riprova." });
            cache.Remove($"reg:{req.CeremonyId}");

            var cred = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = req.Response,
                OriginalOptions = CredentialCreateOptions.FromJson(pending.Json),
                IsCredentialIdUniqueToUserCallback = async (args, token) =>
                    !await db.Passkeys.AnyAsync(p => p.CredentialId == args.CredentialId, token),
            }, ct);

            db.Passkeys.Add(new PasskeyCredential
            {
                UserId = pending.UserId,
                CredentialId = cred.Id,
                PublicKey = cred.PublicKey,
                SignCount = cred.SignCount,
                Name = string.IsNullOrWhiteSpace(req.Name) ? "Passkey" : req.Name,
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        }).RequireAuthorization();

        g.MapGet("/passkeys", async (HttpContext ctx, AppDb db) =>
        {
            var id = ctx.User.UserId();
            return await db.Passkeys.Where(p => p.UserId == id)
                .Select(p => new { p.Id, p.Name, p.CreatedAt, p.LastUsedAt }).ToListAsync();
        }).RequireAuthorization();

        g.MapDelete("/passkeys/{id:int}", async (int id, HttpContext ctx, AppDb db) =>
        {
            var uid = ctx.User.UserId();
            await db.Passkeys.Where(p => p.Id == id && p.UserId == uid).ExecuteDeleteAsync();
            return Results.NoContent();
        }).RequireAuthorization();

        // ---- Passkey login (discoverable credentials: username optional) ----
        g.MapPost("/passkeys/login/begin", async (PasskeyLoginStart req, AppDb db, IFido2 fido2, IMemoryCache cache) =>
        {
            var allowed = new List<PublicKeyCredentialDescriptor>();
            if (!string.IsNullOrWhiteSpace(req.Username))
            {
                allowed = await db.Passkeys.Where(p => p.User!.Username == req.Username)
                    .Select(p => new PublicKeyCredentialDescriptor(p.CredentialId)).ToListAsync();
            }
            var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
            {
                AllowedCredentials = allowed,
                UserVerification = UserVerificationRequirement.Preferred,
            });
            var ceremonyId = Guid.NewGuid().ToString("N");
            cache.Set($"login:{ceremonyId}", options.ToJson(), TimeSpan.FromMinutes(5));
            return Results.Ok(new CeremonyStart(ceremonyId, options));
        }).RequireRateLimiting("auth");

        g.MapPost("/passkeys/login/finish", async (PasskeyLoginFinish req, AppDb db, IFido2 fido2, IMemoryCache cache, TokenService tokens, CancellationToken ct) =>
        {
            if (!cache.TryGetValue<string>($"login:{req.CeremonyId}", out var json) || json is null)
                return Results.BadRequest(new { error = "Sessione scaduta, riprova." });
            cache.Remove($"login:{req.CeremonyId}");

            var rawId = req.Response.RawId;
            var stored = await db.Passkeys.Include(p => p.User).ThenInclude(u => u!.Passkeys)
                .FirstOrDefaultAsync(p => p.CredentialId == rawId, ct);
            if (stored is null) return Results.Unauthorized();

            var result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = req.Response,
                OriginalOptions = AssertionOptions.FromJson(json),
                StoredPublicKey = stored.PublicKey,
                StoredSignatureCounter = stored.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(Encoding.UTF8.GetString(args.UserHandle) == stored.UserId.ToString("N")),
            }, ct);

            stored.SignCount = result.SignCount;
            stored.LastUsedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new AuthResponse(tokens.CreateAccessToken(stored.User!), stored.User!.ToDto()));
        }).RequireRateLimiting("auth");
    }
}
