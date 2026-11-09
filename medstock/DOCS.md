# MedStock

MedStock tracks the medication stock of your household and calculates how many days each
medication will last. You enter the stock once (open package + sealed packages) and the usage
(per day, a weekly pattern, or as needed); from then on the stock goes down by itself.
You only act when you buy more ("+1 box") or when you want to correct the count ("Recount").

## Requirements

- Home Assistant OS or Supervised (an installation with the Supervisor and apps).
- For push notifications: the Home Assistant Companion app on the phones that should receive them.
- Optional: the Mosquitto broker app (sensors that survive restarts) and a to-do list such as "Local To-do" (for the to-do extra).

## Install and update

1. Add the repository `https://github.com/joachimcarrein/MedStock` under
   **Settings › Apps › App store › ⋮ › Repositories** (or use the badge in the README).
2. Install **MedStock**. A prebuilt image (amd64 or aarch64) is downloaded; this takes about a minute.
3. Start the app and enable **Show in sidebar**.

To update: **App store › ⋮ › Check for updates**, then **Update** on the app page. Your data is kept.

## Getting started

1. **Create a profile.** A profile is a list, usually one per person ("Husband", "Wife"). Open MedStock: with no
   profiles yet it offers **New profile**. Pick a name and a colour. You can add more later under
   **Settings › Profiles**, or straight from the medication form (**+ New profile…**).
2. **Add a medication** with the **+** button on the overview. The important fields:
   - **Type**: decides the words used ("2 bottles + 20 doses", "3 injections") and the icon.
   - **Units per package**: how many units one box, bottle or pack contains. A unit is what you count when you
     use it: one pill, one spray dose, one injection, one ml.
   - **Usage** (see the examples below): **Every day**, **Weekly pattern** or **As needed**.
   - **Current stock**: units left in the **open** package plus the number of **sealed** packages.
   - **All of today's doses already taken**: tick it if today's doses are already used (ticked by default after
     20:00); otherwise today's dose is subtracted from what you entered.
3. **Set up your notifications** in **Settings › My notifications**: choose your phone(s) and tap **Save** once.
   Until you save, you receive nothing. **Send test notification** checks that it arrives.

### Usage examples

| Medication | Type | Units per package | Usage | Stock entered |
|---|---|---|---|---|
| Nasal spray, 2 sprays in each nostril twice a day | Nasal spray | 140 (doses per bottle) | Every day: **8** | 100 left in the open bottle + 1 sealed |
| Weekly injection on Saturday | Injection | 4 (pens per pack) | Weekly pattern: Saturday **1**, other days 0 | 3 left, 0 sealed |
| Half a pill every other day | Pill | 28 | Every day: **0.25** | 20 + 0 |
| Painkiller when needed | Pill | 20 | As needed, minimum stock **10** | 6 + 1 |

- **Every day**: type the number of units per day, or open **Help me calculate**: "3 times a day × 2 per time", or
  fill in morning / noon / evening / night; MedStock adds it up. Decimals are fine (0.5 = one unit every two days);
  both `0.5` and `0,5` are accepted.
- **Weekly pattern**: units per weekday. **Same every day** copies the first value to all days.
- **As needed**: no fixed usage, so no "days remaining". The stock only goes down when you tap **−1** / **−n** on the
  detail screen. The **minimum stock** decides when it counts as low (0 = only when empty).

## Daily use

### Overview

All medication, the most urgent first, with the stock ("1 bottle + 20 doses"), a badge and the date of the last dose.
The chips at the top filter by profile; your choice is remembered per Home Assistant user.

| Colour | Badge | Meaning |
|---|---|---|
| Red | **Out** | No stock left. |
| Red | **last day** | Today's dose is the last one. |
| Red | **n days left** | At or below the alert threshold (default 14 days). |
| Red | **as needed · n left** | As-needed medication at or below its minimum stock. |
| Orange | **n days left** | Within twice the alert threshold. |
| Green | **n days left** | Enough stock. |
| Grey | **as needed · n left** | As-needed medication above its minimum. |
| Grey | **paused** | Paused: the stock does not go down. |

Every badge also has its own icon, so colour is never the only signal. Days are always rounded **down**, and today's
dose already counts as taken. More than ten years of stock shows as "> 10 years".

### Detail screen

