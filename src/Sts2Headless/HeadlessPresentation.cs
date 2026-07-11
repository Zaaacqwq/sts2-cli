using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Audio.Debug;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Vfx.Utilities;

namespace Sts2Headless;

/// <summary>
/// Removes presentation-only work without changing the global NGame singleton.
/// Keeping NGame.Instance null is load-bearing: many headless-safe game paths use
/// that null to skip scene-tree access.
/// </summary>
internal static class HeadlessPresentation
{
    private static NDebugAudioManager? _audio;

    public static void Install()
    {
        var assembly = typeof(MegaCrit.Sts2.Core.Commands.CardPileCmd).Assembly;
        var audioType = Require(
            assembly.GetType("MegaCrit.Sts2.Core.Audio.Debug.NDebugAudioManager"),
            "NDebugAudioManager");
        var commandType = Require(
            assembly.GetType("MegaCrit.Sts2.Core.Commands.Cmd"),
            "Cmd");
        var denseType = Require(
            assembly.GetType("MegaCrit.Sts2.Core.Models.Events.DenseVegetation"),
            "DenseVegetation");

        _audio = (NDebugAudioManager)RuntimeHelpers.GetUninitializedObject(audioType);
        var harmony = new Harmony("sts2headless.presentation-safe");
        harmony.Patch(
            PropertyGetter(audioType, "Instance"),
            Prefix(nameof(AudioInstancePrefix)));
        harmony.Patch(Method(audioType, "Play"), Prefix(nameof(PlayPrefix)));
        harmony.Patch(Method(audioType, "_Ready"), Prefix(nameof(SkipPrefix)));
        harmony.Patch(Method(audioType, "Stop"), Prefix(nameof(SkipPrefix)));
        harmony.Patch(Method(audioType, "StopAll"), Prefix(nameof(SkipPrefix)));
        harmony.Patch(Method(audioType, "SetMasterAudioVolume"), Prefix(nameof(SkipPrefix)));
        harmony.Patch(Method(audioType, "SetSfxAudioVolume"), Prefix(nameof(SkipPrefix)));
        harmony.Patch(
            Method(commandType, "CustomScaledWait"),
            Prefix(nameof(CompletedTaskPrefix)));

        // DenseVegetation.Rest directly dereferences NGame.Instance only to rumble
        // the screen. Replace that one call inside its async state machine; never
        // make the global singleton non-null.
        var rest = Method(denseType, "Rest");
        var stateMachine = rest.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException("DenseVegetation.Rest has no async state machine");
        var moveNext = stateMachine.GetMethod(
            "MoveNext",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("DenseVegetation.Rest.MoveNext not found");
        harmony.Patch(moveNext, transpiler: Prefix(nameof(DenseRestTranspiler)));

        Console.Error.WriteLine("[INFO] Installed headless-safe audio/wait/rumble presentation patches");
    }

    private static IEnumerable<CodeInstruction> DenseRestTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        var screenRumble = typeof(NGame).GetMethod(
            "ScreenRumble",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NGame.ScreenRumble not found");
        var replacement = typeof(HeadlessPresentation).GetMethod(
            nameof(IgnoreScreenRumble),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(screenRumble))
            {
                replaced++;
                yield return new CodeInstruction(OpCodes.Call, replacement)
                    .WithLabels(instruction.labels)
                    .WithBlocks(instruction.blocks);
            }
            else
            {
                yield return instruction;
            }
        }
        if (replaced != 1)
            throw new InvalidOperationException(
                $"expected one DenseVegetation.Rest ScreenRumble call, found {replaced}");
    }

    private static void IgnoreScreenRumble(
        NGame? game,
        ShakeStrength strength,
        ShakeDuration duration,
        RumbleStyle style)
    {
    }

    private static bool AudioInstancePrefix(ref NDebugAudioManager __result)
    {
        __result = _audio!;
        return false;
    }

    private static bool PlayPrefix(ref int __result)
    {
        __result = 0;
        return false;
    }

    private static bool SkipPrefix() => false;

    private static bool CompletedTaskPrefix(ref Task __result)
    {
        __result = Task.CompletedTask;
        return false;
    }

    private static Type Require(Type? type, string name) =>
        type ?? throw new InvalidOperationException($"type not found: {name}");

    private static MethodInfo PropertyGetter(Type type, string property) =>
        type.GetProperty(property, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetMethod
        ?? throw new InvalidOperationException($"{type.Name}.{property} getter not found");

    private static MethodInfo Method(Type type, string name)
    {
        var methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == name)
            .ToList();
        if (methods.Count != 1)
            throw new InvalidOperationException(
                $"expected exactly one {type.Name}.{name}, found {methods.Count}");
        return methods[0];
    }

    private static HarmonyMethod Prefix(string name) =>
        new(typeof(HeadlessPresentation).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static)!);
}
