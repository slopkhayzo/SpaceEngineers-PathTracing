using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using SpaceEngineers.RTHWAccel;

// Game-free checks against the installed game (read only): the runtime shader
// patch against its own shader sources (patched copies go to a temporary
// folder), and the scene mirror's member lookups against its assemblies.
// Run by ctest with the game's Content\Shaders folder as the argument.
internal static class ManagedTests
{
    private static int s_failures;
    private static string s_shaders;
    private static string s_bin64;

    private static int Main(string[] arguments)
    {
        if (arguments.Length != 1 || !Directory.Exists(arguments[0]))
        {
            Console.WriteLine("usage: SpaceEngineers.RTHWAccel.Tests <game Content\\Shaders folder>");
            return 2;
        }
        s_shaders = arguments[0];
        s_bin64 = Path.GetFullPath(Path.Combine(s_shaders, "..", "..", "Bin64"));
        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromGame;
        Run(nameof(PatchAnchorsMatchOnce), PatchAnchorsMatchOnce);
        Run(nameof(PatchedShadersCompileInEveryVariant), PatchedShadersCompileInEveryVariant);
        Run(nameof(UnpatchedShadersCompile), UnpatchedShadersCompile);
        Run(nameof(PatchRefusesPatchedText), PatchRefusesPatchedText);
        Run(nameof(PatchRefusesMissingAnchor), PatchRefusesMissingAnchor);
        Run(nameof(SceneMirrorMembersResolve), SceneMirrorMembersResolve);
        Run(nameof(LampMirrorMembersResolve), LampMirrorMembersResolve);
        Run(nameof(ProbePixelsParse), ProbePixelsParse);
        Run(nameof(HotkeysParse), HotkeysParse);
        Run(nameof(PhotoControlsWork), PhotoControlsWork);
        Run(nameof(GameFrameLayoutMatches), GameFrameLayoutMatches);
        Console.WriteLine(s_failures == 0 ? "all tests passed" : $"{s_failures} check(s) failed");
        return s_failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        int before = s_failures;
        try
        {
            test();
        }
        catch (Exception exception)
        {
            Check(false, $"{name} threw {exception}");
        }
        Console.WriteLine($"{(s_failures == before ? "PASS" : "FAIL")} {name}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            s_failures++;
            Console.WriteLine("  " + message);
        }
    }

    // The game's frame constants are the 1280 bytes src/shaders/game_frame.hlsli mirrors.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void GameFrameLayoutMatches()
    {
        Assembly render11 = Assembly.LoadFrom(Path.Combine(s_bin64, "VRage.Render11.dll"));
        GameFrame.Resolve(render11);
        Check(GameFrame.Size == GameFrame.ExpectedSize,
            $"MyCommon.FrameConstantsData is {GameFrame.Size} bytes, expected {GameFrame.ExpectedSize}");
    }

    // [Debug] Probes: "x,y" pairs separated by spaces; malformed entries are skipped.
    private static void ProbePixelsParse()
    {
        int[] pixels = Settings.ParsePixels(" 656,654  1012,258	7,x 1,2,3 -4,5 1690,472 ");
        Check(pixels.SequenceEqual(new[] { 656, 654, 1012, 258, 1690, 472 }),
            "parsed " + string.Join(" ", pixels));
        Check(Settings.ParsePixels("").Length == 0, "an empty value parses to no pixels");
    }

    // [Debug] ScreenshotKey, TracingToggleKey: key names to Windows virtual-key codes and combinations with
    // modifiers, 0 for anything else.
    private static void HotkeysParse()
    {
        Check(Settings.VirtualKey("F11") == 0x7A, "F11");
        Check(Settings.VirtualKey(" f1 ") == 0x70, "f1 with spaces");
        Check(Settings.VirtualKey("F24") == 0x87, "F24");
        Check(Settings.VirtualKey("k") == 'K', "k");
        Check(Settings.VirtualKey("7") == '7', "7");
        Check(Settings.VirtualKey("ScrollLock") == 0x91, "ScrollLock");
        foreach (string bad in new[] { "", "F0", "F25", "F1x", "F-1", "Ctrl", "Enter" })
            Check(Settings.VirtualKey(bad) == 0, "'" + bad + "' is not a key name");
        Check(Settings.KeyCombo("F2") == 0x71, "F2");
        Check(Settings.KeyCombo("Ctrl+F2") == (0x71 | Settings.ComboCtrl), "Ctrl+F2");
        Check(Settings.KeyCombo(" control + shift + k ") == ('K' | Settings.ComboCtrl | Settings.ComboShift),
            "control + shift + k");
        Check(Settings.KeyCombo("Alt+7") == ('7' | Settings.ComboAlt), "Alt+7");
        foreach (string bad in new[] { "", "Ctrl+", "Win+F2", "F2+Ctrl", "Ctrl+Enter", "+F2" })
            Check(Settings.KeyCombo(bad) == 0, "'" + bad + "' is not a key combination");
    }

