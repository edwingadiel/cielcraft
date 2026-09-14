using System.Reflection;
using System.Runtime.InteropServices;
using CielCraft.Core;

namespace CielCraft.Raphael;

/// <summary>
/// ICraftSolver backed by the native cielcraft-raphael library (raphael-rs).
/// The native library ships next to this assembly; when it is missing,
/// <see cref="IsAvailable"/> is false and Solve returns a failed solution.
/// </summary>
public sealed class RaphaelSolver : ICraftSolver
{
    private const string LibraryName = "cielcraft_raphael";
    private const int MaxActions = 64;

    private static readonly Lazy<bool> Available = new(ProbeNativeLibrary);

    /// <summary>
    /// Directory to probe first for the native library. Dalamud loads plugin assemblies from memory,
    /// so <see cref="Assembly.Location"/> is empty in game; the plugin sets this from
    /// <c>IDalamudPluginInterface.AssemblyLocation</c> before the solver is first used.
    /// </summary>
    public static string? LibraryDirectory { get; set; }

    public static bool IsAvailable => Available.Value;

    static RaphaelSolver()
    {
        NativeLibrary.SetDllImportResolver(typeof(RaphaelSolver).Assembly, ResolveLibrary);
    }

    public CraftSolution Solve(CraftSetup setup, CraftObjective objective)
    {
        if (!IsAvailable)
            return CraftSolution.Failed("native Raphael library is not available");

        var input = BuildInput(setup, objective);
        var buffer = new uint[MaxActions];
        var result = raphael_solve(ref input, buffer, buffer.Length);
        return ToSolution(result, buffer, input);
    }

    private static RaphaelInput BuildInput(CraftSetup setup, CraftObjective objective) => new()
    {
        RecipeLevel = setup.RecipeLevel,
        MaxProgress = setup.MaxProgress,
        MaxQuality = setup.MaxQuality,
        MaxDurability = setup.MaxDurability,
        Craftsmanship = setup.Craftsmanship,
        Control = setup.Control,
        Cp = setup.Cp,
        TargetQuality = objective.TargetQuality,
        InitialQuality = objective.InitialQuality,
        Level = setup.Level,
        IsExpert = ToByte(setup.IsExpert),
        Manipulation = ToByte(setup.Manipulation),
        HeartAndSoul = ToByte(setup.HeartAndSoul),
        QuickInnovation = ToByte(setup.QuickInnovation),
        Adversarial = ToByte(objective.Adversarial),
        BackloadProgress = ToByte(objective.BackloadProgress),
        ExcludeFirstStepActions = ToByte(objective.ExcludeFirstStepActions),
        ExcludePrudent = ToByte(objective.ExcludePrudent),
    };

    private static CraftSolution ToSolution(int result, uint[] buffer, RaphaelInput input)
    {
        ushort baseProgress = 0, baseQuality = 0;
        if (result >= 0)
            raphael_base_values(ref input, ref baseProgress, ref baseQuality);

        return result switch
        {
            >= 0 => new CraftSolution(buffer[..result], BaseProgress: baseProgress, BaseQuality: baseQuality),
            -2 => CraftSolution.Failed("the solver found no solution for these parameters"),
            -3 => CraftSolution.Failed("the solver panicked"),
            -4 => CraftSolution.Failed("the solution exceeded the action buffer"),
            -5 => CraftSolution.Failed("the craft is already finished (no durability or progress complete)"),
            _ => CraftSolution.Failed($"invalid solver arguments (code {result})"),
        };
    }

