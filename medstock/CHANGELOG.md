# Changelog

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
