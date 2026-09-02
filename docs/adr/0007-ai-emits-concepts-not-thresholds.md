# ADR-0007: AI emits concepts, never thresholds or SQL

**Status:** Accepted · **Date:** 2026-09-02 · **Phase:** 2

## Context

`PLAN.md` §5.1 states the rule this project is built around:

> **The AI may infer *concepts*. It may not invent *financial thresholds*.**

§11 lists this as ADR #4, "the core correctness and security argument," and left it to be
written once Phase 2 built the thing it argues for. Four decisions made while implementing that
phase are the substance of the argument, and each is easy to get wrong in a way that looks
correct until someone edits the vocabulary or the model changes its mind about a number:

1. Splitting the vocabulary into two tables instead of the one §5.2 originally specified.
2. Deriving the model's output schema from that vocabulary at request time, not hand-writing it.
3. Making concept resolution fail closed at every step, never fall back to a nearest match.
4. Making a user's own number *replace* a concept's default rather than combine with it.

## Decision

### 1. Two tables, not one

§5.2 specifies a single `MetricConcepts` table carrying `Name`, `Aliases[]`, `DefinitionJson`,
`IsEnabled`, `IsSystem`, `OwnerUserId` — a user-editable Strategy Vocabulary. Phase 1 had already
shipped a table of that name doing something else: `Name → ColumnName + Source +
AllowedOperators + Min/Max`, which `CriteriaCompiler` trusts to build SQL column references
(`src/MarketEye.Application/Screening/CriteriaCompiler.cs`).

Those are two different trust boundaries wearing one name. `MetricConceptEntity` is system-owned
and its `ColumnName` becomes a SQL identifier — nothing about the DSL works if that value is not
exactly a real column. `StrategyConceptEntity` is what §5.2 asks a user to edit in a text box, and
carries no column name at all — only a `FilterNode` fragment naming *metrics*, which the compiler
resolves through the sealed table above.

Collapsing them into one table, as originally specified, would make the row a user edits the same
row that feeds SQL generation. The split is what lets `/api/vocabulary/strategy-concepts` accept
arbitrary user edits with only domain validation (`ScreenCriteriaValidator`, run before every
write) standing between a browser and the compiler, while `MetricConcepts` stays read-only and
seeded.

```
MetricConcepts (system, sealed)          StrategyConcepts (user-editable)
  PeRatio → f.[Pe]                         cheap      → AND(PeRatio < 25, PbRatio < 3)
  Rsi14   → i.[Rsi14]                      profitable → AND(ReturnOnEquity > 0)
  ▲ CriteriaCompiler resolves columns here  ▲ IntentJsonSchema enumerates only these names
```

This is a deliberate deviation from §5.2's literal schema, not an oversight — `PLAN.md` §14 and
§10 point here.

### 2. The output schema is derived from the vocabulary, never hand-written

`IntentJsonSchema.Build` (`src/MarketEye.Ai/IntentJsonSchema.cs`) generates the model's JSON
Schema from the live vocabulary on every request: `concepts` items are typed as an `enum` of
currently-enabled strategy concept names, and `explicit_filters[].field` as an `enum` of metric
names. Under schema-constrained decoding (OpenAI strict mode, or grammar-based decoding for a
local model) the model **cannot emit** a concept that is not in the vocabulary — not "is
unlikely to," cannot: it is not a token sequence the decoder will produce.

Two consequences follow from generating the schema rather than writing it once:

- Disabling a concept on the Vocabulary screen changes what the model is even able to say on the
  very next request, with no prompt or schema file to keep in sync by hand.
- The schema is a conservative subset — objects, arrays, `enum`, string/number, every property
  required, `additionalProperties: false` — chosen as the intersection of what OpenAI strict mode
  and grammar-guided decoding both accept, so the same schema builder works unchanged across
  providers (`IIntentParser` has more than one implementation for exactly this reason).

This is real, but it is a property of one provider's decoder, not of the system — which is why
resolution below does not trust it.

