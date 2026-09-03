# ADR-0013: Tree-shaped DSL, flat implementation in v1

**Status:** Accepted · **Date:** 2026-09-03 · **Phase:** 1 (retroactively documented in Phase 4)

## Context

`PLAN.md` §6 states the rule: "Model the tree from day one; implement only `AND` in v1. The type
is a tree, the JSON schema is a tree, and the validator walks a tree — but the v1 compiler and UI
handle a single flat `AND` group." This was implemented in Phase 1 —
`FilterNode`/`Group`/`Comparison` (`src/MarketEye.Domain/Screening/FilterNode.cs`) and
`CriteriaCompiler` (`src/MarketEye.Application/Screening/CriteriaCompiler.cs`) have existed since
then — and CLAUDE.md restates it as a project invariant almost verbatim. §11 lists this as one of
six ADRs the plan calls for; this closes that gap for a decision already shipped, and argues the
cost/benefit explicitly rather than leaving it implicit in the type shapes.

## Decision

### The type is already a tree, everywhere, today

`FilterNode` is an abstract record with two cases: `Group` (an `Op` plus a list of `Children`,
themselves `FilterNode`s) and `Comparison` (a leaf: field, operator, decimal value). `Group.Op` is
a three-value `GroupOperator` enum — `And`, `Or`, `Not` — all three represented in the type today,
not just `And`. `FilterNode.Depth()` and `.Comparisons()` are recursive tree-walks implemented on
the abstract base, so anything holding a `FilterNode` already handles arbitrary nesting: a `Group`
containing `Group`s containing `Comparison`s serialises, deserialises
(`[JsonPolymorphic]`/`[JsonDerivedType]` discriminated union), and walks correctly right now.

### The validator already walks the whole tree; only the compiler is restricted

The validator enforces field whitelisting, per-field operator whitelisting, per-field ranges, a max
tree depth, and a max comparison count — all genuine tree-shaped constraints, checked recursively
regardless of how deep or how mixed the `Op` values are. `CriteriaCompiler` is the one place that
narrows: it throws if it encounters a `Group` whose `Op` is not `GroupOperator.And` ("`Group
operator '{g.Op}' does not compile in v1 (§6). Only AND is supported.`"). A criteria tree using
`Or` or `Not` is therefore valid at the type and JSON-schema level, passes the validator's
structural checks, and is rejected only at the final compilation step, with a message naming
exactly why.

### What this buys: additive cost later, instead of a rewrite

The alternative — a `ScreenCriteria` typed as a flat list of comparisons, matching what v1 actually
needs — would be simpler today and cost nothing in Phase 1. But every one of the four layers that
touch it (the C# type, the JSON schema the AI model is constrained to emit, the validator, and the
UI's criteria rendering) would need a breaking rewrite the day `OR`/`NOT` is actually wanted, and
every already-saved `SavedStrategy.CriteriaJson` blob would need a migration to the new shape. Model
the tree now, and adding `OR`/`NOT` later is: relax `CriteriaCompiler`'s one `Op` check into real
SQL generation for `OR`/`NOT` groups, extend the nested-group UI rendering, and widen the model's
JSON schema and eval cases to actually emit them — no type change, no JSON shape change, no
migration of existing saved strategies.

### The cost is paid once, deliberately deferred rather than avoided

`PLAN.md` §6 is explicit that this is not free: "full boolean support means recursive validation,
recursive SQL generation, nested-group UI, and a harder parsing target for the model." That work is
real and is deliberately deferred to Phase 3+, "once the foundation is proven." As of Phase 3's
exit notes, it remains explicitly out of scope, decided with the user rather than silently dropped —
nothing in §7 or §8's backtest semantics needs boolean combinators, and flat-`AND` plus
`MaxPositions`-by-sort already covers every strategy the eval suite and the synthetic-market tests
exercise.

## Consequences

- Adding `OR`/`NOT` remains a compiler and UI change plus a model/eval-schema widening, never a data
  migration: every `FilterNode` ever serialised into a `SavedStrategy.CriteriaJson` or
  `BacktestRun.DefinitionJson` blob already round-trips through the full tree-shaped JSON contract,
  whether or not the compiler currently accepts what it describes.
- The validator's depth and comparison-count limits (max tree depth 4, max 20 comparisons) are
  already tree-aware and require no change when `OR`/`NOT` ships — they were sized for the tree
  PLAN.md's §6 describes, not for the flat case the compiler currently handles alone.
- A criteria tree naming `Or` or `Not` today fails at `CriteriaCompiler.Compile` with a message that
  names the offending operator and cites §6 — an explicit "not yet," not a silent no-op or an
  incorrect SQL translation.
