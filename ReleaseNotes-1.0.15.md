# Hexor 1.0.15

## CAPTCHA auto-resolve is now built in
- Hexor solves and submits CAPTCHA challenges automatically, with nothing else to run. The separate solver companion is no longer needed: the solving engine now lives inside the console and uses the game's own challenge requests directly.
- The CAPTCHA page opens with the live auto-resolve state: `Auto-resolve is active. Solved / rejected / failed` counters plus a clear armed message. The dashboard tile reads "No challenge yet" instead of jargon.

## Live Control without any extra software
- Hexor now connects to the running game session itself, so live status and in-game controls no longer depend on LegionCommander being open.
- Live sessions show the real character readout (name, level, map, activity) straight from the game.
- The optional controller bridge remains available on its own and is only mentioned where its data appears.

## Friendlier interface
- Removed internal jargon from every page: no more "direct IPC", pipe names, bridge methods or tool names. Sections now say what they do in plain language ("Game connection", "Auto-resolve").
- Settings application messages now state when changes take effect instead of referring to other applications.

## Shipped with this release
- The Hexor-branded AION updater (Hexor Services / hexor.wtf branding, safe self-close after updates) is now delivered automatically with every Hexor install and update, verified against the signed release manifest.

## Fixes
- More robust live session tracking (per-game process lifecycle), clearer solver failure reporting, and a broad pass of automated checks (1,000+ UI and integration checks pass in this release).
