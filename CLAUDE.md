# CLAUDE.md

This file is a working guide for developing **Slay the Spire 2** mods in **JetBrains Rider** with **Godot/MegaDot**, **Harmony**, and the current **Alchyr StS2 template/BaseLib** ecosystem.

It is written as a practical reference for future coding sessions, not as end-user documentation.

---

## 1. Scope and terminology

### What this file is for
- Explaining the Rider + Godot + STS2 mod workflow.
- Recording the important assembly/package roles.
- Explaining why Harmony patches are preferred over inheritance/overrides.
- Recording template-specific conventions that are easy to forget.
- Capturing Rider-specific issues that show up when working from decompiled STS2 code.

---

## 2. Toolchain requirements

Slay the Spire 2 modding currently expects:
- **MegaDot / Godot 4.5.1-compatible tooling**
- **.NET 9**
- a C# IDE such as **JetBrains Rider**
- assuming lastest Slay the Spire 2 stable version is 0.99.1

The current Alchyr template setup guide explicitly recommends Rider, says to download the latest MegaDot or matching-version Godot, and install **.NET 9.0**. The same guide also notes that Rider is preferred because Godot’s C# workflow is centered around solution files (`.sln`).

### Current references
- Alchyr template setup guide: https://github.com/Alchyr/ModTemplate-StS2/wiki/Setup
- BaseLib feature list: https://alchyr.github.io/BaseLib-Wiki/docs/Features.html

### Version reminders
Before starting or updating a mod, verify:
1. `dotnet new install Alchyr.Sts2.Templates` is current.
2. The installed **MegaDot/Godot** version still matches what STS2 expects.
3. The game version has not moved its assemblies or changed relevant APIs.
4. BaseLib and any other package dependencies are up to date.

---

## 3. Rider / Godot / solution-file reality

### Use `.sln` for STS2 Godot+C# projects
For **Godot C#** projects, Rider expects the project’s **solution file** (`.sln`). Godot also generates both a `.sln` and `.csproj` when the C# project is initialized.

### Practical rule
- For STS2 + Godot + C#, open the **`.sln`** in Rider.
- If the template created a `.sln`, use that.
- If only the `.csproj` is opened, Rider can generate a solution when needed, but the Godot/Rider path is simplest when the `.sln` already exists.

### Rider setup notes
- In Rider, use the **Visual Studio** keymap.
- To inspect symbol declarations in decompiled STS2 code:
  - `Ctrl+Shift+A` = menu for Go to Declaration, Usages, Type Declaration
  - `Ctrl+t` = search everywhere useful in Assembly Explorer
  - `Ctrl+B` = Go to Declaration
  - `Ctrl+Shift+B` = Go to Type Declaration
  - `Alt+F7` = Find Usages
  - `Ctrl+F12` = File Structure popup
- If mouse navigation fails, use the caret on the symbol and invoke navigation from the keyboard.
- When navigating assembly decompiled code sometimes searching everything works better that go to declaration.

### Why Rider is particularly useful here
Rider gives you:
- Godot-aware C# support
- decompilation of game assemblies
- strong navigation over decompiled symbols
- a NuGet tool window for package updates
- good support for MSBuild-based C# project editing

---

## 4. Pointing Rider at .NET 9

If Rider does not find the correct SDK automatically:
- Open **Settings / Preferences**
- Go to **Build, Execution, Deployment → Toolset and Build**
- Set a custom **.NET CLI executable path** if needed
- Set a custom **MSBuild** path if needed

This is relevant when:
- .NET 9 is installed in a non-standard location
- Rider is detecting the wrong SDK
- Godot publish/build is failing because of SDK resolution

Also check Rider’s **Environment** page for detected SDKs/components.

### Practical reminder
If Rider is using the wrong SDK, builds may succeed partially, fail strangely, or Godot export steps may break even when the code itself is valid.

---

## 5. Core assemblies and packages

### `sts2.dll`
This is the main game assembly.

Use it to:
- resolve game types like `Neow`, `EventOption`, `ModifierModel`, `Player`, `SetEventState`, etc.
- decompile game logic in Rider
- compile against the game’s public/protected API surface

