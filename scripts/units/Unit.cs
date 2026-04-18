using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;
using Natiolation.Map;

namespace Natiolation.Units
{
    /// <summary>
    /// Unidad 3D con tres niveles de calidad visual, en orden de prioridad:
    ///
    ///   1. MODELO GLB  — res://assets/units/&lt;UnitType&gt;.glb
    ///                    (Kenney Tiny Town, Mini Characters, etc.)
    ///   2. SPRITE PNG  — res://assets/units/&lt;UnitType&gt;.png
    ///                    (Kenney 1-Bit Pack, cualquier sprite 2D con transparencia)
    ///   3. PIEZA DE AJEDREZ  — fallback procedural: cilindros + esfera,
    ///                    forma distinta por tipo de unidad.
    ///
    /// La base coloreada (disco + aro) siempre aparece para identificar civilización.
    /// </summary>
    public partial class Unit : Node3D
    {
        public UnitType UnitType  { get; set; } = UnitType.Settler;
        public Color    CivColor  { get; set; } = new Color(0.2f, 0.45f, 0.95f);
        public int      CivIndex  { get; set; } = 0;

        public int  Q { get; private set; }
        public int  R { get; private set; }

        public int   MaxMovement          => UnitTypeData.GetStats(UnitType).MaxMovement;
        public float RemainingMovement    { get; private set; }
        public int   CurrentMovementPoints => (int)RemainingMovement;

        // ── HP ──────────────────────────────────────────────────────────────
        [Export] public int MaxHP     { get; private set; }
        [Export] public int CurrentHP { get; private set; }

        // ── C# events (UI decoupling) ────────────────────────────────────────
        public event System.Action<int, int>? HPChanged;   // (currentHP, maxHP)
        public event System.Action?           Died;

        public bool  IsMoving          { get; private set; }
        public bool  IsFortified       { get; private set; }
        public bool  IsVeteran         { get; private set; }
        public bool  IsReadyForTurn    { get; private set; }
        public bool  IsAutoExploring   { get; private set; }
        private Node3D? _fortifyNode;
        private Node3D? _veteranNode;

        private bool  _selected;
        private float _pulse;

        private Label3D             _label    = null!;
        private OmniLight3D         _light    = null!;
        private MeshInstance3D?     _hpBarFg  = null;
        private StandardMaterial3D? _hpBarMat = null;

        // Alturas del token
        private const float BASE_TOP   = 0.10f;   // Y de la superficie de la base
        private const float FIGURE_BOT = 0.10f;   // donde empieza la figura (encima de la base)

        // ================================================================

        public override void _Ready()
        {
            var stats     = UnitTypeData.GetStats(UnitType);
            MaxHP         = Mathf.Max(30, stats.CombatStrength * 10);
            CurrentHP     = MaxHP;
            RemainingMovement = MaxMovement;
            BuildVisuals();
        }

        public override void _Process(double delta)
        {
            if (!_selected) return;
            _pulse = (_pulse + (float)delta * 3f) % Mathf.Tau;
            float p = (Mathf.Sin(_pulse) + 1f) * 0.5f;
            _light.LightEnergy = 0.6f + p * 0.9f;
            _light.OmniRange   = 2.5f + p * 1.5f;
        }

        // ================================================================
        //  POSICIÓN Y MOVIMIENTO
        // ================================================================

        public void PlaceAt(int q, int r, float tileHeight)
        {
            Q = q; R = r;
            Position = HexTile3D.AxialToWorld(q, r)
                     + new Vector3(0f, tileHeight + HexTile3D.TokenHover, 0f);
        }

        /// <summary>
        /// Actualiza solo la posición lógica (Q/R) sin mover el modelo 3D.
        /// Usado por Army para mantener sincronizadas las coordenadas de sus unidades.
        /// </summary>
        public void SetLogicalPosition(int q, int r)
        {
            Q = q;
            R = r;
        }

        public void Select(bool value)
        {
            _selected          = value;
            _pulse             = 0f;
            _light.Visible     = value;
            _light.LightEnergy = 0.6f;
            UpdateLabel();
        }

        // ── Waypoint (movimiento multi-turno) ────────────────────────────
        public int? WaypointQ { get; private set; }
        public int? WaypointR { get; private set; }
        public bool HasWaypoint => WaypointQ.HasValue;

        public void SetWaypoint(int q, int r)
        {
            WaypointQ      = q;
            WaypointR      = r;
            IsReadyForTurn = true;   // tener destino cuenta como haber dado orden
            UpdateLabel();
        }

        public void ClearWaypoint()
        {
            WaypointQ = null;
            WaypointR = null;
            UpdateLabel();
        }

        // ──────────────────────────────────────────────────────────────────

        public void ResetMovement()
        {
            RemainingMovement = MaxMovement;
            // Fortificadas, unidades con waypoint o en auto-exploración arrancan listas
            IsReadyForTurn    = IsFortified || HasWaypoint || IsAutoExploring;
            UpdateLabel();
        }

        /// <summary>Activa o desactiva el modo auto-exploración (tecla A).</summary>
        public void SetAutoExplore(bool enabled)
        {
            IsAutoExploring = enabled;
            if (enabled) IsReadyForTurn = true;
            UpdateLabel();
        }

        /// <summary>Gasta una cantidad específica de movimiento (usado por auto-movimiento de waypoint).</summary>
        public void ConsumeMovement(float cost)
        {
            RemainingMovement = Mathf.Max(0f, RemainingMovement - cost);
            IsReadyForTurn    = true;
            UpdateLabel();
        }

        /// <summary>Gasta todo el movimiento restante (al construir una mejora, atacar, etc.).</summary>
        public void ConsumeAllMovement()
        {
            RemainingMovement = 0;
            IsReadyForTurn    = true;
            UpdateLabel();
        }

        /// <summary>Salta el turno de esta unidad: sin acción, sin movimiento.</summary>
        public void SkipTurn()
        {
            ClearWaypoint();
            RemainingMovement = 0;
            IsReadyForTurn    = true;
            UpdateLabel();
        }

        // ── Fortificación ─────────────────────────────────────────────────

