using AnimeRoll.Server.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AnimeRoll.Server.Api.Auth;

public sealed record ProfileDto(Guid Id, string Name, int Avatar, bool HasPasscode, bool IsCurrent, bool IsOwner);
public sealed record CreateProfileRequest(string Name, int Avatar, string? Passcode);
public sealed record UpdateProfileRequest(string? Name, int? Avatar);
public sealed record PasscodeRequest(string? Current, string? Passcode);
public sealed record EnterProfileRequest(string? Passcode);

/// Netflix-style profiles on one server. Each profile is a user of its own: its own watch progress and
/// its own AniList / MyAnimeList list. The library and the downloads are shared by everyone.
/// To use the server a device signs in once (password or passkey of any profile); after that a profile
/// is entered with "enter", which asks for the passcode when the profile has one. Profiles made from the
/// app have no password: they are reached through "enter" (passcode) or a passkey.
public static class ProfileEndpoints
{
    private static readonly PasswordHasher<User> Hasher = new();
    private const int MinPasscode = 4, MaxPasscode = 12;

    public static void MapProfiles(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/profiles").WithTags("Profiles").RequireAuthorization();

        async Task<List<ProfileDto>> List(AppDb db, Guid me)
        {
            var users = await db.Users.OrderBy(u => u.CreatedAt).ToListAsync();
            var owner = users.FirstOrDefault()?.Id;
            return users.Select(u => new ProfileDto(u.Id, u.Username, u.Avatar, u.PasscodeHash != null, u.Id == me, u.Id == owner)).ToList();
        }

        g.MapGet("", async (HttpContext ctx, AppDb db) => await List(db, ctx.User.UserId()));

        g.MapPost("", async (CreateProfileRequest req, HttpContext ctx, AppDb db) =>
        {
            var name = req.Name.Trim();
            if (name.Length is < 2 or > 24) return Results.BadRequest(new { error = "Il nome deve avere da 2 a 24 caratteri." });
            if (req.Passcode != null && !ValidPasscode(req.Passcode)) return Results.BadRequest(new { error = PasscodeRule });
            if (await db.Users.AnyAsync(u => u.Username == name)) return Results.Conflict(new { error = "Esiste già un profilo con questo nome." });
            var user = new User { Username = name, PasswordHash = "", Avatar = Math.Max(0, req.Avatar) };
            if (req.Passcode != null) user.PasscodeHash = Hasher.HashPassword(user, req.Passcode);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return Results.Ok((await List(db, ctx.User.UserId())).First(p => p.Id == user.Id));
        });

        g.MapPatch("/{id:guid}", async (Guid id, UpdateProfileRequest req, HttpContext ctx, AppDb db) =>
        {
            if (id != ctx.User.UserId()) return Results.Forbid(); // only your own profile: enter it first
            var user = await db.Users.FirstAsync(u => u.Id == id);
            if (req.Name != null)
            {
                var name = req.Name.Trim();
                if (name.Length is < 2 or > 24) return Results.BadRequest(new { error = "Il nome deve avere da 2 a 24 caratteri." });
                if (await db.Users.AnyAsync(u => u.Username == name && u.Id != id)) return Results.Conflict(new { error = "Esiste già un profilo con questo nome." });
                user.Username = name;
            }
            if (req.Avatar != null) user.Avatar = Math.Max(0, req.Avatar.Value);
            await db.SaveChangesAsync();
            return Results.Ok((await List(db, id)).First(p => p.Id == id));
        });

        // Set, change or remove (Passcode null) the passcode of the signed-in profile.
        g.MapPut("/me/passcode", async (PasscodeRequest req, HttpContext ctx, AppDb db) =>
        {
            var user = await db.Users.FirstAsync(u => u.Id == ctx.User.UserId());
            if (user.PasscodeHash != null && !Check(user, req.Current))
            {
                await Task.Delay(Random.Shared.Next(300, 700));
                return Results.Unauthorized();
            }
            if (req.Passcode != null && !ValidPasscode(req.Passcode)) return Results.BadRequest(new { error = PasscodeRule });
            user.PasscodeHash = req.Passcode == null ? null : Hasher.HashPassword(user, req.Passcode);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireRateLimiting("auth");

        // Switch to another profile: a new token for it, after the passcode when it has one.
        g.MapPost("/{id:guid}/enter", async (Guid id, EnterProfileRequest req, HttpContext ctx, AppDb db, TokenService tokens) =>
        {
            var user = await db.Users.Include(u => u.Passkeys).FirstOrDefaultAsync(u => u.Id == id);
            if (user is null) return Results.NotFound();
            if (id != ctx.User.UserId() && user.PasscodeHash != null && !Check(user, req.Passcode))
            {
                await Task.Delay(Random.Shared.Next(300, 700));
                return Results.Json(new { error = "Passcode errato." }, statusCode: 403);
            }
            return Results.Ok(new AuthResponse(tokens.CreateAccessToken(user), user.ToDto()));
        }).RequireRateLimiting("auth");

        // Delete a profile with its progress and passkeys (what it downloaded stays in the library).
        // Allowed on yourself, or on anyone for the owner (the oldest profile); a locked profile asks for its passcode.
        g.MapDelete("/{id:guid}", async (Guid id, string? passcode, HttpContext ctx, AppDb db) =>
        {
            var me = ctx.User.UserId();
            var users = await db.Users.OrderBy(u => u.CreatedAt).ToListAsync();
            var target = users.FirstOrDefault(u => u.Id == id);
            if (target is null) return Results.NotFound();
            if (users.Count == 1) return Results.Conflict(new { error = "Non puoi eliminare l'ultimo profilo." });
            if (id != me && users[0].Id != me) return Results.Forbid();
            if (id != me && target.PasscodeHash != null && !Check(target, passcode))
                return Results.Json(new { error = "Passcode errato." }, statusCode: 403);
            await db.WatchProgress.Where(w => w.UserId == id).ExecuteDeleteAsync();
            await db.Passkeys.Where(p => p.UserId == id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync();
            return Results.NoContent();
        });
    }

    private const string PasscodeRule = "Il passcode deve avere da 4 a 12 caratteri.";
    private static bool ValidPasscode(string p) => p.Length is >= MinPasscode and <= MaxPasscode;
    private static bool Check(User u, string? passcode) =>
        u.PasscodeHash != null && passcode != null && Hasher.VerifyHashedPassword(u, u.PasscodeHash, passcode) != PasswordVerificationResult.Failed;
}