- **+1 box** (the word depends on the type) and **+ n**: you bought packages. Shows "Added: +1 box" for a few seconds.
- **Recount**: correct the stock with what you actually have (open package + sealed packages, with the same
  "already taken today" tick box).
- **−1** / **− n** (as-needed medication only): you used some.
- Icons next to the name: **✏ edit**, **⏸ pause / ▶ resume**, and the red **archive** icon (asks for confirmation).
- **Photo**: take one, choose one from the gallery, or **search a picture by name** (see *Optional extras*).
- A change in usage (via edit) applies **from tomorrow**. If someone else changed the same medication in the
  meantime, MedStock shows "Someone else just changed this" and asks you to check and try again.

### Prescription

Per profile: what to ask your doctor for and how many packages, to cover the **coverage period**. The period is
taken from the medication, else the profile, else the global setting (default 90 days). Items you need are at the
top; **Share** sends the list via the share sheet, or copies it when sharing is not available (for example when
Home Assistant is opened over plain http at home). The text also stays selectable in the box below the list.

### Archive

Medication you no longer use: **archive** it from the detail screen. **Settings › Archived** lists it per profile,
where you can **restore** it or **delete permanently** (stock history and photo included). A profile can only be
deleted when it has no medication left, archived included.

## Notifications

- **Who receives them**: each user chooses in **Settings › My notifications** whether they want notifications, on
  which devices (`notify.*` services; Companion app phones are suggested) and for which profiles (all ticked = all
  profiles, including new ones). Remember to tap **Save** once.
- **When**: once a day at the configured time (default 09:00, local time of your Home Assistant). If MedStock was not
  running at that time, the check runs as soon as it is back.
- **Which steps**: a notification is sent when the days remaining reach the alert threshold (default 14 days, per
  medication adjustable), again at half and a quarter of it, and when it is empty: 14 → 7 → 3 → 0. Each step is
  sent only once; buying enough stock re-arms them. As-needed medication gets one notification when it reaches its
  minimum stock.
- At most one notification per medication per run. A newer one about the same medication replaces the older one.
- **Tap** the notification to open MedStock. Its **+1** button (e.g. "+1 bottle") books one package without
  opening the app.
- **Language**: your language setting, or the language of your last visit when it is set to *Automatic*.
- **Send test notification** sends a test to your own saved devices. **Run the daily check now** (Settings › For
  everyone) runs the real check immediately and lists what it did; it never sends the same step twice.

## Optional extras

All off by default.

### Picture by name

Detail screen › **Search a picture by name (internet)**. Choose a source, adjust the search text if needed (the brand
name or the active substance), tap **Search** and tap a picture to use it as the photo. **Other pictures** shows the
next ones.

- **Wikimedia Commons**: free pictures of all kinds.
- **DailyMed** (US National Library of Medicine): official packaging and label pictures of medicines sold in the US.
  Good for brand names that also exist there (e.g. Weekject, Nasalix); products sold only in Belgium are not in it.
  Packaging pictures are shown first.

### Sensors in Home Assistant

**Settings › For everyone › Sensors in Home Assistant** has two independent options:

**A "days remaining" sensor per medication**

- Entity: `sensor.medstock_<medication>_<profile>`, for example `sensor.medstock_nasalix_husband` (the strength is
  part of the name when you filled it in). You can rename entities in Home Assistant as usual.
- State: days remaining (unit `d`); *unknown* for as-needed or paused medication.
- Attributes: `remaining_units`, `packages`, `unit`, `last_dose_date`, `medication`, `profile`.

**A summary sensor per profile** (no medication names, for automations or a shared dashboard)

- Entity: `sensor.medstock_<profile>_days_left`, for example `sensor.medstock_husband_days_left`.
- State: the lowest days remaining of that profile's medication (as-needed and paused medication do not count).
- Attributes: `medications` (how many), `low` (red), `warning` (orange), `profile`.

Both are updated after every change in MedStock and at least every 15 minutes. Archiving or deleting a medication,
or turning an option off, removes its sensor again.

**With or without MQTT.** When the Mosquitto broker app is installed, MedStock uses MQTT discovery (it gets the
credentials from the Supervisor): one device **MedStock** appears under **Settings › Devices & services › MQTT**
and the sensors survive Home Assistant restarts. Without a broker the sensors are written as plain states: they
work the same in automations and dashboards, but have no device, cannot be renamed in the UI, and are missing for
up to 15 minutes after a Home Assistant restart.