        /// <summary>Fortifica la unidad: visual de escudo, sin movimiento restante.</summary>
        public void Fortify()
        {
            if (IsFortified) return;
            IsFortified = true;
            RemainingMovement = 0;
            AddFortifyVisual();
            UpdateLabel();
        }

        /// <summary>Desfortifica la unidad (al recibir órdenes de movimiento).</summary>
        public void Unfortify()
        {
            if (!IsFortified) return;
            IsFortified = false;
            RemoveFortifyVisual();
            UpdateLabel();
        }

        private void AddFortifyVisual()
        {
            RemoveFortifyVisual();
            _fortifyNode = new Node3D { Name = "FortifyVisual" };

            // Anillo dorado flotante sobre la unidad
            _fortifyNode.AddChild(MI(
                new CylinderMesh { TopRadius = 0.53f, BottomRadius = 0.59f,
                                   Height = 0.06f, RadialSegments = 16 },
                SolidMat(new Color(0.96f, 0.82f, 0.20f), roughness: 0.18f, metallic: 0.72f),
                new Vector3(0f, 1.60f, 0f)));

            // Disco interior con color de civ
            _fortifyNode.AddChild(MI(
                new CylinderMesh { TopRadius = 0.38f, BottomRadius = 0.38f,
                                   Height = 0.04f, RadialSegments = 16 },
                SolidMat(CivColor.Lightened(0.20f), roughness: 0.32f),
                new Vector3(0f, 1.60f, 0f)));

            // Cruz blanca (icono de escudo)
            _fortifyNode.AddChild(MI(
                new BoxMesh { Size = new Vector3(0.08f, 0.30f, 0.06f) },
                SolidMat(Colors.White, roughness: 0.40f),
                new Vector3(0f, 1.61f, 0f)));
            _fortifyNode.AddChild(MI(
                new BoxMesh { Size = new Vector3(0.28f, 0.08f, 0.06f) },
                SolidMat(Colors.White, roughness: 0.40f),
                new Vector3(0f, 1.63f, 0f)));

            AddChild(_fortifyNode);
        }

        private void RemoveFortifyVisual()
        {
            if (_fortifyNode == null || !IsInstanceValid(_fortifyNode)) return;
            RemoveChild(_fortifyNode);
            _fortifyNode.QueueFree();
            _fortifyNode = null;
        }

        // ── Veterano ──────────────────────────────────────────────────────

        /// <summary>Convierte la unidad en veterana (bonus de combate + visual estrella).</summary>
        public void MakeVeteran()
        {
            if (IsVeteran) return;
            IsVeteran = true;
            AddVeteranVisual();
            UpdateLabel();
        }

        private void AddVeteranVisual()
        {
            if (_veteranNode != null && IsInstanceValid(_veteranNode))
            {
                RemoveChild(_veteranNode);
                _veteranNode.QueueFree();
            }
            _veteranNode = new Node3D { Name = "VeteranVisual" };
            // Pequeña estrella dorada flotando a la derecha del sombrero
            _veteranNode.AddChild(MI(
                new CylinderMesh { TopRadius = 0f, BottomRadius = 0.18f,
                                   Height = 0.22f, RadialSegments = 5 },
                SolidMat(new Color(0.96f, 0.82f, 0.10f), roughness: 0.18f, metallic: 0.72f),
                new Vector3(0.45f, 1.55f, 0f)));
            _veteranNode.AddChild(MI(
                new CylinderMesh { TopRadius = 0.18f, BottomRadius = 0f,
                                   Height = 0.22f, RadialSegments = 5 },
                SolidMat(new Color(0.96f, 0.82f, 0.10f), roughness: 0.18f, metallic: 0.72f),
                new Vector3(0.45f, 1.77f, 0f)));
            AddChild(_veteranNode);
        }

        public async Task MoveTo(List<HexCoord> path, MapManager map)
        {
            if (IsMoving || path.Count < 2) return;
            if (IsFortified) Unfortify();   // recibir órdenes de movimiento desfortifica
            // Movimiento manual cancela el waypoint (el jugador tomó el control)
            ClearWaypoint();
            IsReadyForTurn = true;          // moverse cuenta como acción
            IsMoving = true;

            for (int i = 1; i < path.Count; i++)
            {
                if (!IsInstanceValid(this)) break;
                var step  = path[i];
                var ttype = map.GetTileType(step.Q, step.R);
                if (ttype == null) break;
                float cost = map.GetEffectiveCost(step.Q, step.R);
                if (RemainingMovement < cost) break;

                RemainingMovement -= cost;
                Q = step.Q; R = step.R;

                float destH = map.GetTileHeight(step.Q, step.R);
                var   dest  = HexTile3D.AxialToWorld(step.Q, step.R)
                            + new Vector3(0f, destH + HexTile3D.TokenHover, 0f);

                var tween = CreateTween();
                tween.SetEase(Tween.EaseType.InOut);
                tween.SetTrans(Tween.TransitionType.Sine);
                tween.TweenProperty(this, "position", dest, 0.20f);
                await ToSignal(tween, Tween.SignalName.Finished);
                UpdateLabel();
            }

            IsMoving = false;
        }

        /// <summary>
        /// Mueve el modelo 3D suavemente a lo largo de un array de posiciones mundo.
        /// Animación pura — no consume puntos de movimiento.
        /// Usado por efectos externos (cutscenes, demos, etc.).
        /// </summary>
        public async Task MoveAlongPath(Vector3[] worldPath)
        {
            if (IsMoving || worldPath.Length < 2) return;
            IsMoving = true;
            for (int i = 1; i < worldPath.Length; i++)
            {
                if (!IsInstanceValid(this)) break;
                var tween = CreateTween();
                tween.SetEase(Tween.EaseType.InOut);
                tween.SetTrans(Tween.TransitionType.Sine);
                tween.TweenProperty(this, "position", worldPath[i], 0.18f);
                await ToSignal(tween, Tween.SignalName.Finished);
            }
            IsMoving = false;
        }

        // ================================================================
        //  HP — DAÑO Y CURACIÓN
        // ================================================================

