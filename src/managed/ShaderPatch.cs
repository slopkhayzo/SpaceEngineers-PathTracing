using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;

namespace SpaceEngineers.RTHWAccel
{
    // The text side of the runtime shader patches (ShaderPatcher.cs applies
    // them inside the game): anchored edits of the game's own sources, and a
    // compile check like the game's. No game types, so the tests use it too.
    internal static class ShaderPatch
    {
        public const string Marker = "SpaceEngineers.RTHWAccel";

        public sealed class Edit
        {
            public string Anchor;  // a fragment of exactly one line of the original
            public bool Before;    // insert before (true) or after (false) that line
            public string[] Lines;
        }

        // A compile the game makes of a file: its entry point, profile and defines.
        public sealed class Variant
        {
            public string Entry = "__pixel_shader";
            public string Profile = "ps_5_0";
            public string[] Defines = new string[0];

            public override string ToString() => $"{Entry} {Profile} [{string.Join(" ", Defines)}]";
        }

        public sealed class FilePatch
        {
            public string File;  // relative to the shader root
            public Edit[] Edits;
            public Variant[] Variants;  // every variant the game compiles of it (MyLightsRendering.Init)
        }

        // The RT overlay texture (t22; bound by the plugin, an unbound slot reads
        // zero) blended over the lit surface by its alpha: 1 replaces the game's
        // light with the traced one (P2), the game's fog, sky and post-processing
        // stay; debug views use a partial alpha.
        public static readonly FilePatch LightDir = new FilePatch
        {
            File = @"Lighting\LightDir.hlsl",
            Edits = new[]
            {
                new Edit
                {
                    Anchor = "Texture2D<float> ShadowsMainView",
                    Before = false,
                    Lines = new[]
                    {
                        "// " + Marker + ": ray-traced lighting, bound by the plugin (an unbound slot reads zero).",
                        "Texture2D<float4> RtOverlay : register(t22);",
                    },
                },
                new Edit
                {
                    Anchor = "output = Fog(shaded, input.depth);",
                    Before = true,
                    Lines = new[]
                    {
                        "        shaded = lerp(shaded, RtOverlay[vertex.position.xy].rgb, RtOverlay[vertex.position.xy].a); // " + Marker,
                    },
                },
            },
            Variants = new[]
            {
                new Variant(),
                new Variant { Defines = new[] { "SAMPLE_FREQ_PASS" } },
                new Variant { Defines = new[] { "NO_SHADOWS" } },
            },
        };

        // The lamp passes (tiled point lights, spot lights) leave the pixels whose lamps the traced light
        // covers: where the overlay is opaque and the surface lies inside the light grid, where the traced
        // light samples the lamps (src/shaders/lights.hlsli InLightGrid: 32 cells of 2 m across, around the
        // camera; keep the two alike). The light they add elsewhere is scaled as the traced lamps' is: the
        // LampIntensity setting, in t23 (x; y = 1), which the plugin binds with the overlay while the traced
        // light is on (rt_core.cpp BindLighting). Unbound, as with the gate off, it reads zero: the game's own
        // lamp light, unscaled, so that switching the traced light off restores the game's look.
        private static readonly string[] LampShare =
        {
            "// " + Marker + ": ray-traced lighting, bound by the plugin (an unbound slot reads zero).",
            "Texture2D<float4> RtOverlay : register(t22);",
            "// " + Marker + ": x the LampIntensity setting, y 1; bound with the overlay while the traced light is on.",
            "Texture2D<float2> RtLampScale : register(t23);",
            "// " + Marker + ": the share of the lamp's light this pass adds: none where the traced light covers",
            "// the pixel and traces the lamps (inside its light grid, camera-relative), the rest scaled as the",
            "// traced lamps are; all of it, as the game sets it, while the traced light is off.",
            "float RtLampShare(float2 pixel, float3 position)",
            "{",
            "    float2 scale = RtLampScale[uint2(0, 0)];",
            "    if (scale.y == 0)",
            "        return 1;",
            "    float3 cell = floor(position / 2.0) + 16;",
            "    bool inGrid = all(cell >= 0) && all(cell < 32);",
            "    return (inGrid ? 1 - RtOverlay[uint2(pixel)].a : 1) * scale.x;",
            "}",
        };

