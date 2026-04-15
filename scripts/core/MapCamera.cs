using Godot;

namespace Natiolation.Core
{
    /// <summary>
    /// Cámara isométrica 3D estilo Civilization.
    ///
    /// Controles:
    ///   WASD / flechas  — pan del target
    ///   Scroll          — zoom (acercar/alejar)
    ///   Botón medio     — rotar yaw alrededor del target
    ///
    /// Al inicializarse crea WorldEnvironment + DirectionalLight en la escena.
    /// </summary>
    public partial class MapCamera : Camera3D
    {
        [Export] public float PanSpeed      = 16f;
        [Export] public float ZoomSpeed    = 2.5f;
        [Export] public float ZoomMin      = 6f;
        [Export] public float ZoomMax      = 58f;
        [Export] public float ZoomSmoothK  = 12f;   // velocidad de interpolación del zoom

        // Estado de cámara
        private Vector3 _target      = new(160f, 0f, 108f);
        private float   _dist        = 28f;          // distancia actual (interpolada)
        private float   _targetDist  = 28f;          // distancia objetivo (cambia en scroll/tecla)
        private float   _pitch       = 52f;
        private float   _yaw         = 10f;

        private bool    _rotating  = false;
        private Vector2 _rotStart;

        // Límites del mapa en coordenadas mundo (para 60×40 con HexSize=4)
        private const float WorldMaxX =  560f;
        private const float WorldMaxZ =  254f;
        private const float WorldPad  =   25f;

        // Exposición pública para el minimapa
        public Vector3 CameraTarget   => _target;
        public float   CameraDistance => _dist;

        public override void _Ready()
        {
            SetupLighting();
            UpdateTransform();
            GetWindow().GrabFocus();
        }

        // ── Permite que UnitManager centre la cámara en el spawn ──────────
        public void FocusOn(Vector3 worldPos, bool immediate = true)
        {
            _target = new Vector3(worldPos.X, 0f, worldPos.Z);
            if (immediate) UpdateTransform();
        }

        // ================================================================
        //  INPUT
        // ================================================================

        public override void _Process(double delta)
        {
            float dt = (float)delta;
            HandleKeyboardPan(dt);
            HandleKeyboardZoom(dt);

            // Zoom suave: interpola la distancia actual hacia la objetivo
            if (!Mathf.IsEqualApprox(_dist, _targetDist, 0.01f))
            {
                _dist = Mathf.Lerp(_dist, _targetDist, dt * ZoomSmoothK);
                UpdateTransform();
            }
        }

        public override void _Input(InputEvent @event)
        {
            if (@event is InputEventMouseButton mb)
            {
                if (mb.ButtonIndex == MouseButton.WheelUp)
                    _targetDist = Mathf.Clamp(_targetDist - ZoomSpeed, ZoomMin, ZoomMax);
                else if (mb.ButtonIndex == MouseButton.WheelDown)
                    _targetDist = Mathf.Clamp(_targetDist + ZoomSpeed, ZoomMin, ZoomMax);
                else if (mb.ButtonIndex == MouseButton.Middle)
                    { _rotating = mb.Pressed; _rotStart = mb.Position; }
            }

            if (@event is InputEventMouseMotion mm && _rotating)
            {
                _yaw   -= mm.Relative.X * 0.35f;
                _pitch  = Mathf.Clamp(_pitch - mm.Relative.Y * 0.18f, 20f, 80f);
                UpdateTransform();
            }
        }

        private void HandleKeyboardZoom(float dt)
        {
            // +/= acercar   –  alejar   (se mantiene presionado para zoom continuo)
            float zoomDelta = 0f;
            if (Input.IsKeyPressed(Key.Plus)  || Input.IsKeyPressed(Key.Equal)) zoomDelta -= 1f;
            if (Input.IsKeyPressed(Key.Minus) || Input.IsKeyPressed(Key.KpSubtract)) zoomDelta += 1f;
            if (zoomDelta == 0f) return;
            _targetDist = Mathf.Clamp(_targetDist + zoomDelta * ZoomSpeed * dt * 8f, ZoomMin, ZoomMax);
        }

        private void HandleKeyboardPan(float dt)
        {
            var dir = Vector2.Zero;

            if (Input.IsKeyPressed(Key.W) || Input.IsActionPressed("ui_up"))    dir.Y -= 1;
            if (Input.IsKeyPressed(Key.S) || Input.IsActionPressed("ui_down"))  dir.Y += 1;
            if (Input.IsKeyPressed(Key.A) || Input.IsActionPressed("ui_left"))  dir.X -= 1;
            if (Input.IsKeyPressed(Key.D) || Input.IsActionPressed("ui_right")) dir.X += 1;

            if (dir == Vector2.Zero) return;

            dir = dir.Normalized() * PanSpeed * dt;

            float yRad = Mathf.DegToRad(_yaw);
            var   fwd  = new Vector3(-Mathf.Sin(yRad), 0f, -Mathf.Cos(yRad));
            var   rgt  = new Vector3( Mathf.Cos(yRad), 0f, -Mathf.Sin(yRad));

            _target += fwd * (-dir.Y) + rgt * dir.X;
            _target.X = Mathf.Clamp(_target.X, -WorldPad, WorldMaxX + WorldPad);
            _target.Z = Mathf.Clamp(_target.Z, -WorldPad, WorldMaxZ + WorldPad);
            UpdateTransform();
        }

