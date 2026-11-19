using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using VRageMath;

namespace SpaceEngineers.RTHWAccel
{
    // Hooks on the lighting recording (VRage.Render11.dll), research/docs/space-engineers-rt-plan.md §3.2:
    //
    // MyGBufferResolver.DoWork records, on its own deferred context, the
    // environment-probe finish, the CSM shadow mask, SSAO/HBAO and
    // MyLightsRendering.Render (point lights, spotlights, sun + ambient) and
    // stores the command list in the static m_fc; MyRenderScheduler.Done then
    // replays it on the immediate context. The DoWork postfix hands that list
    // to the native gate, which runs the cross-device work right before its
    // replay. Before the lamp passes (MyLightsRendering.Render) the RT results
    // are bound: the patched point- and spot-light shaders leave the pixels
    // whose lamps are traced to them. Around the sun/ambient draw
    // (RenderDirectionalEnvironmentLight, recorded on the same deferred
    // context) they are bound again and the lit HDR target noted.
    //
    // The targets are large methods (542, 240 and 389 bytes of IL), which
    // keeps them out of line in the NGen image.
    internal static unsafe class LightingHooks
    {
        private static delegate*<object, int> s_doWork;
        private static delegate*<object, object, object, void> s_renderDirectional;
        private static delegate*<object, object, object, object, void> s_renderLights;
        private static FieldInfo s_finishedContextField;  // MyGBufferResolver.m_fc (struct MyFinishedContext)
        private static FieldInfo s_commandListField;      // MyFinishedContext.CommandList
        private static FieldInfo s_deviceContextField;    // MyRenderContext.m_deviceContext
        private static FieldInfo s_environmentField;      // MyRender11.Environment
        private static FieldInfo s_matricesField;         // MyEnvironment.Matrices
        private static FieldInfo s_environmentDataField;  // MyEnvironment.Data (VRageRender.MyEnvironmentData)
        private static FieldInfo s_cameraPositionField;   // MyEnvironmentMatrices.CameraPosition (Vector3D)
        private static FieldInfo[] s_matrixFields;        // ViewAt0, InvViewAt0, Projection, InvProjection, ViewProjectionAt0, InvViewProjectionAt0
        private static PropertyInfo s_resolutionProperty; // MyRender11.ResolutionI
        private static int s_errorLogged;

        public static void Install(Assembly render11)
        {
            Type resolver = GameReflection.Type(render11, "VRage.Render11.GBufferResolve.MyGBufferResolver");
            Type lights = GameReflection.Type(render11, "VRage.Render11.LightingStage.MyLightsRendering");
            Type renderContext = GameReflection.Type(render11, "VRage.Render11.RenderContext.MyRenderContext");
            Type finishedContext = GameReflection.Type(render11, "VRage.Render11.RenderContext.MyFinishedContext");
            Type cullQuery = GameReflection.Type(render11, "VRage.Render11.Culling.MyCullQuery");
            Type srvTexture = GameReflection.Type(render11, "VRage.Render11.Resources.ISrvTexture");
            Type rtvTexture = GameReflection.Type(render11, "VRage.Render11.Resources.IRtvTexture");
            Type cullResults = GameReflection.Type(render11, "VRage.Render11.Culling.MyCullResults");

            MethodInfo doWork = GameReflection.Method(resolver, "DoWork", cullQuery);
            MethodInfo directional = GameReflection.Method(lights, "RenderDirectionalEnvironmentLight",
                renderContext, srvTexture, rtvTexture);
            MethodInfo render = GameReflection.Method(lights, "Render", renderContext, cullResults, srvTexture, rtvTexture);
            s_finishedContextField = GameReflection.Field(resolver, "m_fc");
            s_commandListField = GameReflection.Field(finishedContext, "CommandList");
            s_deviceContextField = GameReflection.Field(renderContext, "m_deviceContext");
            Type render11Type = GameReflection.Type(render11, "VRageRender.MyRender11");
            Type environment = GameReflection.Type(render11, "VRageRender.MyEnvironment");
            Type matrices = GameReflection.Type(render11, "VRageRender.MyEnvironmentMatrices");
            s_environmentField = GameReflection.Field(render11Type, "Environment");
            s_matricesField = GameReflection.Field(environment, "Matrices");
            s_environmentDataField = GameReflection.Field(environment, "Data");
            s_cameraPositionField = GameReflection.Field(matrices, "CameraPosition");
            s_matrixFields = new[] { "ViewAt0", "InvViewAt0", "Projection", "InvProjection", "ViewProjectionAt0", "InvViewProjectionAt0" }
                .Select(name => GameReflection.Field(matrices, name)).ToArray();
            s_resolutionProperty = GameReflection.Property(render11Type, "ResolutionI");

            RenderThread.Run(() =>
            {
                using (var hooks = new HookTransaction())
                {
                    s_doWork = (delegate*<object, int>)hooks.Install(doWork, Own(nameof(DoWorkDetour)));
                    s_renderDirectional = (delegate*<object, object, object, void>)hooks.Install(directional,
                        Own(nameof(RenderDirectionalDetour)));
                    s_renderLights = (delegate*<object, object, object, object, void>)hooks.Install(render,
                        Own(nameof(RenderLightsDetour)));
                    hooks.Commit();
                }
                int gate = NativeApi.RtInstallGate();
                if (gate != 0)
                    throw new DetourException($"the gate on ExecuteCommandList could not be installed ({gate})");
            });
            Log.Info("Lighting hooks installed (MyGBufferResolver.DoWork, MyLightsRendering.Render, " +
                     "RenderDirectionalEnvironmentLight, gate).");
        }

