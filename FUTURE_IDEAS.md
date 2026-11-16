# Future ideas

Ideas that were discussed and parked. Not planned; reconsider when there is a real need.

## Several barcodes per medication

The pharmacy sometimes hands out another brand or pack of the same medicine, with another code. Linking several codes
to one medication would recognise every variant.

Why parked:
- another pack can hold a different quantity, so "+1 box" after scanning it would add the wrong number of units;
- over the years the list of old codes keeps growing and is hard to keep clean.

Today: scanning the new box and linking it replaces the code; the ✕ in the barcode field clears it.

## Dose change from a date

Schedule a new dose that starts on a chosen date (e.g. a titration schedule: "from 1 December 1 mg"), so the
countdown already accounts for it. Today a change in use applies from tomorrow, so you change it on the day itself.

## Standalone version (without Home Assistant)

Run MedStock as a plain web app for a household without Home Assistant. Without `SUPERVISOR_TOKEN` the app already runs
on its own (as in development), but Home Assistant provides things it would then need itself:

- **Login and identity**: ingress now decides who gets in and who you are. Standalone, every visitor would be the same
  user with full access. Most natural fit: a reverse proxy that authenticates and passes the user in a header
  (Cloudflare Access, Authelia, Authentik), since the app already reads the user from a header.
- **Notifications**: now via Home Assistant's notify services. Standalone needs another channel (ntfy, e-mail, Web
  Push) as a second implementation behind `IHaClient`.
- **Home Assistant only features**: sensors, to-do list and the "+1" action in notifications would be dropped or optional.
- **HTTPS and backups**: now handled by Home Assistant; standalone they are up to whoever hosts it.

An in-between step with little work: run standalone but keep sending notifications through an existing Home Assistant
(its URL plus a long-lived token instead of the Supervisor). Login would still be needed.

Why parked: no household without Home Assistant needs it yet.

## Recount hint

The stock goes down by itself, but a forgotten dose or a spilled pill does not, so the count drifts over time. The
detail screen (and maybe the overview) could give a soft hint after a while without a count, e.g. "Last count 2 months
ago, recount?", with the period as a setting (e.g. 60 days, 0 = off).

Why parked: not needed yet; the intake reminders and undo cover the daily mistakes.
