# gateway

The system's public API surface. Everything under `/api/*` reaches a backend through here, for
every client — the web frontend today, a mobile app later.

`src/Gateway/appsettings.json` holds **the** route table. It used to exist in four places
(`infra/nginx.conf.template`, a dead `infra/nginx.conf`, `e2e/local-prod/nginx.conf`, and the dev
rewrites in `frontend/next.config.mjs`), which is how it drifted: the e2e copy never grew the SSE
handling the production one had.

## What this is not

It does not authorise. Deciding here which paths tolerate an anonymous caller would mean holding a
copy of every service's endpoint list — the same duplication this project exists to remove. Each
service validates the token itself against identity's key set, and each owns the permissions only
it can answer: forum reads `CommunityMembership.Role`, household reads its memberships.

## What replaced nginx

This is the whole edge now. Alongside routing it does TLS, serves the ACME challenge, redirects to
the canonical HTTPS host, sets the security headers, serves the two upload volumes off disk, and
rate limits per client IP — that last one being the thing the services genuinely cannot do, since
their own limiter policies have no partition key and are therefore global buckets.

certbot still renews on a host cron. It writes to the same paths; its deploy hook restarts this
container instead of reloading nginx.

One ordering trap worth knowing: `UseStaticFiles` stands aside whenever routing has already
selected an endpoint, and the frontend catch-all route matches every path. `UseRouting` is
therefore called explicitly, after the static file middleware. Move it and every avatar quietly
starts being served through Next instead of off disk.

## Pointing a service somewhere else

Each destination is overridable with one env var, so you can run six services from images and the
one you are working on from source:

```bash
SVC_FINANCE_URL=http://host.docker.internal:5083
```

The names are `SVC_IDENTITY_URL`, `SVC_FORUM_URL`, `SVC_FINANCE_URL`, `SVC_NOTIFICATIONS_URL`,
`SVC_HOUSEHOLD_URL`, `SVC_MATH_URL`, `SVC_GEOGRAPHY_URL`. Note the path prefix for household is
`/api/households` while the service and variable are singular.

A service that is not running answers 502 on its own routes and nothing else — the gateway resolves
destinations per request, so it starts fine against a partial stack. That is the difference from
nginx, which resolves every `upstream` at startup and refuses to boot with
`host not found in upstream` if one hostname is missing.

So running a subset is just running a subset:

```bash
docker compose -f compose.dev.yaml up -d postgres rabbitmq identity finance gateway
```

Requests to `/api/forum/*` return 502; everything else works.

