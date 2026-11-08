## What and why

<!-- What does this change, and why? Link the issue if there is one. -->

## Checklist

- [ ] Tests added or updated, `dotnet test medstock/src/MedStock.sln` passes
- [ ] User-visible change: a line under `## Unreleased` in `medstock/CHANGELOG.md` (and `DOCS.md` when needed)
- [ ] Model change: a migration in the same pull request (additive only)
- [ ] New UI text in `Resources/strings.tsv` (English and Dutch), then `python tools/gen_resx.py`
- [ ] No real medication data in code, tests, screenshots or logs
