# MedStock – Home Assistant app

[![Add repository to Home Assistant](https://my.home-assistant.io/badges/supervisor_add_addon_repository.svg)](https://my.home-assistant.io/redirect/supervisor_add_addon_repository/?repository_url=https%3A%2F%2Fgithub.com%2Fjoachimcarrein%2FMedStock)

MedStock is a Home Assistant app (add-on) that tracks the medication stock of a household
and tells you **how many days you have left**, without logging every single intake.

- Stock goes down by itself, based on the usage you configure: per day, a weekly pattern
  (e.g. one injection every Saturday) or "as needed".
- Restocking is one tap: "+1 box", "+2".
- One list (profile) per person; everybody logged in to Home Assistant sees and edits everything.
- Push notifications through the Home Assistant Companion app when something runs low.
- A doctor screen that tells you what to ask for, and how many boxes, to cover the next 90 days.
- Works inside the Home Assistant sidebar and Companion app via ingress. No extra ports, no cloud,
  no external services. Data is part of your Home Assistant backups.

The UI is available in English and Dutch.

## Installation

1. Click the badge above, or go to **Settings › Apps › App store › ⋮ › Repositories** and add
   `https://github.com/joachimcarrein/MedStock`.
2. Install **MedStock** (a prebuilt image for amd64 or aarch64 is downloaded).
3. Start it and enable **Show in sidebar**.

See [DOCS.md](medstock/DOCS.md) for the full documentation.

## Development

Requires the .NET 10 SDK.

```sh
dotnet test medstock/src/MedStock.sln
ASPNETCORE_ENVIRONMENT=Development dotnet run --project medstock/src/MedStock.Web
```

In development the app uses a fake Home Assistant client, a local SQLite database under
`%LOCALAPPDATA%\MedStock\dev` (or `$MEDSTOCK_DATA_DIR`) and the user `dev`.
Add `--demo` to seed fictional demo data.

## License

MIT
