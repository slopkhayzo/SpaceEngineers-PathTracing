using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Sandbox;
using VRage.FileSystem;
using VRageRender;

namespace SpaceEngineers.RTHWAccel
{
    // Applies ShaderPatch's edits inside the game (research/docs/space-engineers-rt-plan.md §3.7, §5.5).
    //
    // The game compiles its HLSL at runtime from MyShaderCompiler.m_shadersPath
    // and caches the bytecode under a hash of the preprocessed text. The shader
    // tree is copied into the plugin's cache folder, the copies of the patched
    // files (Lighting/LightDir.hlsl, LightPoint.hlsl, LightSpot.hlsl) are
    // patched and compiled here first in every variant the game compiles (a
    // failed compile inside the game ends in Debugger.Break), then the game's
    // shader root points at the copy and every shader is reloaded. Unchanged
    // files still hit the game's shader cache; the game's own files stay
    // untouched and none of them is shipped with the plugin. All or none: one
    // file that cannot be patched leaves every shader as it is.
    internal static class ShaderPatcher
    {
        public static bool Apply(Assembly render11)
        {
            Type compiler = GameReflection.Type(render11, "VRageRender.MyShaderCompiler");
            FieldInfo pathField = GameReflection.Field(compiler, "m_shadersPath");
            FieldInfo includesField = GameReflection.Field(compiler, "m_includes");
            string source = pathField.GetValue(null) as string;
            if (string.IsNullOrEmpty(source))
                source = Path.Combine(MyFileSystem.ShadersBasePath, "Shaders");
            foreach (ShaderPatch.FilePatch patch in ShaderPatch.Files)
            {
                if (!File.Exists(Path.Combine(source, patch.File)))
                {
                    Log.Error($"Shader patch: {patch.File} not found under {source}; the game's shaders stay as they are.");
                    return false;
                }
            }

            string mirror = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Plugin.Name, "Shaders");
            DateTime started = DateTime.UtcNow;
            if (Directory.Exists(mirror))
                Directory.Delete(mirror, true);
            int files = ShaderPatch.CopyTree(source, mirror);

            int variants = 0;
            foreach (ShaderPatch.FilePatch patch in ShaderPatch.Files)
            {
                string path = Path.Combine(mirror, patch.File);
                string patched = ShaderPatch.ApplyEdits(File.ReadAllText(path), patch.Edits, out string error);
                if (patched == null)
                {
                    Log.Error($"Shader patch: {patch.File}: {error}; the game's shaders stay as they are.");
                    return false;
                }
                File.WriteAllText(path, patched, new UTF8Encoding(false));
            }
            foreach (ShaderPatch.FilePatch patch in ShaderPatch.Files)
            {
                foreach (ShaderPatch.Variant variant in patch.Variants)
                {
                    string message = ShaderPatch.Compile(Path.Combine(mirror, patch.File), mirror, variant);
                    if (message != null)
                    {
                        Log.Error($"Shader patch: the patched {patch.File} does not compile ({variant}): {message.Trim()}; " +
                                  "the game's shaders stay as they are.");
                        return false;
                    }
                    variants++;
                }
            }

            includesField.SetValue(null, new List<string> { mirror });
            pathField.SetValue(null, mirror);
            Log.Info($"Shader patch: {files} files mirrored from {source} to {mirror}; " +
                     $"{string.Join(", ", ShaderPatch.Files.Select(patch => patch.File))} patched and compiled in " +
                     $"{variants} variants ({(DateTime.UtcNow - started).TotalMilliseconds:F0} ms). The game's shader " +
                     "root now points at the copy; reloading every shader.");
            MySandboxGame.Static.Invoke(() => MyRenderProxy.ReloadEffects(), Plugin.Name);
            return true;
        }
    }
}
