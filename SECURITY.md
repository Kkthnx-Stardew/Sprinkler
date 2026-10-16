# Security

## Reporting a vulnerability

Please do not open a public issue for a security problem. Use GitHub's private vulnerability reporting on this repository (Security tab, then "Report a vulnerability"). Include steps to reproduce and the version shown in the app footer. You should get a reply within a week.

## What the app is designed to protect

- **Your mods and saves.** Nothing is installed without a confirmation, every replaced folder is backed up first, and save games are never read or written.
- **Archives.** Downloaded `.zip`, `.7z` and `.rar` files are treated as untrusted. Entries with absolute paths, `..` segments, invalid names or duplicate names are rejected, file counts and sizes are capped, and the mod ID inside must match the mod being updated.
- **Your Nexus key.** It is stored encrypted with Windows DPAPI for the current user, sent only to the Nexus API, and left out of the problem report.
- **The local page.** The UI is served on 127.0.0.1 only, requires a random per-run token, checks the Host header, and only opens links on a short allow-list of sites.

## Out of scope

Problems in mods themselves, in SMAPI, or in the sites the app downloads from.
