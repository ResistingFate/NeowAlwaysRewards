# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]

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