Example automation trigger: `numeric_state` of the sensor below 7.

### To-do list

**Settings › For everyone › Add medication that runs low to this to-do list**: choose a `todo.*` list (for example
"Local To-do" or the shopping list). MedStock adds "Name strength (profile)" to that list:

- at every notification step, unless it is still open on the list;
- once for medication that is already low when you choose the list (at the next daily check, or with
  **Run the daily check now**).

Check it off when you have bought it. If the stock is still low, it comes back at the next notification step.

### Export

**Settings › Export data (JSON)** downloads `medstock-export-<date>.json` with the profiles, all medication (archived
included) with their current stock, and the general settings. Photos and personal notification settings are not
included. There is no import yet; the file is meant for keeping or moving your data.

## Privacy

| What | Where it goes |
|---|---|
| Everything you enter, photos | Only in the app's own folder (`/data`), part of your Home Assistant backups. |
| Notification texts (medication and profile names) | To the devices chosen by each user, via Home Assistant. |
| Sensors per medication (optional) | Medication and profile names become visible in Home Assistant's history, logbook and dashboards, to every Home Assistant user. |
| Summary sensors (optional) | Profile names and numbers only. |
| To-do list (optional) | "Name strength (profile)" on the chosen Home Assistant list. |
| Picture search (optional) | Only when you tap **Search**: the search text to Wikimedia Commons or DailyMed; your phone loads the previews from there. |
| App log | Identifiers and counts only, never medication or profile names (at `info` level). |

MedStock contacts nothing else outside your Home Assistant.

## Configuration

| Option | Description |
|---|---|
| `log_level` | `trace`, `debug`, `info` (default), `warning`, `error`. Restart the app after changing it. |

All other settings are in the app itself (**Settings**).

## Users and access

- Your Home Assistant accounts are the users. There is no separate login.
- Everybody who can log in to Home Assistant can see and edit everything (`panel_admin: false`), including
  non-administrators. Only administrators can install, update or configure the app.
- Profiles are not linked to Home Assistant users or persons.
- Per user only notification preferences, language and the last chosen profile filter are stored.
- Should Home Assistant ever not pass on who you are, MedStock uses a shared "household" user (without notifications)
  and shows a banner; in **Settings › Who are you?** you can pick yourself.

## Troubleshooting

| Problem | What to do |
|---|---|
| I receive no notifications | Open **Settings › My notifications**, tick your phone and tap **Save**; then **Send test notification**. Check that the profile is ticked and that the medication has notifications on (edit › settings). A step that was already sent is not sent again until the stock is sufficient. |
| "Device not found anymore; choose again" | The phone was renamed or removed in Home Assistant. Tick the new device and save. |
| "Home Assistant not reachable" in Settings | Home Assistant is restarting or busy; reload after a minute. Notifications are retried every minute. |
| Sensors are missing right after a Home Assistant restart | Without the Mosquitto broker app they come back within 15 minutes. Install the broker to keep them across restarts. |
| The medication is still red after buying | Use **+1** / **+ n** (or **Recount**) so MedStock knows; it calculates from the last count. |
| The days look one day off | Days are rounded down and today's dose counts as taken. When you recount, use the "already taken today" tick box. |
| "Session expired – tap to reload" | The Home Assistant session ended after inactivity; tap the banner. |
| Picture search finds nothing | Try the brand name, the active substance, or the other source. |
| Sharing does nothing at home | Over plain http the phone does not allow sharing or copying; the text is selected so you can copy it yourself, or use your https address. |

## Data and backup

- All data is stored in `/data` of the app: `medstock.db` (SQLite), `photos/`, `keys/` (data-protection keys) and
  `backup/` (an automatic copy of the database before each schema upgrade; the last 3 are kept).
- The app is part of your Home Assistant backups. It uses a *cold* backup: the app is stopped for a few seconds during
  the backup so the database is consistent.
- The app runs under its own AppArmor profile: it can only write to `/data` and `/tmp`.

## Limitations

- Android: taking a photo with the camera may open the gallery instead; choose a photo there.
- No import of an export file yet.
- If a schema upgrade fails, the app stops and logs the path of the database copy in `/data/backup`. Restore a Home
  Assistant backup, or open an issue with the log (it contains no medication names).
