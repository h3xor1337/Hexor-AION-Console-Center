# Hexor Control Center 1.0.23

## Fixes

- **Gathering filter no longer resets itself.** The in-game Target Filter keeps its list in a per-character settings file, and that file was being saved empty. Hexor now repairs the saved list for every character (and the saved default) automatically, so the filter no longer clears itself mid-session.
- **Start/Stop buttons now follow the task rotation.** New in-game control buttons on the Daily Activities page: Start Tasks, Start Gathering, and Stop — they send the same commands the vendor console sends, including the safe order that stops one mode before starting another.