    // [Debug] FreeCamera's controls: Ctrl down, Shift/Alt speed, a roll independent of the speed, the wheel's zoom.
    private static void PhotoControlsWork()
    {
        Check(PhotoControls.Move(new VRageMath.Vector3(0, 0, -1), true, 1) == new VRageMath.Vector3(0, -1, -1), "Ctrl adds down");
        Check(PhotoControls.Move(new VRageMath.Vector3(0, 1, 0), true, 1) == VRageMath.Vector3.Zero, "Space and Ctrl cancel");
        Check(PhotoControls.Move(new VRageMath.Vector3(0, -1, 0), true, 1) == new VRageMath.Vector3(0, -1, 0), "C and Ctrl stay one down");
        Check(PhotoControls.Move(new VRageMath.Vector3(1, 0, 0), false, PhotoControls.SpeedFactor(true, false)) ==
            new VRageMath.Vector3(5, 0, 0), "Shift 5x");
        Check(Math.Abs(PhotoControls.SpeedFactor(false, true) - 0.2f) < 1e-6f, "Alt 5x slower");
        Check(Math.Abs(PhotoControls.SpeedFactor(true, true) - 1f) < 1e-6f, "Shift and Alt cancel");
        // The spectator rolls by input x speed x 0.1, at most 0.02 a step: the input given makes RollPerStep at any speed.
        foreach (float speed in new[] { 0.001f, 0.1f, 10f })
        {
            float rolled = Math.Max(-0.02f, Math.Min(0.02f, PhotoControls.Roll(-1, speed, 1) * speed * 0.1f));
            Check(Math.Abs(rolled + PhotoControls.RollPerStep) < 1e-6f, $"roll at speed {speed}: {rolled}");
        }
        Check(PhotoControls.Roll(0, 0.1f, 5) == 0, "no roll without Q/E");
        Check(PhotoControls.Roll(1, 0.1f, 5) * 0.1f * 0.1f <= 0.02f + 1e-6f, "Shift's roll stays within the spectator's limit");
        const float fov = 1f;
        Check(PhotoControls.Fov(fov, 120) < fov && PhotoControls.Fov(fov, -120) > fov, "the wheel forward zooms in");
        Check(Math.Abs(PhotoControls.Fov(PhotoControls.Fov(fov, 240), -240) - fov) < 1e-5f, "zooming back returns");
        Check(PhotoControls.Fov(fov, 120 * 100) == PhotoControls.MinFov && PhotoControls.Fov(fov, -120 * 100) == PhotoControls.MaxFov,
            "the zoom stays within 5 and 120 degrees");
    }

    private static string Original(ShaderPatch.FilePatch patch) => File.ReadAllText(Path.Combine(s_shaders, patch.File));

    private static string OriginalLightDir() => Original(ShaderPatch.LightDir);

