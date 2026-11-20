using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Godot;
using Ymir.Box3D;

namespace BadDay.Client;

/// <summary>
/// Finds ymir_box3d for Ymir.Box3D's DllImports. Godot loads game assemblies into its own load context, so the
/// default probing does not look beside them. A run from the editor or <c>--path</c> loads the library from the
/// project's build directory; an exported run from the data folder Godot puts beside the executable.
/// </summary>
internal static class NativeLibraries
{
    private const string Library = "ymir_box3d";

#pragma warning disable CA2255 // application code: the resolver must exist before the first DllImport
    [ModuleInitializer]
    internal static void Register() =>
        NativeLibrary.SetDllImportResolver(typeof(Box3DSoaWorld).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return IntPtr.Zero;
        string dir = OS.HasFeature("template")
            ? Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath())!, $"data_BadDay.Client_{Platform()}_{Arch()}")
            : ProjectSettings.GlobalizePath("res://.godot/mono/temp/bin/Debug");
        string path = Path.Combine(dir, FileName());
        return File.Exists(path) && NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;
    }

    private static string FileName() =>
        OperatingSystem.IsWindows() ? "ymir_box3d.dll" : OperatingSystem.IsMacOS() ? "libymir_box3d.dylib" : "libymir_box3d.so";

    private static string Platform() =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linuxbsd";

    private static string Arch() =>
        RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x86_64";
}