        /// <summary>Aplica daño a la unidad. Dispara HPChanged y Died si HP llega a 0.</summary>
        public void TakeDamage(int amount)
        {
            if (amount <= 0) return;
            CurrentHP = Mathf.Max(0, CurrentHP - amount);
            UpdateHPBar();
            HPChanged?.Invoke(CurrentHP, MaxHP);
            if (CurrentHP <= 0)
                Died?.Invoke();
        }

        /// <summary>Cura la unidad, sin superar MaxHP.</summary>
        public void Heal(int amount)
        {
            if (amount <= 0) return;
            CurrentHP = Mathf.Min(MaxHP, CurrentHP + amount);
            UpdateHPBar();
            HPChanged?.Invoke(CurrentHP, MaxHP);
        }

        private void UpdateHPBar()
        {
            if (_hpBarFg == null || _hpBarMat == null) return;
            float ratio = MaxHP > 0 ? (float)CurrentHP / MaxHP : 0f;
            ratio = Mathf.Clamp(ratio, 0.001f, 1f);

            // Escalar desde el centro (billboard: X visual = camera-right, no world-X)
            _hpBarFg.Scale = new Vector3(ratio, 1f, 1f);

            // Color: verde → amarillo → rojo
            _hpBarMat.AlbedoColor = ratio > 0.5f
                ? new Color(0.18f, 0.82f, 0.18f).Lerp(new Color(0.95f, 0.80f, 0.10f), (1f - ratio) * 2f)
                : new Color(0.95f, 0.80f, 0.10f).Lerp(new Color(0.90f, 0.12f, 0.10f), (0.5f - ratio) * 2f);
        }

        // ================================================================
        //  CONSTRUCCIÓN DE VISUALES
        // ================================================================

        private const float UnitScale = 1.42f;   // factor de escala global de la figura

        private void BuildVisuals()
        {
            // Escalar la figura entera para que sea más visible en el mapa
            Scale = new Vector3(UnitScale, UnitScale, UnitScale);

            // Shadow: ajustar posición para que quede sobre la superficie del tile
            float groundY = -(HexTile3D.TokenHover - 0.04f) / UnitScale;
            AddChild(MI(
                new CylinderMesh { TopRadius = 0.58f, BottomRadius = 0.58f,
                                   Height = 0.03f, RadialSegments = 12 },
                UnlitMat(new Color(0f, 0f, 0f, 0.42f), alpha: true),
                new Vector3(0f, groundY, 0f)));

            // ── Base disc — aro exterior + relleno civ ───────────────
            AddChild(MI(
                new CylinderMesh { TopRadius = 0.56f, BottomRadius = 0.62f,
                                   Height = 0.10f, RadialSegments = 16 },
                SolidMat(Colors.White.Lerp(CivColor, 0.18f), roughness: 0.15f, metallic: 0.60f),
                new Vector3(0f, 0.05f, 0f)));
            AddChild(MI(
                new CylinderMesh { TopRadius = 0.48f, BottomRadius = 0.48f,
                                   Height = 0.11f, RadialSegments = 16 },
                SolidMat(CivColor, roughness: 0.38f),
                new Vector3(0f, 0.055f, 0f)));
            // Pequeño anillo negro interior para contraste
            AddChild(MI(
                new CylinderMesh { TopRadius = 0.35f, BottomRadius = 0.35f,
                                   Height = 0.115f, RadialSegments = 12 },
                SolidMat(new Color(0.08f, 0.08f, 0.10f), roughness: 0.80f),
                new Vector3(0f, 0.057f, 0f)));

            // ── Figura principal (GLB > PNG > humanoide) ─────────────
            if (!TryLoadGlb())
                if (!TryLoadSprite())
                    BuildHumanoid();

            // ── Label de movimiento ───────────────────────────────────
            _label = new Label3D
            {
                Text                  = "",
                FontSize              = 46,
                PixelSize             = 0.0065f,
                Billboard             = BaseMaterial3D.BillboardModeEnum.Enabled,
                AlphaScissorThreshold = 0.10f,
                Modulate              = Colors.White,
                Position              = new Vector3(0f, 1.60f / UnitScale, 0f),
                NoDepthTest           = true,
                HorizontalAlignment   = HorizontalAlignment.Center,
            };
            AddChild(_label);
            UpdateLabel();

            // ── Luz de selección ─────────────────────────────────────
            _light = new OmniLight3D
            {
                LightColor  = new Color(1.00f, 0.88f, 0.15f),
                LightEnergy = 0.6f,
                OmniRange   = 3.0f,
                Visible     = false,
                Position    = new Vector3(0f, 1.45f / UnitScale, 0f),
            };
            AddChild(_light);

            // ── Estandarte + barra de HP ─────────────────────────
            BuildBanner();
        }

        // ================================================================
        //  ESTANDARTE Y BARRA DE HP
        // ================================================================

