using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace NeowAlwaysRewards;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string
        ModId = "NeowAlwaysRewards"; //At the moment, this is used only for the Logger and harmony names.

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        
        GD.Print("[NeowAlwaysRewards] Initialize called");
        Logger.Info("NeowAlwaysRewards Initialize called");
        
        Harmony harmony = new(ModId);
        harmony.PatchAll();
        
        GD.Print("[NeowAlwaysRewards] Harmony patches applied");
        Logger.Info("NeowAlwaysRewards Harmony patches applied");
    }
}