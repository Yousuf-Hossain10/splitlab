# Splitlab dashboard

Angular 22 (standalone components, signals, zoneless) for managing projects, environments, experiments and SDK keys, and for reading results. Design choices are in [ADR-0008](../docs/adr/0008-angular-standalone-signals-generated-client.md).

## Running it

```bash
# 1. the API (needs PostgreSQL: `docker compose up -d` from the repository root)
dotnet run --project ../src/Splitlab.Api --urls http://localhost:5080

# 2. the dashboard, with /v1 and /dev proxied to the API (see proxy.conf.json)
npm ci
npm start
```

Open <http://localhost:4200> and sign in with any name (the API's Development issuer creates a token for it). Whoever creates a project becomes its admin.

## The generated API client

The TypeScript types in `src/app/api/schema.d.ts` are **generated** from `openapi/splitlab.v1.json`, which is itself generated from the running API. Do not edit either by hand.

When the API changes:

```bash
# from the repository root: refresh the OpenAPI document
UPDATE_OPENAPI=1 dotnet test tests/Splitlab.Api.Tests --filter OpenApiContractTests

# from dashboard/: regenerate the client
npm run generate:api
```

CI fails if the document does not match the API, or if the client does not match the document, so a contract change always shows up in review, and a mismatch shows up as a compile error here rather than a runtime failure.

## Layout

```
src/app/
  api/        generated types, plus friendly aliases (models.ts)
  core/       typed API client, auth store, route guard, error handling
  shared/     shell, small UI components, key helpers
  features/   one folder per area, each lazy-loaded
    projects/  experiments/  results/  settings/  sign-in/
```

## Tests

```bash
npm test         # watch mode
npm run test:ci  # once, as CI does
npm run build
```

Unit and component tests run on Vitest with jsdom. End-to-end tests (Playwright) against a running API are not set up yet.

## Not done yet

- Sign-in through a real identity provider (OpenID Connect with PKCE). For now the dashboard takes a token from the Development issuer or a pasted one, and keeps it in `sessionStorage`.
- Feature flags, project members and an audit log view.