        /// <summary>
        /// Construye el estandarte flotante sobre la unidad: asta + bandera civ + barra de HP.
        /// Los elementos visuales usan BillboardMode para ser siempre visibles.
        /// </summary>
        private void BuildBanner()
        {
            // Posiciones en espacio local (escala 1.0; Unit.Scale ya es UnitScale)
            const float poleLocalBot = 1.34f;   // local-Y donde empieza el asta
            const float poleH        = 0.52f;   // altura local del asta
            const float flagW        = 0.36f;   // ancho local de la bandera
            const float flagH        = 0.22f;   // alto  local de la bandera
            const float barW         = 0.44f;   // ancho local de la barra HP
            const float barH         = 0.068f;  // alto  local de la barra HP
            const float barY         = poleLocalBot + poleH + 0.048f + barH * 0.5f;
            const float poleX        = 0.52f;   // ligeramente a la derecha del centro

            // ── Asta ──────────────────────────────────────────────
            AddChild(MI(
                new CylinderMesh { TopRadius = 0.015f, BottomRadius = 0.015f,
                                   Height = poleH, RadialSegments = 5 },
                SolidMat(new Color(0.88f, 0.84f, 0.76f), roughness: 0.22f, metallic: 0.70f),
                new Vector3(poleX, poleLocalBot + poleH * 0.5f, 0f)));

            // Punta de la asta
            AddChild(MI(
                new CylinderMesh { TopRadius = 0f, BottomRadius = 0.022f,
                                   Height = 0.065f, RadialSegments = 5 },
                SolidMat(new Color(0.92f, 0.80f, 0.22f), roughness: 0.20f, metallic: 0.75f),
                new Vector3(poleX, poleLocalBot + poleH + 0.032f, 0f)));

            // ── Bandera (billboard, color civ) ────────────────────
            var flagMat = new StandardMaterial3D
            {
                AlbedoColor        = CivColor,
                Roughness          = 0.60f,
                BillboardMode      = BaseMaterial3D.BillboardModeEnum.Enabled,
                BillboardKeepScale = true,
            };
            var flagInst = new MeshInstance3D
            {
                Mesh             = new QuadMesh { Size = new Vector2(flagW, flagH) },
                MaterialOverride = flagMat,
                Position         = new Vector3(poleX + flagW * 0.5f,
                                               poleLocalBot + poleH - flagH * 0.5f, 0f),
            };
            AddChild(flagInst);

            // Icono blanco en la bandera
            var iconMat = new StandardMaterial3D
            {
                AlbedoColor        = Colors.White.Lerp(CivColor, 0.15f),
                Roughness          = 0.55f,
                BillboardMode      = BaseMaterial3D.BillboardModeEnum.Enabled,
                BillboardKeepScale = true,
            };
            var iconInst = new MeshInstance3D
            {
                Mesh             = new QuadMesh { Size = new Vector2(flagH * 0.55f, flagH * 0.55f) },
                MaterialOverride = iconMat,
                Position         = new Vector3(poleX + flagW * 0.5f,
                                               poleLocalBot + poleH - flagH * 0.5f, 0.008f),
            };
            AddChild(iconInst);

            // ── Barra HP — fondo ──────────────────────────────────
            var bgMat = new StandardMaterial3D
            {
                AlbedoColor        = new Color(0.12f, 0.12f, 0.16f),
                Roughness          = 0.90f,
                BillboardMode      = BaseMaterial3D.BillboardModeEnum.Enabled,
                BillboardKeepScale = true,
            };
            var bgInst = new MeshInstance3D
            {
                Mesh             = new QuadMesh { Size = new Vector2(barW, barH) },
                MaterialOverride = bgMat,
                Position         = new Vector3(poleX, barY, 0f),
            };
            AddChild(bgInst);

            // ── Barra HP — frente (escala dinámica según HP) ──────
            _hpBarMat = new StandardMaterial3D
            {
                AlbedoColor        = new Color(0.18f, 0.82f, 0.18f),
                Roughness          = 0.70f,
                BillboardMode      = BaseMaterial3D.BillboardModeEnum.Enabled,
                BillboardKeepScale = true,
            };
            _hpBarFg = new MeshInstance3D
            {
                Mesh             = new QuadMesh { Size = new Vector2(barW * 0.96f, barH * 0.58f) },
                MaterialOverride = _hpBarMat,
                Position         = new Vector3(poleX, barY, 0.008f),
            };
            AddChild(_hpBarFg);
        }

        // ================================================================
        //  NIVEL 1: MODELO GLB
        // ================================================================

