# 0002. Prioritise requirements with MoSCoW to fit the deadline

- **Status:** APPROVED by Joey Schnepel, 2026-11-17

## Context

The platform has three core requirements ([requirements](../requirements.md) §1) and a fixed target release of 2026-11-23. The initial requirements list had 59 items, 57 of them marked Must, most of them engineering choices rather than core requirements. With nearly everything a Must, there was nothing to cut if time ran short.

## Decision

Sort the requirements into Must, Should, Could and Won't, as listed in [requirements](../requirements.md) §4.

- **Must:** the core requirements (a working full-stack platform, several consistent interfaces and accounts, realistic simulated activity) plus the correctness work that makes "consistent" provable: integer-cent money, database-time deadlines under a row lock, an atomic bid transaction with an outbox event, idempotency keys with replay, version-gated client refresh, and a simulator with an independent verifier.
- **Should:** a second API instance, request-recovery endpoints, OpenTelemetry, live hosting.
- **Could:** mTLS between services, outbox quarantine and replay, crash-durability tests, further simulator hardening.
- **Won't:** payments, shipping, backups, Kubernetes, soft close, bid retraction.

Requirement IDs are stable; moving an item between categories never renumbers it. The requirements document is the current list.

## Alternatives considered

- **Keep every Must:** rejected. It can't be built and verified honestly by the deadline.
- **Cut correctness work to gain features:** rejected. Consistency under concurrent load is the core of the project.

## Consequences

- If the deadline is at risk, work is cut in the order given in [docs/build-plan.md](../build-plan.md), never from the correctness core.
- Deferred items are listed in the README under "With more time".
