# Security

## Reporting a vulnerability

Please do not open a public issue for a security problem. Use GitHub's private vulnerability reporting on this repository (Security tab, then "Report a vulnerability"). Include steps to reproduce and the version shown on the Help tab. You should get a reply within a week.

## What the app is designed to protect

- **Your mods and saves.** Nothing is installed without a confirmation, every replaced folder is backed up first, and save games are never read or written.
- **Archives.** Downloaded `.zip`, `.7z` and `.rar` files are treated as untrusted. Entries with absolute paths, `..` segments, invalid names or duplicate names are rejected, file counts and sizes are capped, and the mod ID inside must match the mod being updated.
- **Your Nexus key.** It is stored with the best place each system offers (Windows DPAPI for the current user, or the desktop keyring or a mode-600 file on Linux), sent only to the Nexus API, and left out of the problem report.
- **Your network.** The app opens no listening port. A second copy of the app talks to the first through a local pipe that only the current user can open. Web links are opened only on a short allow-list of sites over https.
- **Mod pictures.** Downloaded pictures are stored only when the bytes really are an image, under 2 MB, from an allowed site (also after redirects).

## Out of scope

Problems in mods themselves, in SMAPI, or in the sites the app downloads from.