This is the assembly you inspect most often when patching game behavior.

### `0Harmony.dll`
This is Harmony.

Use it to:
- patch existing methods with Prefix/Postfix/Transpiler/Finalizer
- access patch metadata such as priorities and ordering
- avoid overriding/replacing whole classes when the game already instantiates its own originals

Harmony is the preferred way to alter STS2 behavior.

### `BaseLib.dll`
This is a mod dependency / helper library for STS2 mods, not the game itself.

BaseLib provides utilities and abstractions for content mods and compatibility work. Its documented features include:
- custom model classes for content additions
- automatic ID prefixing
- custom enums/keywords
- node pooling helpers
- `SpireField`
- async patching helpers
- mod configuration helpers
- mod interop helpers
- missing-localization handling that logs instead of crashing

Use BaseLib when you need the features it provides. If your mod does not depend on BaseLib classes, remove the package reference and dependency entry.

### NuGet packages in Rider
You can update package dependencies in Rider using the **NuGet** tool window.

Practical note:
- The NuGet tool window is one of Rider’s tool windows and is usually reachable from the bottom/side tool-window area.
- Use it to inspect, install, update, or remove package references such as BaseLib-related packages.

---

## 6. Current manifest naming: template vs manual guides

### Current Alchyr template convention
The current Alchyr template guide says:
- the mod’s manifest `.json` file has **the same name as the project**
- e.g. `NeowAlwaysRewards.json`

### Practical takeaway
When using the **Alchyr template**, follow the template’s naming and copy/publish logic.

---

## 7. What a basic STS2 mod should contain

At minimum, a typical C# STS2 mod project will have:
- a `.sln`
- a `.csproj`
- a `Main.cs` (or equivalent entrypoint)
- a manifest JSON file
- `project.godot`
- `export_presets.cfg`
- source files for patches/content

### STS2 0.99.1 reworked the mod structure
- mod_manifest.json has been moved outside of the PCK. All mods should now ship with an additional file named <mod_id>.json - see ModManifest for the new JSON structure.
- PCK is no longer required for all mods. Json is the only file that is required to declare a mod. The json must declare whether the mod includes a pck/dll file.
- settings.save now includes a list of all mods that were loaded in the last game run. Reordering this list affects load order.
- Mods may declare dependencies in the manifest. If settings.save declares a load order incompatible with the listed dependencies, then a re-sort is forced.
- Added affects_gameplay field to mod manifest. If false, then the mod will not be checked against the host's mods in a multiplayer game.



### Typical entrypoint pattern (`Main.cs`)
A standard setup uses:
- `using Godot;`
- `using HarmonyLib;`
- `using MegaCrit.Sts2.Core.Modding;`
- a `[ModInitializer(nameof(Initialize))]`
- a static initializer method that creates Harmony and calls `PatchAll()`

Example shape:

```csharp
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace YourMod;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "YourMod";

    public static void Initialize()
    {
        Harmony harmony = new(ModId);
        harmony.PatchAll();

        GD.Print("[YourMod] Harmony patches applied.");
    }
}
```

### Why `Main.cs` matters
This is the point where the mod enters the game. It is where:
- Harmony patching begins
- startup logs are emitted
- your mod becomes active in the runtime

---

## 8. `.csproj` expectations for STS2 mods

A typical STS2 mod `.csproj` based on the current ecosystem includes:
- `Sdk="Godot.NET.Sdk/4.5.1"`
- `<TargetFramework>net9.0</TargetFramework>`
- `<EnableDynamicLoading>true</EnableDynamicLoading>`
- references to `sts2.dll`
- often a reference to `0Harmony.dll`
- package references for BaseLib / analyzers if the template uses them
- build/publish targets that copy files into the game’s `mods` folder

### Why this matters
The `.csproj` is not just compile metadata here. It often also controls:
- where Rider/Godot build outputs go
- where the mod files are copied
- whether Build generates only the `.dll`
- whether Publish also exports the `.pck`

