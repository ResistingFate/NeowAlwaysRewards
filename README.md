# NeowAlwaysRewards

NeowAlwaysRewards makes normal Neow rewards available in custom and challenge-style runs, not just standard runs.

<img src="NeowAlwaysRewards/NeowAlwaysRewards.gif" alt="Alt Text" width="480" />

## What it does

In vanilla, runs with modifiers can take a different Neow path that only presents modifier-specific challenge options. This mod preserves that flow, then chains normal Neow rewards afterward.

### Result
- Standard runs: vanilla Neow behavior
- Custom runs without interactive modifier Neow options: normal Neow rewards appear
- Custom runs with modifier-driven Neow options (for example Sealed Deck / Draft style flows): modifier options appear first, then normal Neow rewards

## How it works

The mod does not permanently replace all of Neow’s reward logic.

Instead it:
- preserves the vanilla modifier-option sequence
- detects when the final modifier option has resolved
- temporarily swaps `RunState.Modifiers` to an empty `IReadOnlyList`
- calls vanilla `GenerateInitialOptions()`
- restores the original modifiers

That lets the game generate the normal Neow rewards using current vanilla logic.

## Why this approach

This is more robust than copying and editing the full `GenerateInitialOptions()` reward branch by hand.

Benefits:
- less fragile across game updates
- preserves modifier/challenge-specific Neow flows
- better compatibility with other mods that postfix `GenerateInitialOptions()`

## Compatibility

This mod is designed to be relatively friendly to other mods that modify Neow rewards, especially mods that postfix `GenerateInitialOptions()` to:
- add more rewards to the pool
- append a fourth choice
- alter the final returned option list

Patch order can still matter when multiple mods postfix the same method.

## Files

- `Main.cs` — mod entrypoint and Harmony setup
- `NeowPatch.cs` — Neow flow patches and helper logic
- `CHANGELOG.md` — release notes / development history

## Installation

Place the mod in:

```text
Slay the Spire 2/mods/NeowAlwaysRewards/
Expected contents:

NeowAlwaysRewards.dll
NeowAlwaysRewards.pck
mod_manifest.json
Status
```
Working for:
- standard runs
- custom runs
- challenge/modifier Neow flows
- modifier runs with or without interactive Neow challenge options

Future work
- Monitor patch order interactions with other Neow mods
- Add explicit Harmony ordering if needed
- Trim remaining debug logging

```

