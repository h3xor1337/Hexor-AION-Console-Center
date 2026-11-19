# Hexor 1.0.16

## Guardian Verification removed
- The "Avoid Captcha System by Logging out/in" option no longer exists in Hexor. It did not stop CAPTCHA challenges and could interrupt gathering sessions.
- This cleanup applies to every installation: on the next startup Hexor deletes the leftover setting from the game's saved settings, so the behavior is fully disabled everywhere, not just hidden.
- CAPTCHA handling is now the built-in auto-resolver only: when the game shows a challenge, Hexor solves and submits it automatically.

## General tab refresh
- The Diagnostics actions are now arranged in a tidy two-column layout instead of a cramped single stack.

## Under the hood
- Broader automated check coverage for the settings cleanup and update pipeline (1,015 checks pass).