### Build vs Publish with the Alchyr template
With the current template setup:
- **Build** usually compiles code and copies the `.dll` and manifest to the mods folder
- **Publish** additionally exports the `.pck`

This is useful because:
- use **Build** for fast code iteration
- use **Publish** when resources / Godot assets changed and a new `.pck` is required

---

## 9. Godot / MegaDot integration in the workflow

STS2 uses a modified Godot 4.5.1-based runtime. In practice, that means:
- use MegaDot/Godot version compatible with the game
- let Godot handle the project/export model
- let Rider handle C# editing, navigation, and build/project inspection

### How Godot connects to the mod
The mod is a Godot/C# project. Godot contributes:
- the project structure (`project.godot`)
- export settings (`export_presets.cfg`)
- PCK packaging
- the Node/C# runtime integration

### Build outputs that matter
Typical final mod files:
- `YourMod.dll`
- `YourMod.pck`
- `YourMod.json`

### `GD.Print()` logging
`GD.Print()` is useful for:
- confirming mod startup
- tracing patch execution
- debugging event flow

In practice, mod authors often inspect logs under:
- `%AppData%\Roaming\SlayTheSpire2\Player.log`
- and/or the game’s `logs` folder under the local SlayTheSpire2 data directory

When debugging a mod, treat `GD.Print()` and the STS2 log files as first-line diagnostics.

---

## 10. Why Harmony patches are preferred over overrides/inheritance

### The short version
Do **not** assume subclassing a game class and overriding one method will affect the game.

Why not:
- the game already instantiates its own concrete classes
- your subclass is usually never constructed by the game
- replacing all instantiation sites is harder and much less compatible

### Why Harmony is better
Harmony patches the actual methods that the game is already calling.

That makes it suitable for:
- adjusting arguments/results
- adding behavior before/after existing logic
- replacing logic only when absolutely necessary
- cooperating with other mods that patch the same method

### Preferred patch types
- **Postfix**: preferred when possible, because it preserves vanilla behavior and composes more cleanly
- **Prefix**: use when you need to alter inputs or skip the original method
- **Transpiler**: use only when you must surgically change IL-level behavior
- **Finalizer**: use for exception-aware cleanup/error handling

### Prefix behavior to remember
A `Prefix` that returns `false`:
- skips the original method
- skips remaining side-effect prefixes
- still allows postfixes/finalizers to run if applicable

### Postfix behavior to remember
A `Postfix`:
- runs after the original method
- is generally more compatible than a full replacement prefix
- is often the right place to merge or adjust returned values

---

## 11. Harmony patch ordering and multi-mod interaction

When multiple mods patch the same method, **patch order matters**.

### Order is not just “load order”
Harmony resolves order primarily through patch metadata:
- `HarmonyPriority(...)`
- `HarmonyBefore(...)`
- `HarmonyAfter(...)`

If these are equal or absent, then registration order becomes the fallback.

### Practical rule
If your mod is acting as a fallback or merger patch, use a **low-priority postfix** so other mods can modify the result first.

Example:

```csharp
[HarmonyPatch(typeof(SomeType), "SomeMethod")]
public static class SomePatch
{
    [HarmonyPriority(Priority.Low)]
    static void Postfix(ref SomeReturnType __result)
    {
        // fallback/merge logic
    }
}
```

### When to use `HarmonyAfter`
Use `HarmonyAfter("other.mod.id")` when your patch should see another mod’s final result before acting.

### Why this matters for STS2 reward/event mods
If multiple mods postfix `GenerateInitialOptions()` or another event-selection method, patch order can decide:
- whether options are merged
- whether one mod overwrites another’s result
- whether a fallback patch runs too early or too late

---

## 12. Why `Traverse` is often required

The correct Harmony helper name is **`Traverse`**.

### Why it is needed
When patching STS2 methods, many members you need are:
- `private`
- `protected`
- inherited from base classes
- properties with private setters
- not directly accessible from your mod assembly

Even though Rider’s decompiler lets you *see* those members, your mod code still compiles as a separate assembly. You do **not** gain same-type access just because the decompiled code is visible.

