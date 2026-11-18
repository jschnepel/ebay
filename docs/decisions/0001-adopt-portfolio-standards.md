# 0001. Intent-based engineering standards instead of literal Power of 10

- **Status:** APPROVED by Joey Schnepel, 2026-11-17

## Context

NASA/JPL's Power of 10 rules were written for safety-critical C. Several of them (preprocessor limits, one level of pointer dereferencing, no function pointers) have no meaning in C#, and others would fight the language's own idioms: lambdas, delegates, LINQ and async. Applying them literally to a .NET web application would add cost without adding safety.

The intent behind the rules still applies: bounded resources, loud failure, simple control flow and small units.

## Decision

- The project follows a set of intent-based engineering standards (v1.0.0, Tier 2) with .NET and web rule packs. They keep the intent of each Power of 10 rule and translate it for .NET.
- The intents are enforced by tooling where possible: warnings as errors, Sonar and CA analyzers with parameter, nesting and complexity limits, and locked package restore. Guard clauses at public entry points and method and file length are code-review rules, not tool-enforced.
- Power of 10 is credited as an influence, never claimed as compliance.

## Alternatives considered

- **Literal Power of 10:** rejected. It conflicts with idiomatic C# and costs time without a matching benefit.
- **Only .NET defaults:** rejected. It loses the fail-loud and bounded-resource discipline.

## Consequences

Power of 10 is never claimed as compliance anywhere in the project.
