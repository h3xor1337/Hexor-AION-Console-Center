# Hexor 1.0.17

## CAPTCHA auto-resolve refinements
- Humanized answering: a randomized pause (about 1-2 seconds) is added before each answer is submitted so response times no longer look machine-generated.
- Every challenge image is now archived (with its outcome) under Hexor's CaptchaSamples folder, so you can review exactly what the solver received — including answers the game rejected.
- The CAPTCHA page activity log now lists every challenge, answer and result in real time; previously only the optional visual template monitor fed it.
- Solver answers are logged with the correct severity.

## Installation detection fixes
- Steam installs stored with forward-slash paths no longer appear as "Multiple installations".
- The menu-ownership manifest is resolved from the correct folder beside the install, so menu status reports accurately.

## Reliability
- Full automated check coverage: 1,024 checks pass.
