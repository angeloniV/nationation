using Godot;
using Natiolation.Core;

namespace Natiolation.UI
{
    /// <summary>
    /// Menú principal animado.
    ///
    /// Opciones:
    ///   • NUEVA PARTIDA    — seed aleatoria → carga Main.tscn
    ///   • PARTIDA CON SEMILLA — LineEdit para semilla específica
    ///   • CONTINUAR        — solo visible si existe save.json
    ///   • SALIR
    ///
    /// Fondo: patrón animado de hexágonos procedurales dibujados con _Draw().
    /// </summary>
    public partial class MainMenu : Control
    {
        private static readonly Color BgDark   = new(0.04f, 0.06f, 0.10f, 1.00f);
        private static readonly Color BgPanel  = new(0.07f, 0.10f, 0.17f, 0.97f);
        private static readonly Color Gold     = new(1.00f, 0.82f, 0.14f);
        private static readonly Color TextMain = new(0.94f, 0.94f, 0.97f);
        private static readonly Color TextDim  = new(0.58f, 0.62f, 0.70f);
        private static readonly Color BtnBlue  = new(0.14f, 0.26f, 0.55f);
        private static readonly Color BtnBlueH = new(0.22f, 0.38f, 0.75f);
        private static readonly Color BtnGreen = new(0.18f, 0.42f, 0.20f);
        private static readonly Color BtnGreenH= new(0.26f, 0.56f, 0.28f);
        private static readonly Color BtnGray  = new(0.22f, 0.24f, 0.30f);
        private static readonly Color BtnRed   = new(0.46f, 0.10f, 0.08f);
        private static readonly Color BtnRedH  = new(0.60f, 0.14f, 0.10f);

        private LineEdit _seedInput  = null!;
        private Control  _seedRow    = null!;
        private double   _hexTime    = 0.0;

        public override void _Ready()
        {
            SetAnchorsPreset(LayoutPreset.FullRect);
            MouseFilter = MouseFilterEnum.Ignore;
            BuildUI();
        }

        public override void _Process(double delta)
        {
            _hexTime += delta * 0.4;
            QueueRedraw();
        }

        // ================================================================
        //  FONDO ANIMADO — hexágonos procedurales
        // ================================================================

        public override void _Draw()
        {
            var sz = GetViewportRect().Size;
            DrawRect(new Rect2(Vector2.Zero, sz), BgDark);

            float hexR  = 60f;
            float hexW  = hexR * Mathf.Sqrt(3f);
            float hexH  = hexR * 2f;
            float cols  = sz.X / hexW + 3f;
            float rows  = sz.Y / (hexH * 0.75f) + 3f;

            for (int row = -1; row < (int)rows; row++)
                for (int col = -1; col < (int)cols; col++)
                {
                    float ox = (row % 2 == 0) ? 0f : hexW * 0.5f;
                    float cx = col * hexW + ox + (float)(Mathf.Sin(_hexTime + row * 0.3) * 4.0);
                    float cy = row * hexH * 0.75f;

                    float dist = new Vector2(cx - sz.X * 0.5f, cy - sz.Y * 0.5f).Length();
                    float alpha = Mathf.Clamp(1.0f - dist / (sz.Length() * 0.6f), 0f, 1f);
                    float pulse = (float)(Mathf.Sin(_hexTime * 0.7 + col * 0.5 + row * 0.4) + 1.0) * 0.5f;

                    var hexColor = new Color(0.10f + pulse * 0.04f, 0.14f + pulse * 0.04f,
                                            0.22f + pulse * 0.04f, 0.55f * alpha);

                    DrawHex(cx, cy, hexR - 2f, hexColor);
                }
        }

        private void DrawHex(float cx, float cy, float r, Color color)
        {
            var pts = new Vector2[6];
            for (int i = 0; i < 6; i++)
            {
                float ang = Mathf.DegToRad(60f * i + 30f);
                pts[i] = new Vector2(cx + r * Mathf.Cos(ang), cy + r * Mathf.Sin(ang));
            }
            // Draw outline
            for (int i = 0; i < 6; i++)
                DrawLine(pts[i], pts[(i + 1) % 6], color, 1.2f);
        }

        // ================================================================
        //  UI
        // ================================================================

        private void BuildUI()
        {
            // Panel central
            var panel = new PanelContainer();
            panel.SetAnchorsPreset(LayoutPreset.Center);
            panel.CustomMinimumSize = new Vector2(480, 0);
            var style = new StyleBoxFlat { BgColor = BgPanel };
            style.SetCornerRadiusAll(14);
            style.SetBorderWidthAll(1);
            style.BorderColor = new Color(0.30f, 0.38f, 0.60f, 0.55f);
            style.ContentMarginLeft = style.ContentMarginRight = 48;
            style.ContentMarginTop  = 40;
            style.ContentMarginBottom = 44;
            panel.AddThemeStyleboxOverride("panel", style);
            AddChild(panel);

            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 18);
            panel.AddChild(col);

