# Security

## Reporting a vulnerability

Please do not open a public issue for a security problem. Use GitHub's private vulnerability reporting on this repository (Security tab, then "Report a vulnerability"). Include steps to reproduce and the version shown on the Help tab. You should get a reply within a week.

## What the app is designed to protect

- **Your mods and saves.** Nothing is installed without a confirmation, every replaced folder is backed up first, and save games are never read or written.
- **Archives.** Downloaded `.zip`, `.7z` and `.rar` files are treated as untrusted. Entries with absolute paths, `..` segments, invalid names or duplicate names are rejected, file counts and sizes are capped, and the mod ID inside must match the mod being updated.
- **Your Nexus key.** It is stored with the best place each system offers (Windows DPAPI for the current user, or the desktop keyring or a mode-600 file on Linux), sent only to the Nexus API, and left out of the problem report.
- **Your network.** The app opens no listening port. A second copy of the app talks to the first through a local pipe that only the current user can open. Web links are opened only on a short allow-list of sites over https.
- **Shared profiles.** A profile file lists mod IDs, names, versions and where to get them. It is size limited, its entries are checked, and opening one only creates a saved list: nothing is installed or switched until you choose to.
- **Updates to the app itself.** Each release carries a SHA-256 list of its files and a signature of that list (ECDSA P-256). The matching public key is built into the app, so a replaced file on GitHub fails the signature check and a damaged download fails the hash check. Downloads are taken only from this project's own release files, size limited, and the new program is started once with `--version` to confirm it works before it replaces the old one. The private key is held only as a GitHub Actions secret and by the maintainer. Without a signature the app does not install anything; it only links to the release page. Looking for updates can be switched off in settings, and it sends nothing but the request for the release list.
- **Mod pictures.** Downloaded pictures are stored only when the bytes really are an image, under 2 MB, from an allowed site (also after redirects).

## Out of scope

Problems in mods themselves, in SMAPI, or in the sites the app downloads from.
