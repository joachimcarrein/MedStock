# Changelog

## Unreleased

- Barcode: link the code on the box and open the medication by scanning it from the overview (photo or live camera; read on the phone). For European medicine boxes only the product number is kept.
- Import of a JSON export (adds profiles and medication, never overwrites). The export now includes the barcode.
- Trip planner (Prescription › Going away?): what to buy before leaving and what to take along.
- Discreet notifications (per user): without the medication name.
- Sensors also work without an MQTT broker (written as plain Home Assistant states).
- New option: a summary sensor per profile (lowest days remaining, counts; no medication names).
- "+" on the overview preselects the profile you are filtered on.

## 1.2.2

- No changes in the app: new release process and contribution guidelines for the repository.

## 1.2.1

- To-do list: medication that is already low when you choose the list is added at the next daily check (once per
  cycle), not only at its next notification step.
- Documentation rewritten: getting started with usage examples, colours and badges, sensors, privacy overview and
  troubleshooting.

## 1.2.0

- Picture search: second source DailyMed (US National Library of Medicine) with official packaging and label pictures.
  Packaging pictures are shown first.

## 1.1.0

- Search a picture by name on Wikimedia Commons (free pictures, no AI) and use it as the photo. This is the only
  external call MedStock makes, and only when you tap Search.
- Export all data (profiles, medication with current stock, settings) as a JSON file from Settings.

## 1.0.1

- Detail screen: edit, pause/resume and archive are now icon buttons next to the name.
- Titles no longer lose the bottom of letters such as g and y.
- To-do list: every alert puts the medication on the list when it is not open there yet (also after you checked it
  off while the stock is still low).

## 1.0.0

- Prebuilt, signed images for amd64 and aarch64: installing and updating takes seconds instead of a local build.
- "+1" button in every notification: books one package without opening the app.
- Optional sensors in Home Assistant (MQTT discovery) with the days remaining per medication. Off by default.
- Optional to-do list: medication that runs low is added to a Home Assistant to-do list of your choice.

## 0.1.3

- Security: own AppArmor profile. The app can only write to its data folder and /tmp. Security rating 8/8.

## 0.1.2

- Fix: the profile filter on the overview did nothing. Its form posted to the ingress root without a trailing
  slash, which Home Assistant answers with 404.

## 0.1.1

- Profile filter chips: one form per chip (more robust on iPhone).
- Clearer injection icon.

## 0.1.0

- First release: profiles, medications with per-day / weekly / as-needed usage,
  automatic days-remaining calculation, one-tap restock, recount, pause,
  archive, doctor (prescription) screen with share button, push notifications via Home Assistant
  (threshold, half, quarter and empty; once per step), photos (camera or gallery), English and Dutch.