        private void UpdateTransform()
        {
            float pitchRad = Mathf.DegToRad(_pitch);
            float yawRad   = Mathf.DegToRad(_yaw);

            float hDist = _dist * Mathf.Cos(pitchRad);
            float vDist = _dist * Mathf.Sin(pitchRad);

            Position = _target + new Vector3(
                hDist * Mathf.Sin(yawRad),
                vDist,
                hDist * Mathf.Cos(yawRad));

            LookAt(_target, Vector3.Up);
        }

        // ================================================================
        //  ENTORNO: SKY + LUZ
        // ================================================================

        private void SetupLighting()
        {
            // ── Sky procedural — hora dorada ────────────────────────────
            var skyMat = new ProceduralSkyMaterial
            {
                SkyTopColor        = new Color(0.18f, 0.38f, 0.72f),
                SkyHorizonColor    = new Color(0.62f, 0.74f, 0.95f),
                GroundHorizonColor = new Color(0.44f, 0.40f, 0.34f),
                GroundBottomColor  = new Color(0.22f, 0.18f, 0.14f),
                SunAngleMax        = 25f,
                SunCurve           = 0.12f,
            };
            var sky = new Sky { SkyMaterial = skyMat };

            var env = new Godot.Environment
            {
                // ── Sky / fondo ─────────────────────────────────────────
                BackgroundMode     = Godot.Environment.BGMode.Sky,
                Sky                = sky,
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightEnergy = 0.55f,

                // ── SSAO — profundidad y peso a todos los objetos ────────
                SsaoEnabled   = true,
                SsaoRadius    = 1.2f,
                SsaoIntensity = 2.0f,
                SsaoPower     = 1.5f,
                SsaoDetail    = 0.5f,

                // ── SSIL — rebotes de luz color-aware ────────────────────
                SsilEnabled   = true,
                SsilRadius    = 5.0f,
                SsilIntensity = 1.2f,

                // ── SSR — reflejo en agua ────────────────────────────────
                SsrEnabled           = true,
                SsrMaxSteps          = 64,
                SsrFadeIn            = 0.15f,
                SsrFadeOut           = 2.0f,
                SsrDepthTolerance    = 0.2f,

                // ── Glow / Bloom — highlights que "respiran" ─────────────
                GlowEnabled      = true,
                GlowIntensity    = 0.60f,
                GlowStrength     = 0.80f,
                GlowBloom        = 0.08f,
                GlowBlendMode    = Godot.Environment.GlowBlendModeEnum.Softlight,
                GlowHdrThreshold = 0.90f,

                // ── Niebla atmosférica — perspectiva y profundidad ───────
                FogEnabled            = true,
                FogLightColor         = new Color(0.72f, 0.80f, 0.92f),
                FogDensity            = 0.0004f,
                FogAerialPerspective  = 0.30f,

                // ── Tone mapping filmic — HDR cinematográfico ────────────
                TonemapMode     = Godot.Environment.ToneMapper.Filmic,
                TonemapExposure = 1.10f,
                TonemapWhite    = 1.60f,

                // ── Ajuste de color ──────────────────────────────────────
                AdjustmentEnabled    = true,
                AdjustmentBrightness = 1.05f,
                AdjustmentContrast   = 1.10f,
                AdjustmentSaturation = 1.15f,
            };

            var worldEnv = new WorldEnvironment { Environment = env };
            GetParent().AddChild(worldEnv);

            // ── Sol (luz direccional cálida — hora dorada) ──────────────
            var sun = new DirectionalLight3D
            {
                LightColor           = new Color(1.00f, 0.94f, 0.82f),
                LightEnergy          = 1.80f,
                LightAngularDistance = 0.8f,
                ShadowEnabled        = true,
            };
            sun.DirectionalShadowMode         = DirectionalLight3D.ShadowMode.Parallel4Splits;
            sun.DirectionalShadowMaxDistance   = 200f;
            sun.DirectionalShadowBlendSplits   = true;
            sun.RotationDegrees = new Vector3(-48f, 28f, 0f);
            GetParent().AddChild(sun);

            // ── Luz de relleno (sky bounce — azul cielo) ─────────────────
            var fill = new DirectionalLight3D
            {
                LightColor    = new Color(0.62f, 0.74f, 0.95f),
                LightEnergy   = 0.28f,
                ShadowEnabled = false,
            };
            fill.RotationDegrees = new Vector3(-15f, -155f, 0f);
            GetParent().AddChild(fill);

            // ── DOF sutil + Auto-exposición ──────────────────────────────
            Fov = 42f;
            var attr = new CameraAttributesPractical
            {
                DofBlurFarEnabled    = true,
                DofBlurFarDistance   = 80f,
                DofBlurFarTransition = 40f,
                DofBlurAmount        = 0.06f,
                AutoExposureEnabled  = true,
                AutoExposureMinSensitivity = 50f,
                AutoExposureMaxSensitivity = 800f,
                AutoExposureSpeed    = 0.5f,
                AutoExposureScale    = 0.4f,
            };
            Attributes = attr;
        }
    }
}