### 3. Resolution fails closed, never falls back to a nearest match

`IntentResolver.Resolve` (`src/MarketEye.Domain/Screening/IntentResolver.cs`) is the load-bearing
half of the feature and contains no AI. Every step that can reject, does, rather than substitutes:

- A concept name the vocabulary does not recognise (`IStrategyConceptVocabulary.Find` returns
  `null`) is a hard `UnknownStrategyConcept` error. `Find` returns `null` for a *disabled* concept
  too — deliberately, because "we turned that off" and "that concept never existed" must produce
  the same answer, or disabling a concept from the Vocabulary screen would silently keep serving
  its old meaning to anyone who already knew its name.
- An explicit filter naming anything other than a real metric is rejected — a strategy concept is
  a set of comparisons, not a column, so `"cheap < 12"` is meaningless and is refused rather than
  guessed at.
- An empty intent (no concepts, no explicit filters) is refused with `EmptyIntent` rather than
  becoming a screen over the entire universe, which is almost never what was meant.
- The resolved tree is re-validated by `ScreenCriteriaValidator` unconditionally before it is
  returned, even though resolution built it from vocabulary rows that should already be valid.
  "Should already be valid" is exactly the assumption that turns a boundary into a hole.

The schema in §2 makes an invalid concept structurally hard to emit; the resolver is what makes
it impossible to *act on*, independent of which provider produced the output or whether it
honoured the schema at all. `StubIntentParser` — a deterministic keyword parser with no schema
enforcement whatsoever, used when no API key is configured — is resolved through this exact same
path, which is the test that the boundary is real rather than assumed.

### 4. A user's own number replaces the concept's default, never adds to it

When a prompt supplies both a concept and a number for the same metric — `"cheap with P/E below
12"` — the resolver drops that metric's comparison out of the concept's definition and keeps only
the user's number (`IntentResolver.SurvivingComparisons`). The alternative, keeping both, is wrong
in a way that is easy to miss: `"cheap with P/E below 40"` would silently keep the vocabulary's
`P/E < 25` *and* add `P/E < 40`, and since the first is stricter the second has no effect —
the user's stated number would be accepted and then ignored.

Replacing is the rule that behaves the same whether the user's number tightens or loosens the
default. `IntentResolution` records which metrics were overridden and by which concept, and the
interpretation panel renders it as an explicit note — the substitution is a fact the user is
told, never something that happens invisibly inside a screen they only see the results of.

This only reaches the top level of a flat `AND` — correct for now because v1 concept definitions
are exactly that (`StrategyConceptValidator` rejects anything else), enforced by a unit test. When
`OR`/`NOT` arrive in Phase 3+ this needs revisiting: dropping a comparison out of an `OR` changes
what the expression means, not just what it evaluates to.

## Consequences

- A concept the model names that is not in the vocabulary is unemittable by a schema-respecting
  provider, and a hard validation failure by any provider that ignores the schema, including
  `StubIntentParser`. There is exactly one code path from "the model said a word" to "a screen
  ran," and it runs the same validation regardless of what said the word.
- Editing or disabling a concept from the Vocabulary screen takes effect on the very next parse,
  for every user, with nothing to restart or redeploy — the schema, the resolver's lookup, and
  `ParseCache`'s vocabulary-version key all read the same table.
- The two-table split means a metric's column name is never one HTTP `PUT` away from a browser.
  The furthest a user-supplied value reaches is a `FilterNode` naming a metric by its already-
  whitelisted name; `CriteriaCompiler` still owns turning that name into SQL.
- §5.2's literal schema (one table, `MetricConcepts`, carrying both roles) is not what got built.
  `PLAN.md` §14 records this deviation with a pointer back here.
- The override rule is a top-level-only mechanism tied to v1's flat-`AND` restriction. It is
  correct today and named in `PLAN.md` §14 "Still open" as something Phase 3's `OR`/`NOT` work
  must revisit, not something this ADR settles permanently.
