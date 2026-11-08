# Security policy

MedStock stores medication data of households. Please report security problems **privately**:
[open a private security advisory](https://github.com/joachimcarrein/MedStock/security/advisories/new)
(Security › Report a vulnerability). Do not open a public issue.

Please include the MedStock version, what an attacker could do, and how to reproduce it (with made-up data).
You will get a reply within a week. Fixes are released as a new version; the advisory is published once the update
is available.

Only the latest version is supported.

## What MedStock relies on

- Access only through Home Assistant ingress: the app answers only the Supervisor (172.30.32.2) and trusts the
  user headers it adds. Everybody who can log in to Home Assistant can see and edit all data, by design.
- Its own AppArmor profile: it can only write to its data folder and `/tmp`.
- Signed images built by GitHub Actions from this repository.
