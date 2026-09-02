namespace MarketEye.Domain.Screening.Vocabulary;

/// <summary>
/// Read access to the Strategy Vocabulary (§5.2). Backed by the StrategyConcepts table in
/// production and by a fixed set in tests.
/// </summary>
public interface IStrategyConceptVocabulary
{
    /// <summary>
    /// Resolves a name or alias. Input is normalised via <see cref="ConceptName"/> first, so
    /// "Small Cap" and "small_cap" reach the same row.
    ///
    /// Returns null when the concept is unknown OR disabled. Callers must fail, not substitute
    /// (§5.1) — a disabled concept is deliberately indistinguishable from an absent one, because
    /// "we turned that off" and "that never existed" have the same correct answer: refuse.
    /// </summary>
    StrategyConcept? Find(string nameOrAlias);

    /// <summary>Enabled concepts only — this is what the model's schema and prompt are built from.</summary>
    IReadOnlyCollection<StrategyConcept> Enabled { get; }

    /// <summary>Every concept including disabled ones, for the vocabulary management screen.</summary>
    IReadOnlyCollection<StrategyConcept> All { get; }

    /// <summary>
    /// Changes whenever any definition changes. Used as part of the ParseCache key (§5.5) so that
    /// editing what "cheap" means invalidates every cached parse by construction — the same
    /// free-invalidation property SnapshotId gives the result cache.
    /// </summary>
    string VersionToken { get; }
}
