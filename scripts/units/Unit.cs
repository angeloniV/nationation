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
        private const float FIGURE_BOT = 0.10f;   // donde empieza la figura sobre el tile
        private const float TOKEN_TOP  = 1.48f;   // local-Y del tope del cuerpo del token

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

        /// <summary>
        /// Restaura el estado de combate de una unidad cargada desde un guardado.
        /// Llamar inmediatamente después de PlaceAt(), antes del primer frame.
        /// </summary>
        public void RestoreFromSave(float movesLeft, int currentHP, bool isVeteran, bool isFortified)
        {
            // Movimiento — respetar el máximo calculado en _Ready()
            RemainingMovement = Mathf.Clamp(movesLeft, 0f, MaxMovement);

            // HP — setear directamente sin disparar evento Died (la unidad existe)
            CurrentHP = Mathf.Clamp(currentHP, 1, MaxHP);
            UpdateHPBar();

            if (isVeteran)   MakeVeteran();
            if (isFortified) Fortify();

            UpdateLabel();
        }

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

        private const float UnitScale = 2.5f;   // factor de escala global de la figura

        private void BuildVisuals()
        {
            // Escalar la figura entera para que sea más visible en el mapa
            Scale = new Vector3(UnitScale, UnitScale, UnitScale);

            // ── Figura principal (GLB > PNG > token de ajedrez) ─────────────
            if (!TryLoadGlb())
                if (!TryLoadSprite())
                    BuildToken();

            // ── Label de movimiento ───────────────────────────────────
            _label = new Label3D
            {
                Text                  = "",
                FontSize              = 46,
                PixelSize             = 0.0065f,
                Billboard             = BaseMaterial3D.BillboardModeEnum.Enabled,
                AlphaScissorThreshold = 0.10f,
                Modulate              = Colors.White,
                Position              = new Vector3(0f, TOKEN_TOP + 0.12f, 0f),
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
                Position    = new Vector3(0f, TOKEN_TOP, 0f),
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
            const float poleLocalBot = TOKEN_TOP + 0.28f;   // local-Y donde empieza el asta
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
                    // Obtener el material sin el bug de precedencia del cast:
                    // GetSurfaceOverrideMaterial y SurfaceGetMaterial devuelven Material (base class)
                    var rawMat = mi.GetSurfaceOverrideMaterial(s)
                                 ?? mi.Mesh.SurfaceGetMaterial(s);

                    if (rawMat is StandardMaterial3D std)
                    {
                        var tinted = (StandardMaterial3D)std.Duplicate();
                        tinted.AlbedoColor = std.AlbedoColor.Lerp(CivColor, 0.30f);
                        // Preservar VertexColorUseAsAlbedo original (modelos Kenney usan vertex colors)
                        mi.SetSurfaceOverrideMaterial(s, tinted);
                    }
                    else
                    {
                        // Material null, ORM u otro tipo — crear StandardMaterial3D tintado
                        // Intentar preservar el color base si hay albedo en el material original
                        Color baseColor = rawMat is BaseMaterial3D bm
                            ? bm.Get("albedo_color").As<Color>()
                            : Colors.White;
                        var newMat = new StandardMaterial3D
                        {
                            AlbedoColor = baseColor.Lerp(CivColor, 0.30f),
                            Roughness   = 0.70f,
                            Metallic    = 0.05f,
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
        //  NIVEL 3: TOKEN DE AJEDREZ (fallback sin assets externos)
        // ================================================================

        /// <summary>
        /// Pieza de ajedrez estilizada: fuste cónico + cintura + esfera + símbolo por tipo.
        /// Sin extremidades sueltas — cuerpo unificado imposible de deformar.
        ///
        /// Alturas sobre FIGURE_BOT = 0.10:
        ///   Fuste   0.00 – 0.32  (CylinderMesh top=0.13 bot=0.22)
        ///   Cintura 0.32 – 0.44  (CylinderMesh top=0.10 bot=0.12)
        ///   Esfera  center = 0.61  (r = 0.17)
        ///   Símbolo desde 0.78 hacia arriba
        /// </summary>
        private void BuildToken()
        {
            var civ   = SolidMat(CivColor,                        roughness: 0.35f, metallic: 0.15f);
            var civD  = SolidMat(CivColor.Darkened(0.28f),        roughness: 0.50f);
            var civL  = SolidMat(CivColor.Lightened(0.30f),       roughness: 0.25f, metallic: 0.28f);
            var metal = SolidMat(new Color(0.70f, 0.72f, 0.76f),  roughness: 0.28f, metallic: 0.82f);
            var dark  = SolidMat(new Color(0.10f, 0.10f, 0.12f),  roughness: 0.75f);

            float y0 = FIGURE_BOT;

            // ── Fuste inferior (cónico, estilo peón de ajedrez) ──────
            AddChild(MI(new CylinderMesh { TopRadius    = 0.24f, BottomRadius = 0.40f,
                                           Height       = 0.58f, RadialSegments = 12 },
                        civ, V(0f, y0 + 0.29f)));

            // ── Cintura (estrangulación) ──────────────────────────────
            AddChild(MI(new CylinderMesh { TopRadius    = 0.18f, BottomRadius = 0.22f,
                                           Height       = 0.20f, RadialSegments = 10 },
                        civD, V(0f, y0 + 0.68f)));

            // ── Esfera superior ───────────────────────────────────────
            AddChild(MI(new SphereMesh { Radius = 0.30f, RadialSegments = 12, Rings = 8 },
                        civ, V(0f, y0 + 1.18f)));

            // ── Símbolo del tipo de unidad (icono compacto sobre el cuerpo)
            AddTokenSymbol(y0 + TOKEN_TOP, metal, civL, dark);
        }

        /// <summary>
        /// Icono compacto sobre el cuerpo del token, diferenciado por tipo.
        /// Todos los elementos son auto-contenidos — sin piezas sueltas fuera del volumen del peón.
        /// </summary>
        private void AddTokenSymbol(float topY,
                                     StandardMaterial3D metal,
                                     StandardMaterial3D civL,
                                     StandardMaterial3D dark)
        {
            switch (UnitType)
            {
                case UnitType.Warrior:
                case UnitType.Swordsman:
                case UnitType.Longswordsman:
                {
                    // Espada: hoja vertical + guarda cruzada
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.11f, 0.65f, 0.11f) },
                                metal, V(0f, topY + 0.32f)));
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.43f, 0.09f, 0.09f) },
                                metal, V(0f, topY + 0.23f)));
                    break;
                }
                case UnitType.Settler:
                {
                    // Casita: cuerpo + tejado piramidal
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.47f, 0.32f, 0.40f) },
                                civL, V(0f, topY + 0.16f)));
                    AddChild(MI(new CylinderMesh { TopRadius=0f, BottomRadius=0.31f,
                                    Height=0.29f, RadialSegments=4 },
                                dark, V(0f, topY + 0.47f)));
                    break;
                }
                case UnitType.Scout:
                {
                    // Flecha apuntando arriba
                    AddChild(MI(new CylinderMesh { TopRadius=0f, BottomRadius=0.20f,
                                    Height=0.47f, RadialSegments=4 },
                                civL, V(0f, topY + 0.23f)));
                    break;
                }
                case UnitType.Archer:
                case UnitType.Longbowman:
                {
                    // Arco: dos brazos angulados + cuerda
                    var bL = MI(new BoxMesh { Size = new Vector3(0.09f, 0.43f, 0.09f) },
                                 metal, V(-0.18f, topY + 0.22f));
                    bL.RotationDegrees = new Vector3(0f, 0f, -14f);
                    AddChild(bL);
                    var bR = MI(new BoxMesh { Size = new Vector3(0.09f, 0.43f, 0.09f) },
                                 metal, V(+0.18f, topY + 0.22f));
                    bR.RotationDegrees = new Vector3(0f, 0f, +14f);
                    AddChild(bR);
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.014f, 0.58f, 0.014f) },
                                 SolidMat(new Color(0.90f, 0.86f, 0.74f), roughness: 0.90f),
                                 V(0.07f, topY + 0.22f)));
                    break;
                }
                case UnitType.Knight:
                {
                    // Penacho: tres conos rojos
                    var red = SolidMat(new Color(0.82f, 0.10f, 0.10f), roughness: 0.82f);
                    for (int p = -1; p <= 1; p++)
                        AddChild(MI(new CylinderMesh { TopRadius=0f, BottomRadius=0.09f,
                                        Height=0.45f, RadialSegments=5 },
                                     red, V(p * 0.14f, topY + 0.22f)));
                    break;
                }
                case UnitType.Ballista:
                {
                    // Cañón horizontal + plataforma
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.54f, 0.11f, 0.40f) },
                                 SolidMat(new Color(0.50f, 0.34f, 0.18f), roughness: 0.82f),
                                 V(0f, topY + 0.05f)));
                    var barrel = MI(new CylinderMesh { TopRadius=0.07f, BottomRadius=0.11f,
                                        Height=0.50f, RadialSegments=8 },
                                     metal, V(0f, topY + 0.09f, 0.18f));
                    barrel.RotationDegrees = new Vector3(90f, 0f, 0f);
                    AddChild(barrel);
                    break;
                }
                case UnitType.Musketman:
                {
                    // Mosquete diagonal
                    var musket = MI(new BoxMesh { Size = new Vector3(0.07f, 0.72f, 0.07f) },
                                     SolidMat(new Color(0.44f, 0.30f, 0.14f), roughness: 0.78f),
                                     V(0.14f, topY + 0.36f));
                    musket.RotationDegrees = new Vector3(0f, 0f, 14f);
                    AddChild(musket);
                    break;
                }
                case UnitType.Worker:
                {
                    // Pala: mango + hoja
                    AddChild(MI(new CylinderMesh { TopRadius=0.05f, BottomRadius=0.06f,
                                    Height=0.61f, RadialSegments=5 },
                                 SolidMat(new Color(0.52f, 0.35f, 0.18f), roughness: 0.80f),
                                 V(0f, topY + 0.31f)));
                    AddChild(MI(new BoxMesh { Size = new Vector3(0.29f, 0.25f, 0.07f) },
                                 metal, V(0f, topY + 0.67f)));
                    break;
                }
                default:
                {
                    // Genérico: esfera pequeña en color civ claro
                    AddChild(MI(new SphereMesh { Radius=0.20f, RadialSegments=8, Rings=5 },
                                 civL, V(0f, topY + 0.20f)));
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
