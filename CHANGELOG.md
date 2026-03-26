# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]

## [1.1.1] - 2026-03-26

### Fixed
- Fixed compatibility with mods thas was lost in 1.1.0 (Using SoulCapture as a base to test).
- Fixed final Neow reward reconstruction dropping third-party `GenerateInitialOptions()` postfix-added options during multiplayer-safe reward rebuilding.

### Technical
- Extended the cached Neow reward snapshot in `NeowRewardHelper` from a key-only vanilla cache to a mixed cache model.
- Cached reward entries are now tracked as either:
  - `LiveVanilla` entries, rebuilt from fresh `neow.AllPossibleOptions`, or
  - `GeneratedOnly` entries, restored from the originally generated cached `EventOption`.
- Updated `BuildAndCacheVanillaRewardKeys(...)` to record whether each generated reward came from vanilla live options or from a generated-only modded option.
- Updated `GetOrBuildLiveVanillaRewardsFromCachedKeys(...)` to:
  - rebuild vanilla blessings from fresh live options in cached order, and
  - fall back to cached generated options for third-party Neow rewards not present in `AllPossibleOptions`.
- Preserved the multiplayer-safe live-option rebuild path while restoring compatibility with mods such as SoulCapture that append Neow options in a `GenerateInitialOptions()` postfix.
- Added extra debug state output for cached reward kinds and generated-only option keys to verify mixed-cache reconstruction in logs.

## [1.1.0] - 2026-03-26

### Fixed
- Fixed a multiplayer desync during the Neow event in custom runs and daily-style modifier runs.
- Fixed a race where one peer could still be on a single modifier option while the other peer had already advanced to the final Neow blessing choices.
- Fixed intermittent `OptionIndexChosenMessage` failures where the remote player saw fewer Neow options than the selecting player.
- Fixed state divergence/disconnects that could occur when leaving the Neow event room after modifier-based Neow choices.
- Fixed multiplayer instability caused by reusing stale cached `EventOption` instances for the final Neow blessing page.

### Changed
- Reworked the custom modifier Neow flow so modifier-generated Neow options still chain into normal Neow blessings in multiplayer.
- Standard Neow blessings are now reconstructed from fresh live vanilla options after the modifier chain finishes, instead of reusing previously cached option objects.
- The final Neow blessing page now preserves vanilla reward callbacks more closely, improving multiplayer synchronization.
- Logging around the Neow event was expanded to make host/client flow and per-player event state easier to inspect.

### Technical
- Replaced the earlier fallback-style initial reward generation patch with a cache-first approach in `GenerateInitialOptions()`.
- `GenerateInitialOptions()` now caches the selected vanilla Neow blessing keys early, before modifier handling continues, when the run is using modifier-driven Neow options.
- Reworked `NeowRewardHelper` to store lightweight cached reward metadata instead of reusing old `EventOption` instances directly.
- Added a private cached reward key model to preserve the selected blessing order and associated reward identifiers.
- Replaced the old “build cached reward options directly” approach with a live rebuild step that:
    - reads the cached blessing keys,
    - looks up matching entries from `neow.AllPossibleOptions`,
    - rebuilds the final blessing page using fresh live vanilla `EventOption`s in the original cached order.
- This avoids stale option reuse and keeps final reward callbacks on the vanilla code path.
- Added `BeforeChosen` debug hooks to final blessing options for logging without replacing or wrapping the underlying `OnChosen` callback.
- Improved reflection handling by centralizing compatible reflected method lookup instead of assuming a single fixed method shape.
- Added Harmony prefix/postfix logging around `EventSynchronizer` event option handling to inspect:
    - player identity,
    - event id,
    - selected option index,
    - current option count,
    - visible option keys,
    - before/after event state.
- Expanded Neow debug utilities, including safer owner/event identification, option key dumps, localized text-safe logging, and structured JSON state dumps.

### Notes
- The multiplayer issue was narrowed down to a timing-sensitive Neow event desync:
    - one peer could receive a final blessing selection before its local copy of the event had advanced to the same option page.
- The final fix was to keep the modifier-to-blessing flow under mod control, while rebuilding the blessing page from fresh live vanilla options instead of reusing cached or wrapped option instances.

## [1.0.0] - 2026-03-25

### Changed
- Reworked Neow reward handling so standard Neow rewards can appear in custom runs and daily challenge runs, not only standard runs.
- Preserved the vanilla modifier/challenge Neow flow for custom run modifiers that generate their own interactive Neow option, such as Sealed Deck and Draft.
- Modifier-driven Neow options now chain into the normal Neow reward flow instead of ending the event immediately after the final modifier option.
- Added fallback behavior for modifier runs that do not generate interactive modifier Neow options, allowing them to receive normal Neow rewards.

### Technical
- Traced the alternate `Neow.GenerateInitialOptions()` branch through `ModifierModel.GenerateNeowOption(...)` and `OnModifierOptionSelected(...)`.
- Confirmed that the modifier branch is a sequential single-option queue, not a normal three-choice Neow reward screen.
- Replaced the earlier full `GenerateInitialOptions()` override approach with a safer flow that preserves vanilla behavior where possible.
- After the final modifier option resolves, the mod temporarily replaces `RunState.Modifiers` with an empty `IReadOnlyList`, calls vanilla `GenerateInitialOptions()`, then restores the original modifier list.
- This allows the game to build standard Neow rewards using current vanilla logic instead of relying on a copied reward-generation branch.
- Updated debug logging to handle `LocString` values correctly when inspecting reward titles and descriptions.

### Compatibility
- Improved compatibility with future game updates by reducing reliance on copied Neow reward-generation code.
- Designed to work more cleanly with other mods that postfix `GenerateInitialOptions()` to add Neow options or append an extra reward choice.
- Patch/postfix order may still matter when multiple mods modify `GenerateInitialOptions()`, so mod interactions should continue to be monitored.

## [0.1.0] - 2026-03-24

### Added
- Initial release of NeowAlwaysRewards.
- Added Harmony-based Neow reward support for non-standard runs.