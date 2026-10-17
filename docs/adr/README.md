# Architecture Decision Records

Short, dated records of architecturally significant decisions — the *why* behind a
choice, the options weighed, and the consequences accepted. One file per decision,
numbered, immutable once `Accepted` (supersede with a new ADR rather than editing).

Format: Status · Context · Decision · Consequences. Keep them terse.

| ADR | Title | Status |
|-----|-------|--------|
| [0005](0005-build-verifier-sandbox.md) | `build_verifier` execution sandbox | Accepted |
| [0011](0011-ui-first-configuration.md) | UI-first configuration: settings registry, scopes, precedence | Accepted |

> ADRs 0001–0004 (modular monolith, LLM gateway, multi-tenant isolation,
> one-engine-three-surfaces) are scheduled as a back-fill in [ROADMAP.md](../../ROADMAP.md) Q2;
> the decisions themselves are already described in [README.md](../../README.md) and
> [docs/architecture.md](../architecture.md). 0005 is written first because it gates Q1.