### What `Traverse` is used for
Use `Traverse` when you need to read/write:
- non-public fields
- non-public or inherited properties
- base-class members not directly exposed where you are patching

Common examples:
- `Owner`
- `Rng`
- `ModifierOptions`
- `RunState.Modifiers`
- `InitialDescription`

### Why direct access often fails
Examples of why direct access may not work:
- the member is non-public
- the member is inherited from a base class
- the type visible in decompiled code differs from what is exposed for compile-time use
- the property setter is private

### Practical pattern
```csharp
var tr = Traverse.Create(__instance);
var owner = tr.Property("Owner").GetValue<Player>();
```

### Property vs field
Decompiled STS2 code often uses PascalCase member access, but you still need to confirm whether a symbol is:
- a property (`Property(...)`)
- a field (`Field(...)`)

If you use the wrong one, reflection returns null and your patch may fail later with null errors.

### Navigating to confirm in Rider
Use:
- `Ctrl+B` for declaration
- `Ctrl+Shift+B` for type declaration
- `Ctrl+F12` for file structure
-  `Ctrl+t` and searching everything if the go to declarations are not working

Search the decompiled file and check whether the member looks like:

```csharp
public SomeType Foo { get; }
```

or:

```csharp
private SomeType foo;
```

### Important special case: private-set properties
For something like:

```csharp
public IReadOnlyList<ModifierModel> Modifiers { get; private set; }
```

The property value may be read-only to callers, but Harmony reflection can still replace the property value using `Traverse.Property(...).SetValue(...)`.

That is useful when temporarily swapping a read-only list during a synthetic vanilla call.

---

## 13. Rider / decompiler / compiler quirks to remember

### 13.1 Decompiled code is not source code you should copy blindly
Rider may show comments like:

```csharp
// ISSUE: object of a compiler-generated type is created
```

This usually means the decompiler could not reconstruct the exact original high-level syntax cleanly.

Do **not** try to instantiate compiler-generated helper types such as:
- `<>z__ReadOnlySingleElementList<T>`
- `<>z__ReadOnlyList<T>`
- `\u003C\u003Ez__ReadOnlySingleElementList<EventOption>`
Notice how sometimes it will return char codes in the types. That's normal, it justt didn't translate to <>z. 

Instead, replace them with normal C# collections unless you have a specific reason to mirror IL behavior exactly.

### 13.2 `dynamic` + extension methods can break
If you retrieve something with `dynamic` and then call an extension method on it, C# can fail with errors such as:
- extension methods cannot be dynamically dispatched

For STS2 modding, this commonly happens with methods like `UnstableShuffle`.

Fix it by:
- using a concrete type from `GetValue<T>()`
- or casting to the concrete type before the extension method call

### 13.3 `LocString` is not `string`
STS2 uses `LocString` heavily.

Do not assume:
- `Title`
- `Description`
- `InitialDescription`

are plain strings.

For logs, use:
- `GetFormattedText()` for resolved localized text
- `LocTable` and `LocEntryKey` for key-level debugging

### 13.4 Nullability warnings are often meaningful
Rider/Roslyn warnings like:
- possible null return from `NextItem<T>()`
- possible null result from reflection lookup

should be handled defensively in patches, especially when you want to fall back to vanilla behavior instead of crashing the game.

### 13.5 Decompiled symbols can drift across game versions
The decompiled method you copied last week may no longer match the current live assembly after an update.

Prefer:
- narrow postfixes
- helper calls into live vanilla methods
- reflection-driven access when necessary

over copying large branches of decompiled code verbatim.

---

## 14. Practical pattern for STS2 patching

### Prefer this order of approaches
1. **Postfix** that merges or augments results
2. **Prefix** that preserves original unless necessary
3. **Prefix returning false** only when replacement is unavoidable
4. **Transpiler** only for targeted IL-level edits or when multiple mods need to coexist cleanly on the same base logic

### Example decision rule
- If you only need to add a reward option: use a postfix.
- If you need to skip a specific condition and preserve everything else: consider a transpiler.
- If you need to rebuild an entire flow and there is no safer seam: use a full prefix replacement.

---