        public static readonly FilePatch LightPoint = new FilePatch
        {
            File = @"Lighting\LightPoint.hlsl",
            Edits = new[]
            {
                new Edit { Anchor = "StructuredBuffer<uint> TileIndices : register(t14);", Before = false, Lines = LampShare },
                new Edit
                {
                    Anchor = "output = acc;",
                    Before = true,
                    Lines = new[] { "\tacc *= RtLampShare(vertex.position.xy, input.position); // " + Marker },
                },
            },
            Variants = new[]
            {
                new Variant(),
                new Variant { Defines = new[] { "SAMPLE_FREQ_PASS" } },
            },
        };

        public static readonly FilePatch LightSpot = new FilePatch
        {
            File = @"Lighting\LightSpot.hlsl",
            Edits = new[]
            {
                new Edit { Anchor = "Texture2D<float> SpotlightShadowmap : register(t14);", Before = false, Lines = LampShare },
                new Edit
                {
                    Anchor = "output = light_factor * CalculateLight(",
                    Before = false,
                    Lines = new[] { "    output *= RtLampShare(svPos.xy, surface.position); // " + Marker },
                },
            },
            Variants = new[]
            {
                new Variant { Entry = "__vertex_shader", Profile = "vs_5_0" },
                new Variant(),
                new Variant { Defines = new[] { "SHADOWS" } },
            },
        };

        // All or none: the traced lamps need the lamp passes patched, the lamp passes the sun pass.
        public static readonly FilePatch[] Files = { LightDir, LightPoint, LightSpot };

        // The patched text, or null and the reason.
        public static string ApplyEdits(string source, IReadOnlyList<Edit> edits, out string error)
        {
            if (source.Contains(Marker))
            {
                error = "the source is already patched";
                return null;
            }
            string newline = source.Contains("\r\n") ? "\r\n" : "\n";
            var lines = new List<string>(source.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
            foreach (Edit edit in edits)
            {
                int[] matches = lines.Select((line, index) => line.Contains(edit.Anchor) ? index : -1)
                    .Where(index => index >= 0).ToArray();
                if (matches.Length != 1)
                {
                    error = $"anchor \"{edit.Anchor}\" found {matches.Length} times (expected once)";
                    return null;
                }
                lines.InsertRange(edit.Before ? matches[0] : matches[0] + 1, edit.Lines);
            }
            error = null;
            return string.Join(newline, lines);
        }

        // Compiles one variant the way the game does (includes from `root`); null when it compiles.
        public static string Compile(string file, string root, Variant variant)
        {
            ShaderMacro[] macros = variant.Defines.Select(name => new ShaderMacro(name, "1")).ToArray();
            try
            {
                using (var include = new IncludeHandler(root, Path.GetDirectoryName(file)))
                using (CompilationResult result = ShaderBytecode.CompileFromFile(file, variant.Entry, variant.Profile,
                           ShaderFlags.None, EffectFlags.None, macros, include))
                {
                    return result.HasErrors || result.Bytecode == null ? result.Message ?? "no bytecode" : null;
                }
            }
            catch (CompilationException exception)
            {
                return exception.Message;
            }
        }

        public static int CopyTree(string source, string target)
        {
            int count = 0;
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
                count++;
            }
            foreach (string directory in Directory.GetDirectories(source))
                count += CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
            return count;
        }

        // Resolves includes like the game's MyIncludeProcessor: "local" ones
        // next to the including file (the main file's folder at the top level),
        // <system> ones from the shader root.
        private sealed class IncludeHandler : SharpDX.CallbackBase, Include
        {
            private readonly string m_root;
            private readonly string m_mainDirectory;

            public IncludeHandler(string root, string mainDirectory)
            {
                m_root = root;
                m_mainDirectory = mainDirectory;
            }

            public Stream Open(IncludeType type, string fileName, Stream parentStream)
            {
                string directory = type == IncludeType.Local
                    ? (parentStream as FileStream)?.Name is string parent ? Path.GetDirectoryName(parent) : m_mainDirectory
                    : m_root;
                string path = Path.Combine(directory, fileName);
                if (!File.Exists(path))
                    path = Path.Combine(m_root, fileName);
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            public void Close(Stream stream) => stream.Dispose();
        }
    }
}