        private static MethodInfo Own(string name) =>
            typeof(LightingHooks).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);

        private static void LogOnce(string what, Exception exception)
        {
            if (Interlocked.Exchange(ref s_errorLogged, 1) == 0)
                Log.Error("Lighting hook: " + what, exception);
        }

        private static IntPtr NativeContext(object renderContext)
        {
            var context = s_deviceContextField.GetValue(renderContext) as SharpDX.Direct3D11.DeviceContext;
            return context?.NativePointer ?? IntPtr.Zero;
        }

        // Recording thread (MyRenderScheduler's parallel tasks).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int DoWorkDetour(object cullQuery)
        {
            int result = s_doWork(cullQuery);
            try
            {
                object finished = s_finishedContextField.GetValue(null);
                if (s_commandListField.GetValue(finished) is SharpDX.Direct3D11.CommandList list)
                {
                    SendCamera();
                    NativeApi.RtSetLightingList(list.NativePointer);
                }
            }
            catch (Exception exception)
            {
                LogOnce("reading the lighting command list or the camera failed", exception);
            }
            return result;
        }

        // The camera this frame was drawn with: MyRender11.Environment.Matrices,
        // final once the scene is prepared (PrepareGameScene runs before the
        // parallel recording; HighFPSFix's interpolated camera is already in).
        private static unsafe void SendCamera()
        {
            object environment = s_environmentField.GetValue(null);
            object matrices = s_matricesField.GetValue(environment);
            var frame = new RtFrame { Size = (uint)sizeof(RtFrame) };
            var resolution = (Vector2I)s_resolutionProperty.GetValue(null);
            frame.Width = (uint)resolution.X;
            frame.Height = (uint)resolution.Y;
            var position = (Vector3D)s_cameraPositionField.GetValue(matrices);
            frame.CameraPosition[0] = position.X;
            frame.CameraPosition[1] = position.Y;
            frame.CameraPosition[2] = position.Z;
            Copy((Matrix)s_matrixFields[0].GetValue(matrices), frame.ViewAt0);
            Copy((Matrix)s_matrixFields[1].GetValue(matrices), frame.InvViewAt0);
            Copy((Matrix)s_matrixFields[2].GetValue(matrices), frame.Projection);
            Copy((Matrix)s_matrixFields[3].GetValue(matrices), frame.InvProjection);
            Copy((Matrix)s_matrixFields[4].GetValue(matrices), frame.ViewProjectionAt0);
            Copy((Matrix)s_matrixFields[5].GetValue(matrices), frame.InvViewProjectionAt0);
            var environmentData = (VRageRender.MyEnvironmentData)s_environmentDataField.GetValue(environment);
            VRageRender.MyEnvironmentLightData light = environmentData.EnvironmentLight;
            VRageRender.MyTextureDebugMultipliers m = environmentData.TextureMultipliers;
            frame.Multipliers[0] = m.AlbedoMultiplier;
            frame.Multipliers[1] = m.MetalnessMultiplier;
            frame.Multipliers[2] = m.GlossMultiplier;
            frame.Multipliers[3] = m.AoMultiplier;
            frame.Multipliers[4] = m.EmissiveMultiplier;
            frame.Multipliers[5] = m.ColorMaskMultiplier;
            frame.Multipliers[6] = m.AlbedoShift;
            frame.Multipliers[7] = m.MetalnessShift;
            frame.Multipliers[8] = m.GlossShift;
            frame.Multipliers[9] = m.AoShift;
            frame.Multipliers[10] = m.EmissiveShift;
            frame.Multipliers[11] = m.ColorMaskShift;
            frame.Multipliers[12] = m.ColorizeHSV.X;
            frame.Multipliers[13] = m.ColorizeHSV.Y;
            frame.Multipliers[14] = m.ColorizeHSV.Z;
            frame.Multipliers[15] = m.ColorizeHSV.W;
            frame.SunDirection[0] = light.SunLightDirection.X;
            frame.SunDirection[1] = light.SunLightDirection.Y;
            frame.SunDirection[2] = light.SunLightDirection.Z;
            frame.SunDiffuse = light.SunDiffuseFactor;
            frame.SunColor[0] = light.SunColorRaw.X;
            frame.SunColor[1] = light.SunColorRaw.Y;
            frame.SunColor[2] = light.SunColorRaw.Z;
            frame.GameFrame = GameFrame.Capture();
            frame.GameFrameSize = frame.GameFrame != IntPtr.Zero ? (uint)GameFrame.Size : 0;
            NativeApi.RtSetFrame(ref frame);
        }

