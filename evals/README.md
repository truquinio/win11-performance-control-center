# Real-World Reliability Lab

This directory contains executable reliability scenarios derived from real incidents and destructive-safety requirements.

## Layers

- regressions/: confirmed failures that must never return.
- capabilities/: difficult diagnostic/repair tasks that measure what the product can do.
- fixtures/: reusable disposable test state.
- graders/: outcome grading rules.
- reports/: generated summaries (not source-of-truth test inputs).

The first gate is storage cleanup because a real cleanup incident removed Claude extension runtime files. The regression does not merely inspect an allowlist: it builds a disposable filesystem fixture, runs the real cleanup service and grades the resulting state.

## Rules

1. Never point an eval at the user's real AppData or personal files.
2. Destructive evals run only under an isolated temporary root, Windows Sandbox or a disposable VM.
3. A confirmed production bug must gain a regression scenario.
4. A scenario must test outcomes, including at least one positive or negative expectation.
5. scripts/verify.ps1 is the canonical CI gate and runs both manifest validation and executable real-world evals.

Current historical regression: claude-mcp-survives-cleanup.
