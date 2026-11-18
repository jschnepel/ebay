# 0003. Same-origin CSRF protection without antiforgery tokens

- **Status:** APPROVED by Joey Schnepel, 2026-11-17

## Context

The project's web and .NET rule packs require antiforgery tokens on unsafe requests. This application serves the Blazor WebAssembly client and its JSON API from one origin, authenticated by a cookie. For that shape, Microsoft's guidance is that a `SameSite=Strict` cookie plus a JSON-only API already blocks cross-site requests: a browser won't attach the cookie to a cross-site request, and a cross-site form can't send `application/json` without a CORS preflight, which the API never approves.

Tokens would add a token endpoint, a round trip on every page load, and token handling in the load simulator, without closing any remaining attack path.

## Decision

- The session cookie is `HttpOnly`, `Secure` behind HTTPS and `SameSite=Strict`, scoped to the app's path base.
- Every unsafe endpoint accepts only `application/json`. Image uploads travel as base64 inside JSON, so there is no multipart exception.
- The API checks the `Origin` header on unsafe requests and rejects a foreign origin (403) or a non-JSON body (415).
- No antiforgery tokens. This is a recorded deviation from the packs.

## Alternatives considered

- **Antiforgery tokens:** conform to the packs, but add moving parts with no security gain for a same-origin JSON API.

## Consequences

- SEC-01's acceptance tests prove the model: a foreign `Origin` gets 403, a form post gets 415, and a same-origin JSON request succeeds.
- With `SameSite=Strict`, links into the app from other sites arrive without the cookie, so the user is asked to sign in. That's acceptable here.
- A future deployment that splits the API onto a separate origin would need tokens or another CSRF defence.
