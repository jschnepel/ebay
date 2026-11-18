# 0004. Keep process records outside the repository

- **Status:** APPROVED by Joey Schnepel, 2026-11-18

## Context

The engineering standards (v1.0.0) ask every project to commit a full process trail alongside the code: a status log, a time log, a changelog, an AI-usage log, a demo guide, slice completion records and every requirements-audit report. In this project that meant 19 root files and more than 40 Markdown files before any product code existed. Most of it recorded how the work was managed rather than what the system is, and it buried the documents a reader actually needs.

## Decision

- **The repository holds system documentation only:** the README, requirements (with Unknowns, assumptions, acceptance criteria and traceability), architecture with its engineering rules and design decisions, the build plan, user stories, auction-law notes and these decision records.
- **Process records are kept outside the repository,** in the owner's planning workspace: status and time logs, slice completion records, requirements-audit reports, AI-usage notes and process decisions (such as the gate for starting to build before the spec audit passes). They are still written; they just aren't shipped with the code.
- **The audits themselves still run.** Each milestone gets a feature audit, and the release gets a full requirements audit, as in the [build plan](../build-plan.md).
- **Root files are limited to what the tooling needs there:** `README.md`, the solution file, `global.json`, `Directory.Build.props`, `Directory.Packages.props` and the dotfiles. Analyzer thresholds live in `eng/`, the environment template in `deploy/`.
- **The standards copy stays local and untracked.** Its version (v1.0.0) and the rules this project applies are stated in [ADR-0001](0001-adopt-portfolio-standards.md).
- **There is no time log.** Scope is managed against the target date with the cut order in the build plan ([ADR-0002](0002-scope-cut-for-deadline.md)).

## Alternatives considered

- **Commit the full process trail, as the standards ask:** it's auditable from a clone, but most of it is noise for anyone reading the system.
- **Drop the process records entirely:** this loses the evidence behind each step and the honesty checks it supports.

## Consequences

- A clone shows the system, the plan and the decisions, but not the audit history. Audit verdicts are summarised in the README's Verification section.
- This deviates from the standards' documentation set (v1.0.0 `documentation.md` §2) and from the Tier 2 Definition of Done items that name STATUS, TIMELOG and AI-USAGE. The upstream standards should be updated to match, so the deviation goes away.