        private static unsafe void Copy(Matrix m, float* d)
        {
            d[0] = m.M11; d[1] = m.M12; d[2] = m.M13; d[3] = m.M14;
            d[4] = m.M21; d[5] = m.M22; d[6] = m.M23; d[7] = m.M24;
            d[8] = m.M31; d[9] = m.M32; d[10] = m.M33; d[11] = m.M34;
            d[12] = m.M41; d[13] = m.M42; d[14] = m.M43; d[15] = m.M44;
        }

        // Recording thread, on the "MyGBufferResolver" deferred context: the lamp passes, then the sun pass.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RenderLightsDetour(object renderContext, object cullResults, object postProcessedShadows,
            object ambientOcclusion)
        {
            try
            {
                IntPtr context = NativeContext(renderContext);
                if (context != IntPtr.Zero)
                    NativeApi.RtBindLighting(context);
            }
            catch (Exception exception)
            {
                LogOnce("binding the RT results for the lamp passes failed", exception);
            }
            s_renderLights(renderContext, cullResults, postProcessedShadows, ambientOcclusion);
        }

        // Recording thread, on the "MyGBufferResolver" deferred context.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RenderDirectionalDetour(object renderContext, object postProcessedShadows, object ambientOcclusion)
        {
            IntPtr context = IntPtr.Zero;
            try
            {
                context = NativeContext(renderContext);
                if (context != IntPtr.Zero)
                    NativeApi.RtBindLighting(context);
            }
            catch (Exception exception)
            {
                LogOnce("binding the RT results failed", exception);
            }
            s_renderDirectional(renderContext, postProcessedShadows, ambientOcclusion);
            if (context != IntPtr.Zero)
                NativeApi.RtSunPassRecorded(context);
        }
    }
}
