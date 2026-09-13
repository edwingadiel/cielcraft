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

    public static bool IsAvailable => Available.Value;

    static RaphaelSolver()
    {
        NativeLibrary.SetDllImportResolver(typeof(RaphaelSolver).Assembly, ResolveLibrary);
    }

    public CraftSolution Solve(CraftSetup setup, CraftObjective objective)
    {
        if (!IsAvailable)
            return CraftSolution.Failed("native Raphael library is not available");

        var input = new RaphaelInput
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
        };

        var buffer = new uint[MaxActions];
        var result = raphael_solve(ref input, buffer, buffer.Length);

        return result switch
        {
            >= 0 => new CraftSolution(buffer[..result]),
            -2 => CraftSolution.Failed("the solver found no solution for these parameters"),
            -3 => CraftSolution.Failed("the solver panicked"),
            -4 => CraftSolution.Failed("the solution exceeded the action buffer"),
            _ => CraftSolution.Failed($"invalid solver arguments (code {result})"),
        };
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

        var directory = Path.GetDirectoryName(assembly.Location);
        if (directory == null)
            return IntPtr.Zero;

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
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int raphael_solve(ref RaphaelInput input, [Out] uint[] actions, int capacity);
}
