using System;
using System.Reflection;
using System.Text;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineers.RTHWAccel
{
    // The plugin's settings in the game's menu, with its video settings: a "Path tracing" button in the game's graphics
    // options (the main menu's and the Esc menu's alike, SpaceEngineers.Game.GUI.MyGuiScreenOptionsGraphics) opens
    // PathTracingScreen. The detour on their RecreateControls adds it after the game laid them out, in the free right
    // half of their last row (Enable PostProcessing; the row above has Ambient occlusion and Enable damage effects
    // side by side), right-aligned with their drop-downs: the screen keeps its size (0.98 of the screen's height).
    internal static unsafe class OptionsMenu
    {
        private static delegate*<object, bool, void> s_recreateControls;
        private static Settings s_settings;
        private static bool s_failureLogged;

        public static void Install(Settings settings)
        {
            s_settings = settings;
            Type graphics = typeof(SpaceEngineers.Game.GUI.MyGuiScreenOptionsGraphics);
            MethodInfo recreate = graphics.GetMethod("RecreateControls", BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(bool) }, null);
            // Its own override only: the base class's would put the button on every screen.
            if (recreate == null || recreate.DeclaringType != graphics)
                throw new DetourException("the game's graphics options have no RecreateControls of their own " +
                                          "(MyGuiScreenOptionsGraphics)");
            using (var hooks = new HookTransaction())
            {
                s_recreateControls = (delegate*<object, bool, void>)hooks.Install(recreate,
                    typeof(OptionsMenu).GetMethod(nameof(RecreateControlsDetour), BindingFlags.NonPublic | BindingFlags.Static));
                hooks.Commit();
            }
            Log.Info("Options screen: a \"Path tracing\" button in the game's graphics options opens the plugin's " +
                     "settings.");
        }

        // The game's graphics options, for the automated check.
        internal static MyGuiScreenBase CreateGraphicsOptionsScreen() =>
            (MyGuiScreenBase)Activator.CreateInstance(typeof(SpaceEngineers.Game.GUI.MyGuiScreenOptionsGraphics), true);

        // Game thread (the GUI's).
        private static void RecreateControlsDetour(object self, bool constructor)
        {
            var screen = self as MyGuiScreenBase;
            s_recreateControls(self, constructor);
            if (screen == null)
                return;
            try
            {
                AddButton(screen);
            }
            catch (Exception exception)
            {
                Failed(exception);
            }
        }

        // In the last row's free right half: level with its checkbox, right-aligned with the drop-downs above.
        private static void AddButton(MyGuiScreenBase screen)
        {
            MyGuiControlBase row = null;     // Enable PostProcessing's checkbox
            MyGuiControlBase column = null;  // a drop-down: the right edge
            foreach (MyGuiControlBase control in screen.Controls)
            {
                if (control.Name == "PostProcessing")
                    row = control;
                else if (control.Name == "AnisotropicFiltering")
                    column = control;
            }
            if (row == null || column == null)
            {
                Failed(new InvalidOperationException("the game's graphics options have no PostProcessing checkbox or " +
                                                     "AnisotropicFiltering drop-down"));
                return;
            }
            var open = new MyGuiControlButton(position: new Vector2(column.Position.X, row.Position.Y),
                visualStyle: MyGuiControlButtonStyleEnum.Default,
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_TOP, text: new StringBuilder("Path tracing"),
                textScale: 0.8f, textAlignment: MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                toolTip: "The plugin's settings: lighting, denoiser, DLSS mode, DLSS Frame Generation, lamps.",
                onButtonClick: _ => MyGuiSandbox.AddScreen(new PathTracingScreen(s_settings)));
            open.Name = "PathTracing";
            screen.Controls.Add(open);
        }

        private static void Failed(Exception exception)
        {
            if (s_failureLogged)
                return;
            s_failureLogged = true;
            Log.Error("The \"Path tracing\" button could not be added to the game's graphics options", exception);
        }
    }

    // The plugin's settings screen: the settings that change while playing, applied at once and written to the INI.
    internal sealed class PathTracingScreen : MyGuiScreenBase
    {
        private static readonly Vector2 kSize = new Vector2(0.56f, 0.71f);
        private const float kRowStep = 0.05f;
        private readonly Settings m_settings;
        private MyGuiControlCombobox m_dlssMode;
        private MyGuiControlCheckbox m_frameGeneration;
        private bool m_saveFailureLogged;

        private const string FrameGenerationToolTip =
            "DLSS Frame Generation: a frame made between each two the game draws, for smoother motion at slightly " +
            "more latency. Needs an NVIDIA GeForce RTX 40 or 50 series GPU, hardware-accelerated GPU scheduling and " +
            "DLSS Ray Reconstruction (the denoiser). RivaTuner's on-screen display slows it down a lot.";

        internal const string Caption = "Path tracing";

        public PathTracingScreen(Settings settings)
            : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, kSize, false, null,
                   MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
        {
            m_settings = settings;
            EnabledBackgroundFade = true;
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "PathTracingSettings";

        // The DLSS mode drop-down and the frame generation switch, for the automated check.
        internal MyGuiControlCombobox DlssModeCombobox => m_dlssMode;
        internal MyGuiControlCheckbox FrameGenerationCheckbox => m_frameGeneration;

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            Vector2 size = Size ?? kSize;
            AddCaption(Caption, null, new Vector2(0f, 0.003f), 0.8f);
            float lineWidth = size.X * 0.83f;
            var separators = new MyGuiControlSeparatorList();
            separators.AddHorizontal(new Vector2(-lineWidth / 2, -size.Y / 2 + 0.075f), lineWidth);
            separators.AddHorizontal(new Vector2(-lineWidth / 2, size.Y / 2 - 0.123f), lineWidth);
            Controls.Add(separators);
            float left = -lineWidth / 2;
            float right = lineWidth / 2;
            float y = -size.Y / 2 + 0.115f;

            Label("Lighting", left, y);
            MyGuiControlCombobox lighting = Combobox(right, y,
                "Replace: the path tracer lights every surface. Overlay: only where its geometry matches the " +
                "game's. Off: the game's own lighting (F2 switches between off and the traced light).");
            lighting.AddItem((long)GateMode.Replace, "Path traced", null, null, false);
            lighting.AddItem((long)GateMode.Overlay, "Path traced (overlay)", null, null, false);
            lighting.AddItem((long)GateMode.Off, "The game's own", null, null, false);
            lighting.SelectItemByKey(m_settings.GateMode == GateMode.RoundTrip ? (long)GateMode.Replace
                                                                                : (long)m_settings.GateMode, false);
            lighting.ItemSelected += () =>
            {
                var mode = (GateMode)lighting.GetSelectedKey();
                m_settings.GateMode = mode;
                NativeApi.RtSetGateMode(mode);
                Hotkeys.SetTracedMode(mode);
                Save();
            };
            y += kRowStep;

            Label("Denoiser", left, y);
            MyGuiControlCombobox denoiser = Combobox(right, y,
                "DLSS Ray Reconstruction on NVIDIA RTX GPUs (sharper, steadier in motion), or the plugin's own filter.");
            denoiser.AddItem((long)Denoiser.Auto, "Auto", null, null, false);
            denoiser.AddItem((long)Denoiser.RayReconstruction, "DLSS Ray Reconstruction", null, null, false);
            denoiser.AddItem((long)Denoiser.Filter, "The plugin's filter", null, null, false);
            denoiser.SelectItemByKey((long)m_settings.Denoiser, false);
            denoiser.ItemSelected += () =>
            {
                m_settings.Denoiser = (Denoiser)denoiser.GetSelectedKey();
                NativeApi.RtSetOption(RtOption.Denoiser, (int)m_settings.Denoiser);
                m_dlssMode.Enabled = m_settings.Denoiser != Denoiser.Filter;
                m_frameGeneration.Enabled = m_settings.Denoiser != Denoiser.Filter;
                Save();
            };
            y += kRowStep;

            Label("DLSS mode", left, y);
            m_dlssMode = Combobox(right, y,
                "With Ray Reconstruction: DLAA traces the light at full resolution; the other modes trace it smaller " +
                "and let Ray Reconstruction upscale it (much cheaper, softer). The game's own rendering keeps its " +
                "resolution.");
            m_dlssMode.AddItem((long)DlssMode.Dlaa, "DLAA", null, null, false);
            m_dlssMode.AddItem((long)DlssMode.Quality, "Quality", null, null, false);
            m_dlssMode.AddItem((long)DlssMode.Balanced, "Balanced", null, null, false);
            m_dlssMode.AddItem((long)DlssMode.Performance, "Performance", null, null, false);
            m_dlssMode.AddItem((long)DlssMode.UltraPerformance, "Ultra performance", null, null, false);
            m_dlssMode.SelectItemByKey((long)m_settings.DlssMode, false);
            m_dlssMode.Enabled = m_settings.Denoiser != Denoiser.Filter;
            m_dlssMode.ItemSelected += () =>
            {
                m_settings.DlssMode = (DlssMode)m_dlssMode.GetSelectedKey();
                NativeApi.RtSetDlssMode(m_settings.DlssMode);
                Log.Info($"Options screen: DLSS mode {m_settings.DlssMode}.");
                Save();
            };
            y += kRowStep;

            // Frame generation uses Ray Reconstruction's depth: not with the plugin's own filter.
            Label("DLSS Frame Generation", left, y);
            m_frameGeneration = new MyGuiControlCheckbox(position: new Vector2(right, y), toolTip: FrameGenerationToolTip,
                isChecked: m_settings.FrameGenerationOn, originAlign: MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER);
            m_frameGeneration.Enabled = m_settings.Denoiser != Denoiser.Filter;
            m_frameGeneration.IsCheckedChanged += SwitchFrameGeneration;
            Controls.Add(m_frameGeneration);
            y += kRowStep;

            Checkbox("Lamps light at any distance", left, right, y, m_settings.LightTree,
                "Lamps and glowing surfaces light their surroundings with traced light at any distance; off: within " +
                "32 m of the camera (beyond it the game's own lamp light).",
                on =>
                {
                    m_settings.LightTree = on;
                    NativeApi.RtSetLightTree(on);
                });
            y += kRowStep;

            Checkbox("Suit lights light their surroundings", left, right, y, m_settings.CharacterLights,
                "The glowing parts of characters' suits light what is around them, your own suit too.",
                on =>
                {
                    m_settings.CharacterLights = on;
                    NativeApi.RtSetOption(RtOption.CharacterLights, on ? 1f : 0f);
                });
            y += kRowStep;

            Checkbox("Glowing decals light their surroundings", left, right, y, m_settings.DecalLights,
                "Indicator lights and signs painted on blocks light what is around them.",
                on =>
                {
                    m_settings.DecalLights = on;
                    NativeApi.RtSetDecalLights(on);
                });
            y += kRowStep;

            Slider("Lamp light", left, right, y, m_settings.LampIntensity,
                "Multiplies the light of the game's lamps (1 = as the game sets them).",
                value =>
                {
                    m_settings.LampIntensity = value;
                    NativeApi.RtSetOption(RtOption.LampIntensity, value);
                });
            y += kRowStep;

            Slider("Emissive light", left, right, y, m_settings.EmissiveIntensity,
                "Multiplies the light that screens, keypads and glowing strips cast on their surroundings (their own " +
                "glow stays as the game draws it).",
                value =>
                {
                    m_settings.EmissiveIntensity = value;
                    NativeApi.RtSetOption(RtOption.EmissiveIntensity, value);
                });

            var done = new MyGuiControlButton(position: new Vector2(0f, size.Y / 2 - 0.07f),
                visualStyle: MyGuiControlButtonStyleEnum.Default, text: new StringBuilder("Done"),
                onButtonClick: _ => CloseScreen());
            Controls.Add(done);
        }

        // On: frame generation started (Streamline loads the first time: a moment's pause) or, when it cannot run, the
        // switch back off and the reason shown. Off: the game presents as without it.
        private void SwitchFrameGeneration(MyGuiControlCheckbox checkbox)
        {
            bool on = checkbox.IsChecked;
            if (on == m_settings.FrameGenerationOn)
                return;  // set back below
            int status = RayTracing.SetFrameGeneration(on, out string note);
            if (on && status == 2)
            {
                Log.Warning("Options screen: DLSS Frame Generation is unavailable: " + note + ".");
                checkbox.IsChecked = false;
                MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(
                    messageText: new StringBuilder("DLSS Frame Generation is unavailable: " + note + "."),
                    messageCaption: new StringBuilder(Caption)));
                return;
            }
            m_settings.FrameGenerationOn = on;
            Log.Info($"Options screen: DLSS Frame Generation {(on ? "on" : "off")}{(note.Length != 0 ? " (" + note + ")" : "")}.");
            if (on && note.Length != 0)
                MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(styleEnum: MyMessageBoxStyleEnum.Info,
                    messageText: new StringBuilder("DLSS Frame Generation is on. Note: " + note + "."),
                    messageCaption: new StringBuilder(Caption)));
            Save();
        }

        private void Label(string text, float x, float y)
        {
            Controls.Add(new MyGuiControlLabel(position: new Vector2(x, y), text: text,
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER));
        }

        private MyGuiControlCombobox Combobox(float right, float y, string toolTip)
        {
            var combobox = new MyGuiControlCombobox(position: new Vector2(right, y), size: new Vector2(0.24f, 0.04f),
                toolTip: toolTip, originAlign: MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER);
            Controls.Add(combobox);
            return combobox;
        }

        private void Checkbox(string text, float left, float right, float y, bool value, string toolTip, Action<bool> apply)
        {
            Label(text, left, y);
            var checkbox = new MyGuiControlCheckbox(position: new Vector2(right, y), toolTip: toolTip, isChecked: value,
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER);
            checkbox.IsCheckedChanged += c =>
            {
                apply(c.IsChecked);
                Save();
            };
            Controls.Add(checkbox);
        }

        // 0 to 5 in steps of 0.1 (the INI takes up to 100), the value beside it.
        private void Slider(string text, float left, float right, float y, float value, string toolTip, Action<float> apply)
        {
            Label(text, left, y);
            var shown = new MyGuiControlLabel(position: new Vector2(right, y), text: Format(value),
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER);
            var slider = new MyGuiControlSlider(position: new Vector2(right - 0.06f, y), minValue: 0f, maxValue: 5f,
                width: 0.18f, defaultValue: Math.Min(value, 5f), toolTip: toolTip,
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER);
            slider.ValueChanged += s =>
            {
                float rounded = (float)Math.Round(s.Value, 1);
                shown.Text = Format(rounded);
                apply(rounded);
                Save();
            };
            Controls.Add(slider);
            Controls.Add(shown);
        }

        private static string Format(float value) => value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        private static bool s_savedLogged;

        private void Save()
        {
            if (m_settings.SaveMenuSettings())
            {
                if (!s_savedLogged)
                {
                    s_savedLogged = true;
                    Log.Info("Options screen: settings saved to " + m_settings.Path + ".");
                }
                return;
            }
            if (m_saveFailureLogged)
                return;
            m_saveFailureLogged = true;
            Log.Warning("Options screen: the settings could not be written to the INI (" +
                        (string.IsNullOrEmpty(m_settings.Path) ? "none loaded" : m_settings.Path) +
                        "); they last until the game closes.");
        }
    }
}
