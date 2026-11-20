# HEXOR CONSOLE — agent guidance

## antislop
For UI, copy, people, mobile layout, or code comments work, read `agents/antislop.md` (core) and then the skill for the task:
- UI / visual: `skills/antislop-ui/SKILL.md`
- Code comments: `skills/antislop-code/SKILL.md`
Design direction for this project: Hexor Control Center keeps the installed vendor console's dark-purple WPF look (`#100D19` background, `#6942A8` primary action, `#69284A` destructive action, card sections on `Section()` helpers). Match the vendor console's (LegionCommander) structure when adding pages — same reading order, same option behavior (options stay visible and disable when they do not apply). No invented gradients, badges, or fabricated claims; every control must perform a real, verified action before release.

## Interface reference skills
- Layout/grouping/spacing: `skills/better-layout/SKILL.md`
- Surfaces/radius/shadows/icons: `skills/better-ui/SKILL.md`

## Project rules (standing)
- Never start automation on its own; only visible operator buttons send commands.
- No internal jargon in UI or release notes.
- Exclude account management, batch login, proxy management from scope.
- Shared settings store is authoritative for the game; verify writes (mutex, read-back).
- Never modify game files or vendor customer databases without authorization.
- OTA main package must contain exactly 2 executables; releases gate on live testing.