    public CraftSolution SolveFromState(CraftSetup setup, CraftSnapshot live, int targetQuality, CraftSolveContext context)
    {
        if (!IsAvailable)
            return CraftSolution.Failed("native Raphael library is not available");

        var effects = CraftLiveEffects.FromSnapshot(live, setup, context);
        var target = (ushort)Math.Clamp(targetQuality, 0, setup.MaxQuality);

        var input = BuildInput(setup, new CraftObjective(
            TargetQuality: target,
            // Past step 1 the first-step-only actions are excluded up front
            // (the combo state would reject them anyway).
            ExcludeFirstStepActions: live.Step > 1));

        var state = new RaphaelLiveState
        {
            Progress = (ushort)Math.Clamp(live.Progress, 0, ushort.MaxValue),
            Quality = (ushort)Math.Clamp(live.Quality, 0, ushort.MaxValue),
            Durability = (ushort)Math.Clamp(live.Durability, 0, ushort.MaxValue),
            Cp = (ushort)Math.Min(live.CurrentCp, ushort.MaxValue),
            InnerQuiet = (byte)effects.InnerQuiet,
            WasteNot = (byte)effects.WasteNot,
            Innovation = (byte)effects.Innovation,
            Veneration = (byte)effects.Veneration,
            GreatStrides = (byte)effects.GreatStrides,
            MuscleMemory = (byte)effects.MuscleMemory,
            Manipulation = (byte)effects.Manipulation,
            TrainedPerfectionAvailable = ToByte(effects.TrainedPerfectionAvailable),
            HeartAndSoulAvailable = ToByte(effects.HeartAndSoulAvailable),
            QuickInnovationAvailable = ToByte(effects.QuickInnovationAvailable),
            TrainedPerfectionActive = ToByte(effects.TrainedPerfectionActive),
            HeartAndSoulActive = ToByte(effects.HeartAndSoulActive),
            Combo = (byte)effects.Combo,
        };

        var buffer = new uint[MaxActions];
        var result = raphael_solve_from_state(ref input, ref state, buffer, buffer.Length);
        return ToSolution(result, buffer, input);
    }

    private static byte ToByte(bool value) => value ? (byte)1 : (byte)0;

    private static bool ProbeNativeLibrary()
    {
        var handle = ResolveLibrary(LibraryName, typeof(RaphaelSolver).Assembly, null);
        if (handle == IntPtr.Zero)
            return NativeLibrary.TryLoad(LibraryName, out _);

        return true;
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName)
            return IntPtr.Zero;

        var candidates = new[]
        {
            LibraryDirectory,
            string.IsNullOrEmpty(assembly.Location) ? null : Path.GetDirectoryName(assembly.Location),
            AppContext.BaseDirectory,
        };

        foreach (var directory in candidates)
        {
            if (string.IsNullOrEmpty(directory))
                continue;

            foreach (var fileName in new[]
                     {
                         "cielcraft_raphael.dll",
                         "libcielcraft_raphael.dylib",
                         "libcielcraft_raphael.so",
                     })
            {
                var path = Path.Combine(directory, fileName);
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                    return handle;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>Must match the #[repr(C)] RaphaelInput struct in native/cielcraft-raphael/src/lib.rs.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RaphaelInput
    {
        public ushort RecipeLevel;
        public ushort MaxProgress;
        public ushort MaxQuality;
        public ushort MaxDurability;
        public ushort Craftsmanship;
        public ushort Control;
        public ushort Cp;
        public ushort TargetQuality;
        public ushort InitialQuality;
        public byte Level;
        public byte IsExpert;
        public byte Manipulation;
        public byte HeartAndSoul;
        public byte QuickInnovation;
        public byte Adversarial;
        public byte BackloadProgress;
        public byte ExcludeFirstStepActions;
        public byte ExcludePrudent;
    }

    /// <summary>Must match the #[repr(C)] RaphaelLiveState struct in native/cielcraft-raphael/src/lib.rs.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RaphaelLiveState
    {
        public ushort Progress;
        public ushort Quality;
        public ushort Durability;
        public ushort Cp;
        public byte InnerQuiet;
        public byte WasteNot;
        public byte Innovation;
        public byte Veneration;
        public byte GreatStrides;
        public byte MuscleMemory;
        public byte Manipulation;
        public byte TrainedPerfectionAvailable;
        public byte HeartAndSoulAvailable;
        public byte QuickInnovationAvailable;
        public byte TrainedPerfectionActive;
        public byte HeartAndSoulActive;
        public byte Combo;
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int raphael_solve(ref RaphaelInput input, [Out] uint[] actions, int capacity);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int raphael_solve_from_state(ref RaphaelInput input, ref RaphaelLiveState live, [Out] uint[] actions, int capacity);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int raphael_base_values(ref RaphaelInput input, ref ushort baseProgress, ref ushort baseQuality);
}
