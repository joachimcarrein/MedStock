# Contributing to MedStock

Thanks for helping! MedStock handles medical data of households, so a few rules are stricter than usual.

## Ground rules

- **No real data.** Never put real medication, profile or person names in code, tests, screenshots, issues or logs.
  Use made-up names ("Medicine A") or the demo data (`--demo`).
- **Logs contain identifiers only** at `info`/`warning`/`error` level: no medication, profile or user names, no
  notification texts. There are tests for this.
- **No external calls** except the Supervisor proxy and the user-initiated picture search. No telemetry.
- **English** for code, comments, commits, docs. UI texts in English and Dutch.

## Development

Requires the .NET 10 SDK (and Python 3 for the resource generator).

```sh
dotnet test medstock/src/MedStock.sln
ASPNETCORE_ENVIRONMENT=Development dotnet run --project medstock/src/MedStock.Web -- --demo
```

Open <http://localhost:5289/api/hassio_ingress/dev/>. In development the app uses a fake Home Assistant, a local
SQLite database under `%LOCALAPPDATA%\MedStock\dev` (or `$MEDSTOCK_DATA_DIR`) and the user `dev`. The fake ingress
prefix makes missing path prefixes visible right away.

- **UI texts:** add every key to `medstock/src/MedStock.Web/Resources/SharedResource.resx` (English) and to each
  language file (`SharedResource.nl.resx`, ...). A test fails when a language misses a key or a placeholder (`{0}`).
- **A new language:** copy `SharedResource.nl.resx` to `SharedResource.<code>.resx`, translate it and add the code to
  `UiCultures` in `Middleware/Localization.cs`; it then appears in Settings › Language.
- **Database changes:** add an EF Core migration in the same pull request
  (`dotnet ef migrations add <Name> --project medstock/src/MedStock.Web --output-dir Data/Migrations`). Migrations
  must be additive (new tables/columns with defaults), so an older version can still open a newer database.
- **URLs:** behind Home Assistant ingress every URL needs the path prefix. Use `Url.Page(...)` / `Url.Content("~/...")`,
  never a literal `/...` in `hx-*` attributes or JavaScript. The app root is `Url.Content("~/")` (with trailing slash).

## Pull requests

1. Fork, create a branch, make the change with tests.
2. Add a line under `## Unreleased` in `medstock/CHANGELOG.md` for every user-visible change. Do **not** change the
   version: that happens at release time.
3. Open a pull request. CI runs the tests, the Docker build and a smoke test under the AppArmor profile. The
   maintainer reviews every pull request before it is merged.

## Releases (maintainer)

**Actions › Release › Run workflow** (or `gh workflow run release.yaml`). Versions follow the date like Home
Assistant: `YEAR.MONTH.N` (2026.11.0, 2026.11.1, then 2026.12.0 next month); the workflow picks the next number
itself. A version can still be given with `-f version=...` (it must be higher than the current one). The
workflow bumps the version, turns `## Unreleased` into the release notes, tests, builds and signs the images, checks
that they can be pulled, and only then updates `main` and creates the tag and GitHub release. Home Assistant offers
the update as soon as `main` has the new version.