## 15. Build/publish workflow in Rider with the Alchyr template

### Build
Use **Build** when:
- you changed only C# logic
- you want a fast iteration loop
- you only need the `.dll`

### Publish
Use **Publish** when:
- you changed Godot resources
- you need a refreshed `.pck`
- you want the full mod package copied to the target folder

### The usual intended flow
- Rider edits code and project settings
- Build checks the `.csproj` paths and compiles the mod DLL
- Publish additionally exports the PCK via the configured Godot command
- outputs are copied into the STS2 `mods` folder

If build succeeds but the mod does not load:
- verify the output folder path in the `.csproj`
- verify the manifest file copied/exported is the correct one
- check the STS2 logs for loader errors

---

## 16. Logging and debugging reminders

### Use logs aggressively during patch development
Useful places to log:
- mod initialization
- patch entry/exit
- branch decisions
- result counts
- loc keys/titles
- fallback paths that intentionally call vanilla

### Better `LocString` logging
Prefer logging:
- formatted text for human readability
- loc table/key for stable debugging identity

### Safe fallback style
When a patch cannot confidently produce a valid result:
- prefer returning to vanilla behavior if possible
- avoid crashing the event/game
- leave a clear log line explaining the fallback

---

## 17. Recommended maintenance checklist before serious patch work

Before editing a patch-heavy STS2 mod, check:
1. Is **MegaDot/Godot 4.5.1-compatible** still the right version?
2. Is **.NET 9** still the target expected by the template/game?
3. Is `dotnet new install Alchyr.Sts2.Templates` current?
4. Are BaseLib / analyzers / other NuGet packages current?
5. Did the game update `sts2.dll` and change symbol names or signatures?
6. Are the `.csproj` paths still pointing at the correct STS2 install and Godot executable?
7. Is the current template `YourProjectName.json`?

---

## 18. Canonical references

### Setup / template
- Alchyr template setup wiki: https://github.com/Alchyr/ModTemplate-StS2/wiki/Setup

### BaseLib
- BaseLib features list: https://alchyr.github.io/BaseLib-Wiki/docs/Features.html

### Harmony docs
- Patching overview: https://harmony.pardeike.net/articles/patching.html
- Prefix docs: https://harmony.pardeike.net/articles/patching-prefix.html
- Postfix docs: https://harmony.pardeike.net/articles/patching-postfix.html
- Execution flow: https://harmony.pardeike.net/articles/execution.html
- Priorities / ordering: https://harmony.pardeike.net/articles/priorities.html

### Rider docs
- Godot support: https://www.jetbrains.com/help/rider/Godot.html
- Toolset and Build (.NET/MSBuild paths): https://www.jetbrains.com/help/rider/Settings_Toolset_and_Build.html
- Environment page: https://www.jetbrains.com/help/rider/Settings_Environment.html
- Go to Declaration: https://www.jetbrains.com/help/rider/Navigation_and_Search__Go_to_Declaration.html
- Go to Type Declaration: https://www.jetbrains.com/help/rider/Navigation_and_Search__Go_to_Type_Declaration.html
- Open projects and solutions: https://www.jetbrains.com/help/rider/Open_projects_and_solutions.html
- NuGet window: https://www.jetbrains.com/help/rider/Reference_Windows_NuGet.html
- Consume NuGet packages: https://www.jetbrains.com/help/rider/Using_NuGet.html

### Godot docs
- C# basics (solution generation): https://docs.godotengine.org/en/4.5/tutorials/scripting/c_sharp/c_sharp_basics.html

---

## 19. Short operational rules

If working on an STS2 mod in Rider:
- open the `.sln`
- verify `.NET 9`
- verify MegaDot/Godot compatibility
- verify the `.csproj` paths
- use Harmony patches, not subclass overrides, for live game behavior
- prefer postfixes where possible
- use `HarmonyPriority`, `HarmonyBefore`, and `HarmonyAfter` for known multi-mod conflicts
- use `Traverse` for non-public/inherited members
- treat decompiled code as a guide, not a perfect source of truth
- prefer calling live vanilla logic over maintaining copied branches when compatibility matters

