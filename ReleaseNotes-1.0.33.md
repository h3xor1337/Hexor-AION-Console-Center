# Hexor 1.0.33

## Bot module controls fixed
- The module status now detects the installed automation module (dxgi.dll) reliably on every layout — Steam, Purple, or a game folder outside a Steam library — even when the game is not running. It previously showed "Not detected" on several installations.
- Unload menu is now truly temporary: the module is renamed beside the game instead of deleted, and the same button restores it. The button switches between Unload and Restore automatically.
- Remove Bot Module permanently deletes the module (and any unloaded backup) from every detected installation. Installation detection no longer scans every file on every drive — the scan now takes seconds instead of minutes, with visible progress and a disabled button while it runs.
- Removal dialogs are fully translated in all ten console languages.

## Installation
This update installs automatically: at launch and while the console is open. The console restarts itself after the install.
