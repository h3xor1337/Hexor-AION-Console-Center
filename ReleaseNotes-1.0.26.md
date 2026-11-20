# Hexor 1.0.26

## Gathering settings: root-cause fix for cleared gathering lists
- We found why some saved gathering lists were being cleared at unexpected times (after a gather run finished, at character login, or when the game saved on exit): an old saved list on each computer could get out of sync with the game's own Target Filter, and the game would then rewrite or empty it.
- On the first launch after this update, Hexor clears that old saved list once, so the game rebuilds its own clean default list — the same result as the game's Reset Default. A copy of the removed list is kept in the Hexor Logs folder.
- After updating, open the game's Target Filter once and re-select the resources you want to gather. Everything then stays stable.
- Hexor continues to stay out of the game's gathering settings: it never edits them, and only restores a cleared configuration file if the game itself removes it.

## Character Enhancement
- Removed the separate "Gear enhancement level" cap, matching the latest vendor release. The combat power cap slider remains and works exactly as before.

## Auto Dungeon
- Removed the duplicate "Prefer Urugugu" option from the dungeon flow section. Uruguru Cave is chosen in the dungeon priority list, as before.

## Installation
- This update installs automatically: at launch and while the console is open. The console restarts itself after the install.
