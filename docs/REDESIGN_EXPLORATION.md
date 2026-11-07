# Redesign exploration

Status: direction A plus the readiness strip was chosen by the maintainer on 2026-11-07 and built (see "Result" at the end). Written against version 1.2.0 plus the four commits after it. It follows the exploration steps in the maintainer's design rules: tasks, references, three concepts, comparison, a recommendation, and acceptance criteria.

## Understanding

Sprinkler's users are Stardew players who are not modders. The design thesis stays: a calm, warm helper that explains itself, and every change can be undone. What is being asked is a redesign of how the screens are organised so that the common jobs take fewer steps and less scrolling, without losing any working behavior (Undo, backups, profiles, 17 languages, keyboard and screen reader names).

## The five tasks that decide the design

| # | Task | How often | Cost of failure |
| --- | --- | --- | --- |
| 1 | See what is outdated and update it | every visit | low (Undo exists) |
| 2 | Find out why the game does not start, and fix it | rare, but the most stressful | high (the player gives up) |
| 3 | Switch between setups (solo, co-op, testing) and start the game | weekly | medium |
| 4 | Add a mod (drop a file, a collection, a required mod) | monthly | medium |
| 5 | Remove or switch off a mod safely | monthly | medium to high |

## Baseline, measured in the real window (Windows, demo folder of 13 mods, 1100 px wide window)

Verified by screenshot:

- Before any content, the header (about 135 px), the tab strip (about 55 px) and a notice (about 75 px) take roughly 265 px. In a 900 px tall window that is about 30 percent, and more on a 768 px laptop.
- An update card is 250 to 330 px tall. Eight updates need about 2,300 px of scrolling. Each card repeats the same four source chips and the same sentence about Nexus logins.
- The same mod appears in two places, an update card on Updates and a row on All mods, with different controls.
- A problem that can stop the game was at the very bottom of Updates (fixed last session by a banner; the list itself is still at the bottom).
- Tabs, notices and dialogs all reflow cleanly at 780 and 1100 px wide. Nothing clips.

Inferred, not measured: that first-time players do not know which of the six tabs to open for task 2; that repeated source chips slow scanning.

Not tested: keyboard-only use end to end, screen readers, per-monitor DPI changes, text scaling, Linux.

## References studied

Fetched and read for this document:

- Microsoft, [NavigationView](https://learn.microsoft.com/en-us/windows/apps/design/controls/navigationview): top navigation suits a set of equally important top-level categories, and left navigation suits narrow windows. Sprinkler's six tabs fit this and already switch to compact tabs below 940 px.
- Microsoft, [Content layout and spacing](https://learn.microsoft.com/en-us/windows/apps/design/basics/content-basics): an 8, 12 and 16 px spacing rhythm, a type ramp, and multi-line list rows with a title, a caption and a 32 px icon. Rows, not tall cards, are the Windows pattern for lists of items.
- GNOME, [Banners](https://developer.gnome.org/hig/patterns/feedback/banners.html): a banner is for an ongoing condition, sits at the top of a view and stays until the condition ends; one-time events belong in toasts. Sprinkler already follows this (the SMAPI notice is a banner, results are toasts).
- Stardrop's [release notes](https://github.com/Floogen/Stardrop/releases): the closest product. It uses a table of mods with sortable, reorderable columns, profiles, notes and a changelog viewer. Open requests include launching a profile directly (now in Sprinkler as Switch and play).

Not obtained: a fetched example from a product outside mod managers (for example an operating system update page or a game store's downloads page). Those patterns are used below from general knowledge and are marked as unverified.

## Three concepts

### A. Convention-first: "Tighter tabs"

Keep the six tabs. Replace tall update cards with one-line rows that expand in place (name, version change, source, a checkbox). Say once, above the list, how downloads work (the Nexus login sentence), not on every card. Make the filter and search bar and the Update button sticky. Merge the header chips into one status strip. Put the Heads up list above the rows when it has an error.

Strengths: lowest risk, keeps every behavior and test, familiar. Weakness: the same mod is still in two places.

### B. Workflow-first: "One list, one inspector"

Merge Updates and All mods into a single Mods screen: a table (switch, mod, version, status, source) with filters such as Needs update (8), plus a right-hand inspector for the selected mod (sources, changelog, notes, folder, remove). A left rail holds Mods, Profiles, History, Settings, Help. Update all is a bulk action on the filtered rows.

Strengths: removes the duplicate lists, matches Stardrop and Vortex habits, best for large mod folders. Weakness: a real rewrite of the main screens, more to learn for first-time players, the inspector needs width (below about 900 px it must become a sheet).

### C. Distinctive: "Pre-flight"

The home screen is a readiness checklist whose end is the Play button: SMAPI installed and current, no duplicate or missing mods, no errors from the last session, updates waiting. Each line is one sentence with one fix button and expands in place. Updates become one of the lines, opening a compact row list. All mods, Profiles and History are secondary tabs.

Strengths: matches the real goal (get to a healthy game), directly serves task 2, product specific. Weakness: risks becoming the generic hero-plus-status-tiles pattern if the lines are decoration, and Updates gets one click deeper.

## Comparison

| Criterion | A Tighter tabs | B One list | C Pre-flight |
| --- | --- | --- | --- |
| Usability for non-modders | high | medium | high |
| Learnability | high | medium | high |
| Accessibility (keyboard, focus order) | easy, already tested | harder (table plus inspector) | easy |
| Original identity | low to medium | low (looks like other managers) | high |
| Performance | same | needs virtualized table | same |
| Feasibility and risk | low | high | medium |
| Fixes measured pain (height, repeated text, buried problems) | yes | partly | partly |

## Recommendation

Build **A**, and take the one idea from **C** that is not decoration: the problems and readiness lines (duplicates, missing requirements, SMAPI, last session) become a compact "Before you play" strip at the top of Updates, each with its fix button, instead of a list at the bottom. Do not build B now: it rewrites the screens that work and adds learning cost for the people the app is for. Revisit B if mod folders of several hundred mods are a common complaint.

Why not C alone: it moves updates, the most common task, one level deeper for the sake of a metaphor.

## Acceptance criteria (for A plus the readiness strip)

- With 8 updates in a window of about 1,000 px of usable height, at least four update rows and the Update button are visible without scrolling when nothing needs attention. Before the redesign it was one partly visible card.
- The same information is available per update as today (versions, sources and their modes, changelog, skip this version), reachable with the keyboard, with accessible names unchanged or better.
- A collapsed row shows only "Source, how the file arrives". The long note about logging in to Nexus appears only inside a row that was opened.
- Any error-level problem is visible without scrolling on Updates.
- No behavior changes: Undo, backups, profiles, drag and drop, all 17 languages. All 482 tests pass, with new tests for the changed view models.
- Checked at 968 (the smallest the window went in the test) and 1100 px wide, light and dark, a long Russian string, and with 0, 1, 7 and 8 updates. A run with 60 updates was not done.

## Non-goals

No new framework, no new navigation model, no change to the palette or the icon, no new network calls.

## Result (built 2026-11-07)

What changed:

- A ready update is one row: tick box, picture, name, the chosen source and how the file arrives, the version change and a chevron. The source buttons, the changelog, the page link and "Skip this version" open in place. A failed update opens by itself, and queued, working, waiting and done rows look as before. The open or closed state survives refreshes.
- The top of Updates is one block: the headline and the four counts (each still opens its filter) share a panel. The step chips are hidden during the review, and "Tick what you want, then press Update" is the header of the label legend instead of a panel of its own.
- Problems are above the rows, errors first, at most three shown (every error always shows) and the rest folded under "Show N more". This replaces the banner added earlier and the list that sat at the bottom.

Measured in the real window on the demo folder (Windows, dark, 1100 px wide, window capture about 1,100 px tall of which about 1,030 are above the taskbar):

| State | Before | After |
| --- | --- | --- |
| Healthy, 7 updates | one card partly visible above the Update bar | four full rows and part of a fifth |
| With the SMAPI notice and one error | one card partly visible | two and a half rows |

So the acceptance criterion of five rows in 900 px was not met in the error case and only nearly in the healthy case. Further savings would come from the header (about 135 px) and the tab strip, which were left alone on purpose.

Also verified: Russian text in the light theme, an opened row, and 486 automated tests (4 new ones for the row behavior). Not verified: keyboard-only use of the chevron beyond its accessible name being present, a screen reader, 60 updates, Linux.
