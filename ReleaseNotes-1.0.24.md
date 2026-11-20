# Hexor Control Center 1.0.24

## Fixes

- **Gathering filter wipe fixed end to end.** The in-game Target Filter keeps its list in a per-character settings file, and that file was being saved empty. Hexor now repairs the saved list for every character — and the saved defaults — from a character that still has it, or from the built-in default selection, keeping a `.bak-empty-targets` backup of each repaired file. Because the running game keeps its own copy in memory and can save over the repaired file, Hexor also tells the game to reload its configuration right after a repair — the same command the game's own menu uses — so the fix holds while the game is running and after it restarts.
- **"Use default config" button added.** The Daily Activities page now has the same control the vendor console has: it asks the running game to reload its saved default configuration (combat, gathering, and abyss gathering) without restarting.
- **Gathering filter repair can no longer fail silently.** Any unexpected file shape is logged in the activity log instead of interrupting the sweep, so a repair never leaves the filter stuck halfway.
- **Update dialog cleanup:** removed a duplicated window setup line left behind by an earlier edit. No visual or behavioral change.