    // The game's assemblies come from its Bin64 folder (loaded for reflection
    // only: no game code runs).
    private static Assembly ResolveFromGame(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        Assembly loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
        if (loaded != null)
            return loaded;
        string path = Path.Combine(s_bin64, name + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }

    // Every non-public VRage.Render11 member the scene mirror compiles an
    // accessor for exists with the shape the expressions expect, and every
    // detour's signature passes the hook check.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SceneMirrorMembersResolve()
    {
        Assembly render11 = Assembly.LoadFrom(Path.Combine(s_bin64, "VRage.Render11.dll"));
        SceneMirror.Resolve(render11);
        foreach ((MethodInfo target, MethodInfo detour) in SceneMirror.Hooks(render11))
            Detour.CheckSignatures(target, detour);
    }

    private static void PatchAnchorsMatchOnce()
    {
        foreach (ShaderPatch.FilePatch patch in ShaderPatch.Files)
        {
            string original = Original(patch);
            string patched = ShaderPatch.ApplyEdits(original, patch.Edits, out string error);
            Check(patched != null, $"{patch.File}: patch refused: {error}");
            if (patched == null)
                continue;
            Check(patched.Contains("register(t22)"), $"{patch.File}: the overlay declaration is missing");
            Check(patched.IndexOf("RtOverlay[", StringComparison.Ordinal) > patched.IndexOf("register(t22)", StringComparison.Ordinal),
                $"{patch.File}: the overlay is used before its declaration");
            Check(patched.Length > original.Length, $"{patch.File}: nothing was inserted");
            int declared = patched.IndexOf("float RtLampShare(", StringComparison.Ordinal);
            int used = patched.IndexOf("*= RtLampShare(", StringComparison.Ordinal);
            Check((declared < 0) == (used < 0) && used >= declared,
                $"{patch.File}: the lamp share is used before its declaration");
            // The lamp share reads its scale (t23, bound with the overlay) where it is declared, nowhere else.
            int scale = patched.IndexOf("Texture2D<float2> RtLampScale : register(t23);", StringComparison.Ordinal);
            Check((declared >= 0) == (scale >= 0) && scale <= declared,
                $"{patch.File}: the lamp scale is not declared with (and before) the lamp share");
        }
    }

    // The same check the plugin runs in the game before it switches the shader root.
    private static void PatchedShadersCompileInEveryVariant()
    {
        string mirror = Path.Combine(Path.GetTempPath(), "SpaceEngineers.RTHWAccel.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            ShaderPatch.CopyTree(s_shaders, mirror);
            foreach (ShaderPatch.FilePatch patch in ShaderPatch.Files)
            {
                string path = Path.Combine(mirror, patch.File);
                string patched = ShaderPatch.ApplyEdits(File.ReadAllText(path), patch.Edits, out string error);
                Check(patched != null, $"{patch.File}: patch refused: {error}");
                if (patched != null)
                    File.WriteAllText(path, patched);  // as the plugin writes it
            }
            foreach (ShaderPatch.FilePatch patch in ShaderPatch.Files)
            {
                foreach (ShaderPatch.Variant variant in patch.Variants)
                {
                    string message = ShaderPatch.Compile(Path.Combine(mirror, patch.File), mirror, variant);
                    Check(message == null, $"{patch.File} ({variant}) does not compile: {message}");
                }
            }
        }
        finally
        {
            if (Directory.Exists(mirror))
                Directory.Delete(mirror, true);
        }
    }

    // Guards the compile check itself: the game's own files must pass it.
    private static void UnpatchedShadersCompile()
    {
        foreach (ShaderPatch.FilePatch patch in ShaderPatch.Files)
        {
            foreach (ShaderPatch.Variant variant in patch.Variants)
            {
                string message = ShaderPatch.Compile(Path.Combine(s_shaders, patch.File), s_shaders, variant);
                Check(message == null, $"the game's own {patch.File} ({variant}) fails the compile check: {message}");
            }
        }
    }

    private static void PatchRefusesPatchedText()
    {
        foreach (ShaderPatch.FilePatch patch in ShaderPatch.Files)
        {
            string patched = ShaderPatch.ApplyEdits(Original(patch), patch.Edits, out _);
            Check(ShaderPatch.ApplyEdits(patched ?? "", patch.Edits, out string error) == null,
                $"{patch.File}: a second application was accepted");
            Check(error != null && error.Contains("already patched"), $"{patch.File}: unexpected reason: {error}");
        }
    }

    private static void PatchRefusesMissingAnchor()
    {
        string withoutFog = OriginalLightDir().Replace("output = Fog(shaded, input.depth);", "output = shaded;");
        Check(ShaderPatch.ApplyEdits(withoutFog, ShaderPatch.LightDir.Edits, out string error) == null,
            "a source without the anchor was patched");
        Check(error != null && error.Contains("found 0 times"), "unexpected reason: " + error);
    }

    // The lamp mirror's member lookups.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LampMirrorMembersResolve()
    {
        Assembly render11 = Assembly.LoadFrom(Path.Combine(s_bin64, "VRage.Render11.dll"));
        LampMirror.Resolve(render11);
    }
}
