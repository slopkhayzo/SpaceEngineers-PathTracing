using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace SpaceEngineers.RTHWAccel
{
    internal sealed class Settings
    {
        // [General]
        public bool Enabled = true;
        public bool Diagnostics;

        // [RayTracing]
        // What the gate in front of the game's lighting does each frame
        // (research/docs/space-engineers-rt-plan.md): off, roundtrip (fence
        // hand-off only), overlay (the view through the patched sun pass where
        // the traced depth matches the game's), replace (on every surface).
        // The defaults are the release's: path-traced light on every surface.
        public GateMode GateMode = GateMode.Replace;
        // What the D3D12 view shows: lit (the traced light), or a debug view:
        // stripes, normals, basecolor, worldgrid, reprojection
        // (src/shaders/debug_view.hlsl), or primary rays through the scene
        // mirror: parts, mismatch, faces, ... (src/shaders/primary_view.hlsl).
        public DebugView DebugView = DebugView.Lit;
        public float PatternStrength = 0.5f;
        // What denoises the lit view: auto (DLSS Ray Reconstruction where available, else our filter), rr, filter.
        public Denoiser Denoiser = Denoiser.Auto;
        // Characters' glowing suit parts light their surroundings (else they only glow, as in the game).
        public bool CharacterLights = true;
        // Glowing decals (indicator lights, signs on blocks) light their surroundings (else they only glow, as in
        // the game).
        public bool DecalLights = true;
        // Lamps and emitters traced at any distance (the light grid's coarse levels beyond its fine one, filled
        // from the light tree), else 32 m around the camera, beyond which the game's own lamp light takes over.
        public bool LightTree = true;
        // Multipliers of the lamps' light (traced, and the game's own beyond the traced area) and of the light
        // emissive surfaces cast (not their own glow); 1 = as the game sets them.
        public float LampIntensity = 1f;
        public float EmissiveIntensity = 1f;
        // Mirror the game's shaders, patch Lighting/LightDir.hlsl, reload.
        public bool PatchShaders = true;
        // Walk the render scene every frame into the native BLAS/TLAS mirror.
        public bool SceneMirror = true;

        // [Debug]
        public string AutoLoadWorld = "";
        public double AutoQuitSeconds;
        public string TracePath = "";
        // Folder for the inspector's screenshots (the game's own screenshot path).
        public string CapturePath = "";
        // Once the world is ready: screenshots and timings in every gate mode.
        public bool InspectorSequence;
        // Before the inspector: the player character sits in the nearest free cockpit (views through a canopy).
        public bool SeatInCockpit;
        // After the inspector (if any): bullet holes where the player's view meets a grid, and views of them
        // (DebugAutomation.DecalSequence).
        public bool TestDecals;
        // Then, per armor skin listed (comma-separated subtypes; "block:<subtype>" for a block type): views of the
        // nearest block with it (DebugAutomation.SkinSequence).
        public string LookAtSkins = "";
        // The transparent pass's parts (glass, holograms, shields) mirrored as opaque surfaces, as before P2r (A/B).
        public bool MirrorTransparent;
        // The game's spectator camera (F8) in worlds this machine runs, also where the world does not allow it
        // (FreeCamera.cs), for screenshots.
        public bool FreeCamera;
        // Keys (Hotkeys.cs), as written and parsed (KeyCombo; 0: none, or not a key): one saves the frame without
        // the HUD as a PNG in the game folder's Screenshots, one switches the traced light off (the game's own
        // look) and on again.
        public string ScreenshotKeyName = "";
        public int ScreenshotKey;
        public string TracingToggleKeyName = "";
        public int TracingToggleKey;
        // Probe pixels for the inspector's material views, "x,y x,y ..." (up to 8).
        public int[] Probes = new int[0];

        public static Settings Load(string path)
        {
            var settings = new Settings();
            settings.Enabled = ReadBool(path, "General", "Enabled", settings.Enabled);
            settings.Diagnostics = ReadBool(path, "General", "Diagnostics", settings.Diagnostics);
            settings.GateMode = ReadGateMode(path, "RayTracing", "GateMode", settings.GateMode);
            settings.DebugView = ReadDebugView(path, "RayTracing", "DebugView", settings.DebugView);
            settings.PatternStrength = (float)ReadDouble(path, "RayTracing", "PatternStrength", settings.PatternStrength);
            settings.Denoiser = ReadDenoiser(path, "RayTracing", "Denoiser", settings.Denoiser);
            settings.CharacterLights = ReadBool(path, "RayTracing", "CharacterLights", settings.CharacterLights);
            settings.DecalLights = ReadBool(path, "RayTracing", "DecalLights", settings.DecalLights);
            settings.LightTree = ReadBool(path, "RayTracing", "LightTree", settings.LightTree);
            settings.LampIntensity = ReadIntensity(path, "LampIntensity", settings.LampIntensity);
            settings.EmissiveIntensity = ReadIntensity(path, "EmissiveIntensity", settings.EmissiveIntensity);
            settings.PatchShaders = ReadBool(path, "RayTracing", "PatchShaders", settings.PatchShaders);
            settings.SceneMirror = ReadBool(path, "RayTracing", "SceneMirror", settings.SceneMirror);
            settings.AutoLoadWorld = ReadString(path, "Debug", "AutoLoadWorld", settings.AutoLoadWorld);
            settings.AutoQuitSeconds = ReadDouble(path, "Debug", "AutoQuitSeconds", settings.AutoQuitSeconds);
            settings.TracePath = ReadString(path, "Debug", "TracePath", settings.TracePath);
            settings.CapturePath = ReadString(path, "Debug", "CapturePath", settings.CapturePath);
            settings.InspectorSequence = ReadBool(path, "Debug", "InspectorSequence", settings.InspectorSequence);
            settings.SeatInCockpit = ReadBool(path, "Debug", "SeatInCockpit", settings.SeatInCockpit);
            settings.TestDecals = ReadBool(path, "Debug", "TestDecals", settings.TestDecals);
            settings.LookAtSkins = ReadString(path, "Debug", "LookAtSkins", settings.LookAtSkins);
            settings.MirrorTransparent = ReadBool(path, "Debug", "MirrorTransparent", settings.MirrorTransparent);
            settings.FreeCamera = ReadBool(path, "Debug", "FreeCamera", settings.FreeCamera);
            settings.ScreenshotKeyName = ReadString(path, "Debug", "ScreenshotKey", "");
            settings.ScreenshotKey = KeyCombo(settings.ScreenshotKeyName);
            settings.TracingToggleKeyName = ReadString(path, "Debug", "TracingToggleKey", "");
            settings.TracingToggleKey = KeyCombo(settings.TracingToggleKeyName);
            settings.Probes = ParsePixels(ReadString(path, "Debug", "Probes", ""));
            return settings;
        }

        public override string ToString() =>
            $"GateMode={GateMode}, DebugView={DebugView}, Denoiser={Denoiser}, CharacterLights={CharacterLights}, " +
            $"DecalLights={DecalLights}, LightTree={LightTree}, " +
            $"LampIntensity={LampIntensity.ToString(CultureInfo.InvariantCulture)}, " +
            $"EmissiveIntensity={EmissiveIntensity.ToString(CultureInfo.InvariantCulture)}, " +
            $"PatternStrength={PatternStrength.ToString(CultureInfo.InvariantCulture)}, " +
            $"PatchShaders={PatchShaders}, SceneMirror={SceneMirror}";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileStringW(string section, string key, string fallback,
            StringBuilder value, int size, string path);

        private static string ReadString(string path, string section, string key, string fallback)
        {
            var value = new StringBuilder(1024);
            GetPrivateProfileStringW(section, key, fallback, value, value.Capacity, path);
            string text = value.ToString();
            int comment = text.IndexOf(';');
            if (comment >= 0)
                text = text.Substring(0, comment);
            return text.Trim();
        }

        private static bool ReadBool(string path, string section, string key, bool fallback)
        {
            switch (ReadString(path, section, key, "").ToLowerInvariant())
            {
                case "true": case "1": case "yes": case "on": return true;
                case "false": case "0": case "no": case "off": return false;
                default: return fallback;
            }
        }

        private static double ReadDouble(string path, string section, string key, double fallback) =>
            double.TryParse(ReadString(path, section, key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value : fallback;

        // A [RayTracing] light multiplier: 0 to 100 (outside: the fallback).
        private static float ReadIntensity(string path, string key, float fallback)
        {
            double value = ReadDouble(path, "RayTracing", key, fallback);
            return value >= 0 && value <= 100 ? (float)value : fallback;
        }

        private static DebugView ReadDebugView(string path, string section, string key, DebugView fallback)
        {
            switch (ReadString(path, section, key, "").ToLowerInvariant())
            {
                case "stripes": return DebugView.Stripes;
                case "normals": return DebugView.Normals;
                case "basecolor": return DebugView.BaseColor;
                case "worldgrid": return DebugView.WorldGrid;
                case "reprojection": return DebugView.Reprojection;
                case "parts": return DebugView.Parts;
                case "mismatch": return DebugView.Mismatch;
                case "faces": return DebugView.Faces;
                case "clay": return DebugView.Clay;
                case "material": return DebugView.Material;
                case "materialcompare": return DebugView.MaterialCompare;
                case "paths": return DebugView.Paths;
                case "lit": return DebugView.Lit;
                case "lit-raw": return DebugView.LitRaw;
                case "emitters": return DebugView.Emitters;
                case "motion": return DebugView.Motion;
                case "lit-rr": return DebugView.LitRR;
                case "lit-filter": return DebugView.LitFilter;
                case "emitters-reference": return DebugView.EmittersReference;
                case "emitters-power-grid": return DebugView.EmittersPowerGrid;
                default: return fallback;
            }
        }

        // The modifiers of a key combination (KeyCombo), above its virtual-key code.
        internal const int ComboCtrl = 0x100;
        internal const int ComboShift = 0x200;
        internal const int ComboAlt = 0x400;

        // "F2", "Ctrl+F2", "Ctrl+Shift+K", ...: the key's virtual-key code (VirtualKey) with the modifiers' bits;
        // 0 for anything else.
        internal static int KeyCombo(string text)
        {
            string[] parts = (text ?? "").Split('+');
            int modifiers = 0;
            for (int i = 0; i < parts.Length - 1; ++i)
            {
                switch (parts[i].Trim().ToUpperInvariant())
                {
                    case "CTRL": case "CONTROL": modifiers |= ComboCtrl; break;
                    case "SHIFT": modifiers |= ComboShift; break;
                    case "ALT": modifiers |= ComboAlt; break;
                    default: return 0;
                }
            }
            int key = VirtualKey(parts[parts.Length - 1]);
            return key == 0 ? 0 : key | modifiers;
        }

        // A key name's Windows virtual-key code: F1-F24, A-Z, 0-9, Pause, ScrollLock (any case); 0 for anything else.
        internal static int VirtualKey(string name)
        {
            string key = (name ?? "").Trim().ToUpperInvariant();
            if (key.Length == 1 && ((key[0] >= 'A' && key[0] <= 'Z') || (key[0] >= '0' && key[0] <= '9')))
                return key[0];
            if (key.Length >= 2 && key[0] == 'F' &&
                int.TryParse(key.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out int number) &&
                number >= 1 && number <= 24)
                return 0x6F + number;  // VK_F1 = 0x70
            switch (key)
            {
                case "PAUSE": return 0x13;
                case "SCROLLLOCK": return 0x91;
                default: return 0;
            }
        }

        // "x,y x,y ...": pixel pairs (x, y, x, y, ...); malformed entries are skipped.
        internal static int[] ParsePixels(string text)
        {
            var pixels = new System.Collections.Generic.List<int>();
            foreach (string entry in text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = entry.Split(',');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) &&
                    x >= 0 && y >= 0 && x < 65536 && y < 65536)
                {
                    pixels.Add(x);
                    pixels.Add(y);
                }
            }
            return pixels.ToArray();
        }

        private static Denoiser ReadDenoiser(string path, string section, string key, Denoiser fallback)
        {
            switch (ReadString(path, section, key, "").Trim().ToLowerInvariant())
            {
                case "auto": return Denoiser.Auto;
                case "rr": return Denoiser.RayReconstruction;
                case "filter": return Denoiser.Filter;
                default: return fallback;
            }
        }

        private static GateMode ReadGateMode(string path, string section, string key, GateMode fallback)
        {
            switch (ReadString(path, section, key, "").ToLowerInvariant())
            {
                case "off": return GateMode.Off;
                case "roundtrip": return GateMode.RoundTrip;
                case "overlay": return GateMode.Overlay;
                case "replace": return GateMode.Replace;
                default: return fallback;
            }
        }
    }
}
