# NeowAlwaysRewards

NeowAlwaysRewards allows Neow rewards to show in Custom Mode and Daily Challenge runs, not just standard runs. (Now works for Multiplayer)

<img src="NeowAlwaysRewards/NeowAlwaysRewards.gif" alt="Alt Text" width="480" />

Notice how you can select Specialist > Insanity > All Stars > and Still get Neow Blessings

## What it does

In vanilla, Neow gives blessings only in standard mode. The reason is likely with Custom Game's Challenge Modifiers like **Draft**, **Sealed Deck**, **Specialize**, **Insanity**, and **All Stars** as they actually count as a clickable blessing when you spawn in with Neow. With this mod, you'll get **Any** of the modifiers you picked first as rewards and then Neow will give you his usual Blessings. Mods that expand Neow's blessing are designed to appear as they normally would.

### Works for:
- Standard Runs
- Daily Runs
- Custom Runs with no extra Neow choices
- Custom Runs with any number of extra Neow choices
- Multiplayer runs (Tested with teams of 2 players)

## How it works

The mod replaces OnModifierOptionsSelected method in the Neow class using a Harmony Prefix. I chose that method as I didn't see many mods changing that method.
I put a Harmony Postfix for the GenerateInitialOption method on low priority as it should run last. What it does is temporarily clear the modifiers and call the original GenerateInitialOptions so the Neow Rewards will show. After that is done the modifiers are restored. I add guarding to stop GenerateInitialOptions from replaying more than once to avoid infinite recursion. In detail:
- preserves the vanilla modifier-option sequence
- detects when the final modifier option has resolved
- temporarily swaps `RunState.Modifiers` to an empty `IReadOnlyList`
- calls vanilla `GenerateInitialOptions()`
- restores the original modifiers

That lets the game generate the normal Neow rewards using the current vanilla logic or any Postfix a mod adds.

## Why this approach

This is more robust than copying and editing the full `GenerateInitialOptions()` reward branch by hand.

Benefits:
- less fragile across game updates
- preserves all Custom Run Modifier flows
- better compatibility with other mods that postfix `GenerateInitialOptions()`

## Compatibility

This mod is designed to be relatively friendly to other mods that modify Neow rewards, especially mods that postfix `GenerateInitialOptions()` to:
- add more rewards to the pool
- append a fourth choice
- alter the final returned option list

Patch order can still matter when multiple mods postfix the same method if you also use Harmony Priority Low.

## Files

- `Main.cs` — mod entrypoint and Harmony setup
- `NeowPatch.cs` — Neow flow patches and helper logic
- `CHANGELOG.md` — release notes / development history

## Installation

Place the mod in:

```text
Slay the Spire 2/
  mods/
    NeowAlwaysRewards/
      NeowAlwaysRewards.dll
      NeowAlwaysRewards.pck
      NeowAlwaysRewardsjson
```

Future work
- Monitor patch order interactions with other Neow mods
- Add explicit Harmony ordering if needed
- Trim remaining debug logging

## Build
- I followed Alchyr's guide with JellyBeans Rider and use their templates:
https://github.com/Alchyr/ModTemplate-StS2/wiki
- I made a Claude.md for working with AI fors making Slay the Spire 2 mods
- I just got JellyBean Rider to make the sln from opening the .cspoj file
- Make sure to edit in .csproj the location of your Slay the Spire 2 game and your Megadot install