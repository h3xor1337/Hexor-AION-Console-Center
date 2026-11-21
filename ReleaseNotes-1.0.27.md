# Hexor 1.0.27

## Daily Activities: full editor and reliable Start/Stop
- The Daily Activities page is rebuilt to match the original console's editor, one column from top to bottom: sequence templates (Restore default, Gold + gather, Gather only, Task + gold + gather, Task + Krao + gold), the step list, add buttons for each activity type (gathering, kinah farming, auto dungeon, Auto Krao Dungeon, switch character) plus Duplicate, Delete, Move up and Move down, the selected step's settings, the after-finish choice, workflow import/export with an Open settings folder button, and the run controls (Start sequence, Stop, Use default config, Start gathering, Stop gathering). Buttons wrap so nothing is cut off at smaller window sizes.
- Start and Stop now use the same command channel the original console uses, so "Start sequence" really starts the daily routine in-game and "Stop" really stops it. This was verified live with the game running: starting showed the routine active in the game's own panel, stopping showed it stopped.
- Kinah farming now picks a realistic daily gold target for each character and day (93%–99% of the daily limit, like the original console), instead of finishing instantly.
- Step settings stay visible but grey out options that don't apply to the selected activity, exactly like the original console.

## Bot module card
- The Bot module status no longer gets stuck on "Verification required" after the game module is updated by the AION updater. It reports "Menu detected" again, and continues to report uncertainty whenever it genuinely can't confirm the module.

## Installation
- This update installs automatically: at launch and while the console is open. The console restarts itself after the install.
