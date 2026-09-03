using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MarketEye.Application.Screening;
using MarketEye.Domain.Entities;
using MarketEye.Domain.Screening;
using MarketEye.Infrastructure.Persistence;

namespace MarketEye.Infrastructure.Screening;

/// <summary>
/// Create, update and delete for saved strategies (PLAN.md §10: "core workflow, not polish").
///
/// Every write validates the criteria first, mirroring StrategyConceptStore: a saved strategy
/// must already be something ScreeningEngine can run, never something that fails the first time
/// someone clicks "Run".
/// </summary>
public sealed class SavedStrategyStore(
    MarketEyeDbContext db,
    ScreenCriteriaValidator validator)
{
    public const int MaxNameLength = 128;
    public const int MaxDescriptionLength = 512;

    public sealed record WriteResult(
        SavedStrategy? Strategy, CriteriaValidationResult Validation, string? Conflict = null)
    {
        public bool Succeeded => Strategy is not null;
    }

    public async Task<WriteResult> CreateAsync(SavedStrategyDraft draft, CancellationToken ct)
    {
        var name = draft.Name.Trim();
        var validation = Validate(draft, name);
        if (!validation.IsValid) return new(null, validation);

        if (await db.SavedStrategies.AnyAsync(s => s.Name == name && s.OwnerUserId == null, ct))
            return new(null, CriteriaValidationResult.Ok(), "name-in-use");

        var now = DateTimeOffset.UtcNow;
        var entity = new SavedStrategy
        {
            Name = name,
            Description = draft.Description,
            OriginalPrompt = draft.OriginalPrompt,
            CriteriaJson = ScreenCriteriaJson.Serialize(draft.Criteria),
            OwnerUserId = null,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.SavedStrategies.Add(entity);
        await db.SaveChangesAsync(ct);
        return new(entity, validation);
    }

    public async Task<WriteResult> UpdateAsync(
        string name, SavedStrategyDraft draft, CancellationToken ct)
    {
        var entity = await db.SavedStrategies.FirstOrDefaultAsync(
            s => s.Name == name && s.OwnerUserId == null, ct);
        if (entity is null) return new(null, CriteriaValidationResult.Ok(), "not-found");

        var newName = draft.Name.Trim();
        var validation = Validate(draft, newName);
        if (!validation.IsValid) return new(null, validation);

        if (newName != entity.Name &&
            await db.SavedStrategies.AnyAsync(s => s.Name == newName && s.OwnerUserId == null, ct))
        {
            return new(null, CriteriaValidationResult.Ok(), "name-in-use");
        }

        entity.Name = newName;
        entity.Description = draft.Description;
        entity.OriginalPrompt = draft.OriginalPrompt;
        entity.CriteriaJson = ScreenCriteriaJson.Serialize(draft.Criteria);
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return new(entity, validation);
    }

    /// <summary>
    /// Turns on read-only sharing (PLAN.md §10 Phase 4 "Strategy sharing"), returning the token.
    /// Idempotent: an already-shared strategy keeps its existing token rather than rotating it,
    /// since rotating would silently break every link already handed out.
    /// </summary>
    public async Task<string?> EnableSharingAsync(string name, CancellationToken ct)
    {
        var entity = await db.SavedStrategies.FirstOrDefaultAsync(
            s => s.Name == name && s.OwnerUserId == null, ct);
        if (entity is null) return null;

        if (entity.ShareToken is null)
        {
            entity.ShareToken = GenerateToken();
            entity.SharedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return entity.ShareToken;
    }

    /// <summary>Revokes sharing. Any link already handed out stops resolving immediately.</summary>
    public async Task<bool> DisableSharingAsync(string name, CancellationToken ct)
    {
        var entity = await db.SavedStrategies.FirstOrDefaultAsync(
            s => s.Name == name && s.OwnerUserId == null, ct);
        if (entity is null) return false;

        entity.ShareToken = null;
        entity.SharedAt = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// 32 bytes of CSPRNG output, base64url-encoded with padding trimmed -- unguessable, and the
    /// entire trust model for the public /api/shared/{token} route (no other check gates it).
    /// </summary>
    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public async Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        var entity = await db.SavedStrategies.FirstOrDefaultAsync(
            s => s.Name == name && s.OwnerUserId == null, ct);
        if (entity is null) return false;

        db.SavedStrategies.Remove(entity);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private CriteriaValidationResult Validate(SavedStrategyDraft draft, string trimmedName)
    {
        var errors = new List<CriteriaValidationError>();

        if (trimmedName.Length == 0)
        {
            errors.Add(new(
                "name", CriteriaErrorCode.InvalidConceptName,
                "A saved strategy needs a name."));
        }
        else if (trimmedName.Length > MaxNameLength)
        {
            errors.Add(new(
                "name", CriteriaErrorCode.InvalidConceptName,
                $"Name is longer than {MaxNameLength} characters."));
        }

        if (draft.Description is { Length: > MaxDescriptionLength })
        {
            errors.Add(new(
                "description", CriteriaErrorCode.InvalidConceptName,
                $"Description is longer than {MaxDescriptionLength} characters."));
        }

        // Validated the same way ScreeningEngine will see it, so a saved strategy that fails here
        // can never reach the point of failing when someone clicks "Run" instead.
        errors.AddRange(validator.Validate(draft.Criteria).Errors
            .Select(e => e with { Path = $"criteria.{e.Path}" }));

        return errors.Count == 0
            ? CriteriaValidationResult.Ok()
            : CriteriaValidationResult.Failed(errors);
    }
}
