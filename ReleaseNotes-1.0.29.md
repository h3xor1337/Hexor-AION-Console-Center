# Hexor 1.0.29

## Smaller, faster updates
- Update downloads are now about half the previous size, so installing an update downloads roughly 60 MB instead of 110 MB and finishes sooner on slower connections.
- If an update download was interrupted earlier, the next attempt reuses the already downloaded package when it matches the signed release, instead of downloading everything again.
- Old, finished or abandoned update files are cleaned up automatically, freeing the disk space they were holding.
- An interrupted or failed update no longer leaves the console stuck: the next check simply picks up where it safely can.

## Installation
- This update installs automatically: at launch and while the console is open. The console restarts itself after the install.