        /// <summary>
        /// Intenta cargar res://assets/units/&lt;UnitType&gt;.glb.
        /// Ajusta escala, posición y tiñe con el color de civilización.
        /// </summary>
        private bool TryLoadGlb()
        {
            string path = $"res://assets/units/{UnitType}.glb";
            if (!ResourceLoader.Exists(path))
            {
                GD.Print($"[Unit] GLB no encontrado: {path}");
                return false;
            }

            try
            {
                var scene = GD.Load<PackedScene>(path);
                if (scene == null)
                {
                    GD.PrintErr($"[Unit] GD.Load retornó null para {path}");
                    return false;
                }

                // Instantiate() sin tipo para evitar ClassCastException con roots no Node3D
                var node = scene.Instantiate();
                if (node is not Node3D inst3d)
                {
                    // El root es Node genérico — envolvemos en un Node3D container
                    var wrap = new Node3D();
                    wrap.AddChild(node);
                    inst3d = wrap;
                }

                inst3d.Scale    = new Vector3(0.55f, 0.55f, 0.55f);
                inst3d.Position = new Vector3(0f, FIGURE_BOT, 0f);
                TintModelWithCiv(inst3d);
                AddChild(inst3d);
                GD.Print($"[Unit] GLB cargado: {path}");
                return true;
            }
            catch (System.Exception ex)
            {
                GD.PrintErr($"[Unit] Error cargando {path}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Tiñe recursivamente todos los MeshInstance3D con el color de civilización.
        /// Mezcla un 30% de CivColor con el albedo original para identificar la civ.
        /// </summary>
        private void TintModelWithCiv(Node node)
        {
            if (node is MeshInstance3D mi && mi.Mesh != null)
            {
                int surfaces = mi.Mesh.GetSurfaceCount();
                for (int s = 0; s < surfaces; s++)
                {
                    // Obtener el material base (desde override o desde el mesh)
                    var mat = mi.GetSurfaceOverrideMaterial(s)
                              ?? mi.Mesh.SurfaceGetMaterial(s) as StandardMaterial3D;
                    if (mat is StandardMaterial3D std)
                    {
                        var tinted = (StandardMaterial3D)std.Duplicate();
                        tinted.AlbedoColor = std.AlbedoColor.Lerp(CivColor, 0.30f);
                        mi.SetSurfaceOverrideMaterial(s, tinted);
                    }
                    else if (mat == null)
                    {
                        // Sin material original — crear uno nuevo con el color civ
                        var newMat = new StandardMaterial3D
                        {
                            AlbedoColor = CivColor.Lightened(0.25f),
                            Roughness   = 0.65f,
                        };
                        mi.SetSurfaceOverrideMaterial(s, newMat);
                    }
                }
            }
            foreach (var child in node.GetChildren())
                TintModelWithCiv(child);
        }

        // ================================================================
        //  NIVEL 2: SPRITE PNG (billboard)
        // ================================================================

        /// <summary>
        /// Intenta cargar res://assets/units/&lt;UnitType&gt;.png como Sprite3D billboard.
        /// El sprite debe ser PNG con canal alfa (fondo transparente).
        /// Tamaño recomendado: 64×64 o 128×128 px.
        /// </summary>
        private bool TryLoadSprite()
        {
            string path = $"res://assets/units/{UnitType}.png";
            if (!ResourceLoader.Exists(path)) return false;

            var tex    = GD.Load<Texture2D>(path);
            var sprite = new Sprite3D
            {
                Texture               = tex,
                PixelSize             = 0.040f,       // 64px × 0.04 = 2.56 unidades de alto
                Billboard             = BaseMaterial3D.BillboardModeEnum.Enabled,
                Transparent           = true,
                AlphaScissorThreshold = 0.08f,
                NoDepthTest           = false,
                Position              = new Vector3(0f, FIGURE_BOT + 0.80f, 0f),
                Modulate              = Colors.White,
            };
            AddChild(sprite);

            // Mini barra de color de civ sobre el sprite (arriba a la izquierda)
            AddChild(MI(
                new CylinderMesh { TopRadius = 0.14f, BottomRadius = 0.14f,
                                   Height = 0.08f, RadialSegments = 8 },
                SolidMat(CivColor, roughness: 0.25f, metallic: 0.40f),
                new Vector3(0f, FIGURE_BOT + 2.0f, 0f)));

            return true;
        }

        // ================================================================
        //  NIVEL 3: HUMANOIDE PROCEDURAL (fallback sin assets externos)
        // ================================================================

        /// <summary>
        /// Figura humanoide completa: piernas separadas, torso, brazos, cabeza
        /// y equipo diferenciado por tipo de unidad.
        ///
        /// Alturas (sobre FIGURE_BOT = 0.10):
        ///   Piernas  0.00 – 0.42  (cy = 0.21)
        ///   Torso    0.42 – 0.78  (cy = 0.60)
        ///   Cuello   0.78 – 0.84
        ///   Cabeza   0.84 – 1.09  (esfera r=0.152, cy ≈ 0.99)
        ///   Equipo   encima de la cabeza / en las manos
        /// </summary>
        private void BuildHumanoid()
        {
            // ─── Materiales base ────────────────────────────────────
            var skin    = SolidMat(new Color(0.90f, 0.76f, 0.60f), roughness: 0.80f);
            var civ     = SolidMat(CivColor,                        roughness: 0.50f, metallic: 0.10f);
            var civDark = SolidMat(CivColor.Darkened(0.28f),        roughness: 0.60f);
            var civLite = SolidMat(CivColor.Lightened(0.28f),       roughness: 0.30f, metallic: 0.22f);
            var metal   = SolidMat(new Color(0.62f, 0.64f, 0.68f),  roughness: 0.35f, metallic: 0.72f);
            var gold    = SolidMat(new Color(0.86f, 0.76f, 0.18f),  roughness: 0.28f, metallic: 0.68f);
            var dark    = SolidMat(new Color(0.12f, 0.12f, 0.15f),  roughness: 0.70f);

            float y0 = FIGURE_BOT;  // 0.10

            // ─── Piernas ────────────────────────────────────────────
            const float legH = 0.42f, legR = 0.072f;
            float legCY = y0 + legH * 0.5f;
            AddChild(MI(new CylinderMesh { TopRadius = legR,        BottomRadius = legR * 1.14f,
                            Height = legH, RadialSegments = 7 }, civDark, V(-0.092f, legCY)));
            AddChild(MI(new CylinderMesh { TopRadius = legR,        BottomRadius = legR * 1.14f,
                            Height = legH, RadialSegments = 7 }, civDark, V(+0.092f, legCY)));

            // Pies
            AddChild(MI(new BoxMesh { Size = new Vector3(0.11f, 0.052f, 0.17f) },
                        dark, V(-0.092f, y0 + 0.026f, 0.032f)));
            AddChild(MI(new BoxMesh { Size = new Vector3(0.11f, 0.052f, 0.17f) },
                        dark, V(+0.092f, y0 + 0.026f, 0.032f)));

            // ─── Torso ──────────────────────────────────────────────
            float torsoBot = y0 + legH;                 // 0.52
            const float torsoH = 0.36f;
            AddChild(MI(new CylinderMesh { TopRadius = 0.148f, BottomRadius = 0.178f,
                            Height = torsoH, RadialSegments = 8 },
                        civ, V(0f, torsoBot + torsoH * 0.5f)));

            // ─── Brazos ─────────────────────────────────────────────
            float armY = torsoBot + torsoH * 0.76f;     // altura de los hombros
            const float armH = 0.27f, armR = 0.054f;

            var lArm = MI(new CylinderMesh { TopRadius = armR * 0.82f, BottomRadius = armR,
                              Height = armH, RadialSegments = 6 }, civ, V(-0.228f, armY - 0.045f));
            lArm.RotationDegrees = new Vector3(0f, 0f, 28f);
            AddChild(lArm);

            var rArm = MI(new CylinderMesh { TopRadius = armR * 0.82f, BottomRadius = armR,
                              Height = armH, RadialSegments = 6 }, civ, V(+0.228f, armY - 0.045f));
            rArm.RotationDegrees = new Vector3(0f, 0f, -28f);
            AddChild(rArm);

            // ─── Cuello y cabeza ────────────────────────────────────
            float headBot = torsoBot + torsoH;          // 0.88
            const float neckH = 0.058f, headR = 0.150f;

            AddChild(MI(new CylinderMesh { TopRadius = 0.070f, BottomRadius = 0.080f,
                            Height = neckH, RadialSegments = 6 },
                        skin, V(0f, headBot + neckH * 0.5f)));

            float headCY = headBot + neckH + headR;     // ≈ 1.088
            AddChild(MI(new SphereMesh { Radius = headR, RadialSegments = 10, Rings = 7 },
                        skin, V(0f, headCY)));

            // Ojos — dos pequeñas esferas oscuras en el frontal de la cabeza
            var eyeMat = SolidMat(new Color(0.06f, 0.05f, 0.04f), roughness: 0.92f);
            float eyeFwd = headR * 0.74f;
            float eyeUp  = headCY + headR * 0.10f;
            AddChild(MI(new SphereMesh { Radius = 0.026f, RadialSegments = 5, Rings = 3 },
                        eyeMat, V(-0.058f, eyeUp, eyeFwd)));
            AddChild(MI(new SphereMesh { Radius = 0.026f, RadialSegments = 5, Rings = 3 },
                        eyeMat, V(+0.058f, eyeUp, eyeFwd)));
            // Nariz — pequeño triángulo/cono
            var noseNode = MI(new CylinderMesh { TopRadius = 0f, BottomRadius = 0.022f,
                                  Height = 0.038f, RadialSegments = 4 },
                               SolidMat(new Color(0.80f, 0.64f, 0.50f), roughness: 0.82f),
                               V(0f, headCY - headR * 0.06f, headR * 0.92f));
            noseNode.RotationDegrees = new Vector3(90f, 0f, 0f);
            AddChild(noseNode);

            // ─── Equipo diferenciado por tipo ───────────────────────
            AddUnitEquipment(headCY, headR, torsoBot, torsoH, armY, metal, gold, dark, civLite, skin, civDark);
        }

        /// <summary>Agrega casco/sombrero y arma según el tipo de unidad.</summary>
        private void AddUnitEquipment(
            float headCY, float headR,
            float torsoBot, float torsoH, float armY,
            StandardMaterial3D metal, StandardMaterial3D gold,
            StandardMaterial3D dark,  StandardMaterial3D civLite,
            StandardMaterial3D skin,  StandardMaterial3D civDark)
        {
            float headTop = headCY + headR;

            switch (UnitType)
            {
                // ── WARRIOR: casco metálico + espada ──────────────────
                case UnitType.Warrior:
                {
                    // Casco: ala → cuerpo → punta
                    AddChild(MI(new CylinderMesh { TopRadius = 0.09f, BottomRadius = 0.20f,
                                    Height = 0.055f, RadialSegments = 10 },
                                metal, V(0f, headCY + headR * 0.40f)));
                    AddChild(MI(new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.12f,
                                    Height = 0.13f, RadialSegments = 10 },
                                metal, V(0f, headCY + headR * 0.40f + 0.09f)));
                    AddChild(MI(new CylinderMesh { TopRadius = 0f,    BottomRadius = 0.055f,
                                    Height = 0.09f, RadialSegments = 8 },
                                metal, V(0f, headTop + 0.045f)));

                    // Espada en mano derecha
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.042f, 0.52f, 0.042f) },
                                metal, V(0.42f, torsoBot + torsoH * 0.32f)));
                    // Guarda de la espada
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.20f, 0.038f, 0.038f) },
                                gold, V(0.42f, torsoBot + torsoH * 0.58f)));
                    break;
                }

                // ── SETTLER: sombrero de ala ancha + mochila ──────────
                case UnitType.Settler:
                {
                    // Ala del sombrero
                    AddChild(MI(new CylinderMesh { TopRadius = 0.265f, BottomRadius = 0.265f,
                                    Height = 0.038f, RadialSegments = 12 },
                                dark, V(0f, headCY + headR * 0.35f)));
                    // Copa del sombrero
                    AddChild(MI(new CylinderMesh { TopRadius = 0.135f, BottomRadius = 0.135f,
                                    Height = 0.21f, RadialSegments = 10 },
                                dark, V(0f, headCY + headR * 0.35f + 0.125f)));
                    // Banda del sombrero
                    AddChild(MI(new CylinderMesh { TopRadius = 0.138f, BottomRadius = 0.138f,
                                    Height = 0.038f, RadialSegments = 10 },
                                gold, V(0f, headCY + headR * 0.35f + 0.038f)));

                    // Mochila (atrás del torso)
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.22f, 0.25f, 0.11f) },
                                SolidMat(new Color(0.52f, 0.36f, 0.20f), roughness: 0.82f),
                                V(0f, torsoBot + torsoH * 0.48f, -0.22f)));
                    break;
                }

                // ── SCOUT: cinta + pluma + capa ───────────────────────
                case UnitType.Scout:
                {
                    // Cinta en la cabeza (aro dorado fino)
                    AddChild(MI(new CylinderMesh { TopRadius = headR + 0.012f, BottomRadius = headR + 0.012f,
                                    Height = 0.036f, RadialSegments = 12 },
                                gold, V(0f, headCY - headR * 0.08f)));
                    // Pluma (cono inclinado hacia atrás)
                    var feather = MI(new CylinderMesh { TopRadius = 0f, BottomRadius = 0.028f,
                                         Height = 0.34f, RadialSegments = 5 },
                                     civLite, V(0f, headTop + 0.17f, -0.038f));
                    feather.RotationDegrees = new Vector3(-16f, 0f, 0f);
                    AddChild(feather);
                    // Capa: rectángulo fino detrás del torso
                    var cape = MI(new BoxMesh { Size = new Vector3(0.28f, 0.30f, 0.025f) },
                                  civLite, V(0f, torsoBot + torsoH * 0.42f, -0.19f));
                    AddChild(cape);
                    break;
                }

                // ── ARCHER: carcaj + arco ─────────────────────────────
                case UnitType.Archer:
                {
                    // Carcaj en la espalda
                    var quiver = MI(new CylinderMesh { TopRadius = 0.058f, BottomRadius = 0.068f,
                                        Height = 0.28f, RadialSegments = 7 },
                                    SolidMat(new Color(0.44f, 0.26f, 0.11f), roughness: 0.76f),
                                    V(0.12f, torsoBot + torsoH * 0.66f, -0.20f));
                    quiver.RotationDegrees = new Vector3(-14f, 0f, 0f);
                    AddChild(quiver);
                    // Flechas asomando
                    for (int f = -1; f <= 1; f++)
                    {
                        AddChild(MI(new CylinderMesh { TopRadius = 0f, BottomRadius = 0.016f,
                                        Height = 0.14f, RadialSegments = 4 },
                                    metal, V(0.12f + f * 0.028f, torsoBot + torsoH * 0.66f + 0.18f, -0.19f)));
                    }
                    // Arco (palo curvo aproximado: dos segmentos angulados)
                    var bowBot = MI(new BoxMesh { Size = new Vector3(0.032f, 0.28f, 0.032f) },
                                   SolidMat(new Color(0.50f, 0.34f, 0.12f), roughness: 0.72f),
                                   V(-0.35f, torsoBot + torsoH * 0.30f, 0.05f));
                    bowBot.RotationDegrees = new Vector3(0f, 0f, 10f);
                    AddChild(bowBot);
                    var bowTop = MI(new BoxMesh { Size = new Vector3(0.032f, 0.28f, 0.032f) },
                                   SolidMat(new Color(0.50f, 0.34f, 0.12f), roughness: 0.72f),
                                   V(-0.35f, torsoBot + torsoH * 0.64f, 0.05f));
                    bowTop.RotationDegrees = new Vector3(0f, 0f, -10f);
                    AddChild(bowTop);
                    // Cuerda del arco
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.010f, 0.50f, 0.010f) },
                                SolidMat(new Color(0.88f, 0.84f, 0.72f), roughness: 0.90f),
                                V(-0.30f, torsoBot + torsoH * 0.47f, 0.09f)));
                    break;
                }

                // ── LONGBOWMAN: capucha verde + arco largo ────────────
                case UnitType.Longbowman:
                {
                    var hoodGreen = SolidMat(new Color(0.18f, 0.42f, 0.12f), roughness: 0.78f);
                    // Capucha (cono sobre cabeza)
                    AddChild(MI(new CylinderMesh { TopRadius = 0f, BottomRadius = headR + 0.02f,
                                    Height = 0.30f, RadialSegments = 8 },
                                hoodGreen, V(0f, headTop + 0.06f)));
                    // Arco largo
                    var longbow = MI(new BoxMesh { Size = new Vector3(0.028f, 0.70f, 0.028f) },
                                     SolidMat(new Color(0.45f, 0.28f, 0.10f), roughness: 0.72f),
                                     V(-0.36f, torsoBot + torsoH * 0.46f, 0.05f));
                    longbow.RotationDegrees = new Vector3(0f, 0f, 5f);
                    AddChild(longbow);
                    // Cuerda
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.009f, 0.62f, 0.009f) },
                                SolidMat(new Color(0.88f, 0.84f, 0.72f), roughness: 0.90f),
                                V(-0.30f, torsoBot + torsoH * 0.46f, 0.10f)));
                    break;
                }

                // ── SWORDSMAN: escudo + espada corta + visera ─────────
                case UnitType.Swordsman:
                {
                    // Visera sobre casco
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.24f, 0.06f, 0.06f) },
                                metal, V(0f, headCY + headR * 0.08f, headR + 0.018f)));
                    // Escudo (izquierda)
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.08f, 0.30f, 0.20f) },
                                SolidMat(new Color(0.58f, 0.12f, 0.12f), roughness: 0.68f),
                                V(-0.34f, torsoBot + torsoH * 0.44f, 0.04f)));
                    // Espada corta (derecha)
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.04f, 0.36f, 0.04f) },
                                metal, V(0.37f, torsoBot + torsoH * 0.48f, 0.04f)));
                    // Guarda
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.16f, 0.036f, 0.036f) },
                                metal, V(0.37f, torsoBot + torsoH * 0.62f, 0.04f)));
                    break;
                }

                // ── KNIGHT: yelmo con penacho + lanza ─────────────────
                case UnitType.Knight:
                {
                    var plumeRed = SolidMat(new Color(0.82f, 0.10f, 0.10f), roughness: 0.85f);
                    // Yelmo completo (cubre cabeza entera)
                    AddChild(MI(new SphereMesh { Radius = headR + 0.018f, RadialSegments = 10, Rings = 5 },
                                metal, V(0f, headCY)));
                    // Visera
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.20f, 0.05f, 0.05f) },
                                metal, V(0f, headCY - headR * 0.12f, headR + 0.010f)));
                    // Penacho (cilindro rojo sobre yelmo)
                    var plume = MI(new CylinderMesh { TopRadius = 0.018f, BottomRadius = 0.04f,
                                        Height = 0.24f, RadialSegments = 5 },
                                   plumeRed, V(0f, headTop + 0.12f));
                    plume.RotationDegrees = new Vector3(-10f, 0f, 0f);
                    AddChild(plume);
                    // Lanza (palo largo + punta)
                    AddChild(MI(new CylinderMesh { TopRadius = 0.022f, BottomRadius = 0.022f,
                                    Height = 0.80f, RadialSegments = 5 },
                                SolidMat(new Color(0.50f, 0.34f, 0.14f), roughness: 0.75f),
                                V(0.35f, torsoBot + torsoH * 0.60f)));
                    AddChild(MI(new CylinderMesh { TopRadius = 0f, BottomRadius = 0.040f,
                                    Height = 0.14f, RadialSegments = 5 },
                                metal, V(0.35f, torsoBot + torsoH * 0.60f + 0.47f)));
                    break;
                }

                // ── BALLISTA: armazón de madera (no humanoide) ─────────
                case UnitType.Ballista:
                {
                    var wood = SolidMat(new Color(0.52f, 0.36f, 0.18f), roughness: 0.82f);
                    // Marco base
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.50f, 0.08f, 0.36f) },
                                wood, V(0f, torsoBot + 0.04f)));
                    // Patas delanteras
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.06f, 0.32f, 0.06f) },
                                wood, V(-0.20f, torsoBot - 0.12f, -0.14f)));
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.06f, 0.32f, 0.06f) },
                                wood, V( 0.20f, torsoBot - 0.12f, -0.14f)));
                    // Patas traseras
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.06f, 0.32f, 0.06f) },
                                wood, V(-0.20f, torsoBot - 0.12f,  0.14f)));
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.06f, 0.32f, 0.06f) },
                                wood, V( 0.20f, torsoBot - 0.12f,  0.14f)));
                    // Arco de torsión (horizontal)
                    AddChild(MI(new CylinderMesh { TopRadius = 0.028f, BottomRadius = 0.028f,
                                    Height = 0.42f, RadialSegments = 6 },
                                metal, V(0f, torsoBot + 0.22f)));
                    // Proyectil (cilindro apuntando al frente)
                    var bolt = MI(new CylinderMesh { TopRadius = 0.018f, BottomRadius = 0.026f,
                                       Height = 0.28f, RadialSegments = 5 },
                                  metal, V(0f, torsoBot + 0.22f, -0.15f));
                    bolt.RotationDegrees = new Vector3(90f, 0f, 0f);
                    AddChild(bolt);
                    break;
                }

                // ── LONGSWORDSMAN: armadura gótica + espadón ──────────
                case UnitType.Longswordsman:
                {
                    var plate = SolidMat(new Color(0.72f, 0.72f, 0.72f), roughness: 0.38f, metallic: 0.55f);
                    // Yelmo gótico (cubre cabeza + cresta)
                    AddChild(MI(new SphereMesh { Radius = headR + 0.025f, RadialSegments = 10, Rings = 5 },
                                plate, V(0f, headCY)));
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.06f, 0.22f, 0.04f) },
                                plate, V(0f, headTop + 0.11f)));  // cresta
                    // Hombreras (pauldrons)
                    AddChild(MI(new SphereMesh { Radius = 0.12f, RadialSegments = 8, Rings = 4 },
                                plate, V(-0.28f, torsoBot + torsoH * 0.82f)));
                    AddChild(MI(new SphereMesh { Radius = 0.12f, RadialSegments = 8, Rings = 4 },
                                plate, V( 0.28f, torsoBot + torsoH * 0.82f)));
                    // Espadón (grande, a dos manos)
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.05f, 0.70f, 0.05f) },
                                metal, V(0.38f, torsoBot + torsoH * 0.38f)));
                    // Guarda cruzada
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.26f, 0.040f, 0.040f) },
                                metal, V(0.38f, torsoBot + torsoH * 0.62f)));
                    break;
                }

                // ── MUSKETMAN: tricornio + mosquete ───────────────────
                case UnitType.Musketman:
                {
                    var tricornMat = SolidMat(new Color(0.12f, 0.10f, 0.08f), roughness: 0.72f);
                    // Ala trasera del tricornio
                    AddChild(MI(new CylinderMesh { TopRadius = 0.24f, BottomRadius = 0.24f,
                                    Height = 0.035f, RadialSegments = 3 },
                                tricornMat, V(0f, headCY + headR * 0.28f)));
                    // Copa del sombrero
                    AddChild(MI(new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.15f,
                                    Height = 0.18f, RadialSegments = 8 },
                                tricornMat, V(0f, headCY + headR * 0.28f + 0.11f)));
                    // Mosquete (cañón largo)
                    var musket = MI(new CylinderMesh { TopRadius = 0.020f, BottomRadius = 0.026f,
                                        Height = 0.74f, RadialSegments = 5 },
                                    SolidMat(new Color(0.42f, 0.28f, 0.12f), roughness: 0.78f),
                                    V(0.36f, torsoBot + torsoH * 0.52f));
                    musket.RotationDegrees = new Vector3(14f, 0f, 0f);
                    AddChild(musket);
                    // Cañón de metal
                    var barrel = MI(new CylinderMesh { TopRadius = 0.018f, BottomRadius = 0.018f,
                                        Height = 0.38f, RadialSegments = 5 },
                                    metal, V(0.36f, torsoBot + torsoH * 0.52f + 0.18f));
                    barrel.RotationDegrees = new Vector3(14f, 0f, 0f);
                    AddChild(barrel);
                    break;
                }

                // ── WORKER: casco amarillo + pala ─────────────────────
                case UnitType.Worker:
                default:
                {
                    var yellow = SolidMat(new Color(0.94f, 0.79f, 0.08f), roughness: 0.52f);
                    // Casco: ala + copa redondeada
                    AddChild(MI(new CylinderMesh { TopRadius = 0.188f, BottomRadius = 0.188f,
                                    Height = 0.038f, RadialSegments = 10 },
                                yellow, V(0f, headCY - headR * 0.18f)));
                    AddChild(MI(new SphereMesh { Radius = 0.148f, RadialSegments = 10, Rings = 5 },
                                yellow, V(0f, headCY - headR * 0.18f + 0.04f)));

                    // Palo de la pala (madera)
                    AddChild(MI(new CylinderMesh { TopRadius = 0.032f, BottomRadius = 0.032f,
                                    Height = 0.58f, RadialSegments = 5 },
                                SolidMat(new Color(0.52f, 0.35f, 0.18f), roughness: 0.78f),
                                V(0.40f, torsoBot + torsoH * 0.22f)));
                    // Hoja de la pala
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.16f, 0.18f, 0.040f) },
                                metal, V(0.40f, torsoBot - 0.09f)));
                    break;
                }
            }
        }

        // ================================================================
        //  LABEL
        // ================================================================

        private void UpdateLabel()
        {
            if (_label == null) return;
            string dots = "";
            for (int i = 0; i < MaxMovement; i++)
                dots += i < RemainingMovement ? "●" : "○";
            string fort  = IsFortified  ? "🛡 " : "";
            string vet   = IsVeteran    ? "★ " : "";
            string skip  = (!IsReadyForTurn && RemainingMovement > 0) ? "⚡" : "";
            string wp    = HasWaypoint  ? "→ " : "";
            var    stats = UnitTypeData.GetStats(UnitType);
            _label.Text     = _selected
                ? $"{fort}{vet}{wp}{stats.DisplayName}\n{dots}"
                : $"{fort}{vet}{wp}{skip}{dots}";
            _label.FontSize = _selected ? 44 : 40;
        }

        // ================================================================
        //  HELPERS
        // ================================================================

        private static MeshInstance3D MI(Mesh mesh, StandardMaterial3D mat, Vector3 pos)
            => new() { Mesh = mesh, MaterialOverride = mat, Position = pos };

        private static Vector3 V(float x, float y, float z = 0f) => new(x, y, z);

        private static StandardMaterial3D SolidMat(Color color,
                                                    float roughness = 0.65f,
                                                    float metallic  = 0.05f)
            => new() { AlbedoColor = color, Roughness = roughness, Metallic = metallic };

        private static StandardMaterial3D UnlitMat(Color color, bool alpha = false)
        {
            var m = new StandardMaterial3D
            {
                AlbedoColor = color,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            };
            if (alpha) m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            return m;
        }
    }
}