            // Título
            var title = new Label
            {
                Text = "NATIOLATION",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            title.AddThemeFontSizeOverride("font_size", 42);
            title.AddThemeColorOverride("font_color", Gold);
            col.AddChild(title);

            var sub = new Label
            {
                Text = "Juego de Estrategia por Turnos",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            sub.AddThemeFontSizeOverride("font_size", 15);
            sub.AddThemeColorOverride("font_color", TextDim);
            col.AddChild(sub);

            // Separador
            col.AddChild(MakeSep());

            // ── Botones ───────────────────────────────────────────────────

            // NUEVA PARTIDA
            var btnNew = MakeBtn("▶  NUEVA PARTIDA", BtnGreen, BtnGreenH, 19);
            btnNew.Pressed += OnNewGame;
            col.AddChild(btnNew);

            // PARTIDA CON SEMILLA
            var btnSeed = MakeBtn("⚙  CON SEMILLA ESPECÍFICA", BtnBlue, BtnBlueH, 16);
            btnSeed.Pressed += ToggleSeedRow;
            col.AddChild(btnSeed);

            // Fila de semilla (oculta inicialmente)
            _seedRow = new HBoxContainer();
            _seedRow.Visible = false;
            _seedRow.AddThemeConstantOverride("separation", 8);

            _seedInput = new LineEdit
            {
                PlaceholderText = "Número de semilla...",
                CustomMinimumSize = new Vector2(0, 38),
            };
            _seedInput.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _seedInput.AddThemeFontSizeOverride("font_size", 16);
            _seedRow.AddChild(_seedInput);

            var btnGo = MakeBtn("IR", BtnBlue, BtnBlueH, 15);
            btnGo.CustomMinimumSize = new Vector2(64, 38);
            btnGo.Pressed += OnSeedGame;
            _seedRow.AddChild(btnGo);
            col.AddChild(_seedRow);

            // CONTINUAR (solo si hay save)
            if (GameSettings.Instance?.HasSave == true)
            {
                var btnCont = MakeBtn("⏵  CONTINUAR PARTIDA", BtnBlue, BtnBlueH, 18);
                btnCont.Pressed += OnContinue;
                col.AddChild(btnCont);
            }

            col.AddChild(MakeSep());

            // SALIR
            var btnQuit = MakeBtn("✕  SALIR", BtnRed, BtnRedH, 15);
            btnQuit.Pressed += () => GetTree().Quit();
            col.AddChild(btnQuit);

            // Versión
            var ver = new Label { Text = "v0.1 — Godot 4 / Vulkan" };
            ver.HorizontalAlignment = HorizontalAlignment.Center;
            ver.AddThemeFontSizeOverride("font_size", 12);
            ver.AddThemeColorOverride("font_color", new Color(0.35f, 0.38f, 0.45f));
            col.AddChild(ver);
        }

        // ================================================================
        //  ACCIONES
        // ================================================================

        private void OnNewGame()
        {
            if (GameSettings.Instance != null)
                GameSettings.Instance.MapSeed = 0;   // seed aleatoria
            GetTree().ChangeSceneToFile("res://scenes/main/Main.tscn");
        }

        private void OnSeedGame()
        {
            if (int.TryParse(_seedInput.Text.Trim(), out int seed) && seed != 0)
            {
                if (GameSettings.Instance != null)
                    GameSettings.Instance.MapSeed = seed;
            }
            else
            {
                if (GameSettings.Instance != null)
                    GameSettings.Instance.MapSeed = 0;
            }
            GetTree().ChangeSceneToFile("res://scenes/main/Main.tscn");
        }

        private void OnContinue()
        {
            // TODO: SaveManager.Load() antes de cambiar de escena
            GetTree().ChangeSceneToFile("res://scenes/main/Main.tscn");
        }

        private void ToggleSeedRow()
        {
            _seedRow.Visible = !_seedRow.Visible;
        }

        // ================================================================
        //  HELPERS
        // ================================================================

        private static Button MakeBtn(string text, Color normal, Color hover, int fontSize)
        {
            var btn = new Button { Text = text };
            btn.CustomMinimumSize = new Vector2(0, 46);
            btn.SizeFlagsHorizontal = SizeFlags.ExpandFill;

            var sn = new StyleBoxFlat { BgColor = normal }; sn.SetCornerRadiusAll(8);
            var sh = new StyleBoxFlat { BgColor = hover  }; sh.SetCornerRadiusAll(8);
            var sp = new StyleBoxFlat { BgColor = normal.Darkened(0.15f) }; sp.SetCornerRadiusAll(8);
            var sf = new StyleBoxFlat { BgColor = normal }; sf.SetCornerRadiusAll(8);
            btn.AddThemeStyleboxOverride("normal",  sn);
            btn.AddThemeStyleboxOverride("hover",   sh);
            btn.AddThemeStyleboxOverride("pressed", sp);
            btn.AddThemeStyleboxOverride("focus",   sf);
            btn.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 1.00f));
            btn.AddThemeFontSizeOverride("font_size", fontSize);
            return btn;
        }

        private static HSeparator MakeSep()
        {
            var sep = new HSeparator();
            var style = new StyleBoxFlat { BgColor = new Color(0.25f, 0.30f, 0.45f, 0.50f) };
            style.ContentMarginTop = style.ContentMarginBottom = 4;
            sep.AddThemeStyleboxOverride("separator", style);
            return sep;
        }
    }
}
