# MedStock

MedStock tracks the medication stock of your household and calculates how many days each
medication will last. You enter the stock once (open box + sealed boxes) and the usage
(per day, a weekly pattern, or as needed); from then on the stock goes down by itself.
You only act when you buy more ("+1 box") or when you want to correct the count ("Recount").

## Requirements

- Home Assistant OS or Supervised (an installation with the Supervisor and apps).
- For push notifications: the Home Assistant Companion app on the phones that should receive them.

## Install

1. Add the repository `https://github.com/joachimcarrein/MedStock` under
   **Settings › Apps › App store › ⋮ › Repositories** (or use the badge in the README).
2. Install **MedStock**. A prebuilt image (amd64 or aarch64) is downloaded; this takes about a minute.
3. Start the app and enable **Show in sidebar**.

To update: **App store › ⋮ › Check for updates**, then update (or **Rebuild**) on the app page.

## Users & access

- Your Home Assistant accounts are the users. There is no separate login.
- Everybody who can log in to Home Assistant can see and edit everything (`panel_admin: false`),
  including non-administrators. Only administrators can install, update or configure the app.
- Profiles are lists, typically one per person ("Husband", "Wife"). They are not linked to
  Home Assistant users or persons.
- Per user only notification preferences, language and the last chosen profile filter are stored.
- Should Home Assistant ever not pass on who you are, MedStock uses a shared "household" user
  (without notifications) and shows a banner; in **Settings › Who are you?** you can pick yourself.

## Usage

- **Overview**: all medications sorted by urgency (days remaining). Filter by profile.
- **Detail**: "+1 box", "+n", **Recount**, **Pause/Resume**, **Edit**, photo, **Archive**.
  For "as needed" medication: "−1" / "−n".
- **Prescription**: per profile, what to ask your doctor for and how many packages, to cover the
  coverage period (medication › profile › global setting, default 90 days). Use **Share** to send
  or copy the list.
- Days remaining are always rounded down. The dose of today is already counted as taken.
- When you enter or recount the stock, tick **All of today's doses already taken** if that is the
  case (it is ticked by default after 20:00); otherwise today's dose is subtracted.
- A change in usage applies from tomorrow. If someone else changed the same medication in the
  meantime, MedStock asks you to check and try again.
- **Archived** medication (via Settings) can be restored or deleted permanently.

## Notifications

- Each user chooses in **Settings** whether they want notifications, on which devices
  (`notify.*` services of Home Assistant, Companion app devices are suggested) and for which profiles.
  **Open Settings and tap Save once**: until then you receive nothing.
- The notification text uses your language setting, or the language of your last visit when it is
  set to *Automatic*.
- A device that Home Assistant no longer knows (renamed or removed phone) is skipped and marked
  in Settings: "Device not found anymore; choose again".
- Once a day, at the configured time (default 09:00), MedStock checks all medication.
  A notification is sent when the days remaining reach the threshold (default 14 days, configurable
  per medication), and again at half and a quarter of it and when it is empty
  (14 → 7 → 3 → 0). Each step is sent only once; buying enough stock re-arms them.
- At most one notification per medication per run. Tapping the notification opens MedStock.
- **Test notification** sends a test to your own devices; **Run daily check now** runs the check
  immediately.

## Configuration

| Option | Description |
|---|---|
| `log_level` | `trace`, `debug`, `info` (default), `warning`, `error`. Restart the app after changing it. |

All other settings are in the app itself (**Settings**).

## Data & backup

- All data is stored in `/data` of the app: `medstock.db` (SQLite), `photos/`, `keys/`
  (data-protection keys) and `backup/` (an automatic copy of the database before each schema
  upgrade, the last 3 are kept).
- The app is part of your Home Assistant backups. It uses a *cold* backup: the app is stopped
  for a few seconds during the backup so the database is consistent.
- Medication names never appear in the log at `info` level.
- The app runs under its own AppArmor profile: it can only write to `/data` and `/tmp`.

## Limitations

- Android: taking a photo with the camera may open the gallery instead; choose a photo there.
- MedStock never contacts external services. Images are only the photos you take or an icon per type.
- If a schema upgrade fails, the app stops and logs the path of the database copy in `/data/backup`.
  Restore a Home Assistant backup, or open an issue with the log (it contains no medication names).
