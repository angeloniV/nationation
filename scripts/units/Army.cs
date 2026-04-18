using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Natiolation.Map;

namespace Natiolation.Units
{
    /// <summary>
    /// Ejército: contenedor de múltiples unidades usando el Patrón Composite.
    ///
    ///   • Movimiento  → velocidad mínima entre todas las unidades contenidas.
    ///   • Combate     → fuerza del "campeón" (unidad más fuerte).
    ///   • Visual      → estandarte GLB (assets/buildings/banner-{color}.glb)
    ///                   o estandarte procedural como fallback.
    ///   • Deploy      → una unidad puede abandonar el ejército hacia un hex adyacente vacío.
    /// </summary>
    public partial class Army : Node3D
    {
        // ── Posición en el mapa ──────────────────────────────────────────────
        public int   Q        { get; private set; }
        public int   R        { get; private set; }
        public Color CivColor { get; set; }
        public int   CivIndex { get; set; }

        // ── Estado de movimiento ─────────────────────────────────────────────
        public float RemainingMovement { get; private set; }
        public bool  IsMoving          { get; private set; }
        public bool  IsReadyForTurn    { get; private set; }

        // ── Estadísticas compuestas ──────────────────────────────────────────
        /// <summary>Movimiento del ejército = el menor movimiento máximo entre sus unidades.</summary>
        public int MaxMovement
            => _units.Count > 0 ? _units.Min(u => u.MaxMovement) : 2;

        /// <summary>Fuerza de combate = la unidad más fuerte del ejército (su "campeón").</summary>
        public int CombatStrength
            => _units.Count > 0
               ? _units.Max(u => UnitTypeData.GetStats(u.UnitType).CombatStrength)
               : 0;

        /// <summary>La unidad más fuerte: primera línea en combate.</summary>
        public Unit? Champion
            => _units.OrderByDescending(u => UnitTypeData.GetStats(u.UnitType).CombatStrength)
                     .FirstOrDefault();

        /// <summary>HP total del ejército = suma de HP de sus unidades.</summary>
        public int TotalHP    => _units.Sum(u => u.CurrentHP);
        public int TotalMaxHP => _units.Sum(u => u.MaxHP);

        // ── Unidades contenidas ──────────────────────────────────────────────
        private readonly List<Unit> _units = new();
        public IReadOnlyList<Unit> Units => _units;

        // ── C# Events (UI decoupling) ────────────────────────────────────────
        public event System.Action<Army>? CompositionChanged;  // al añadir/quitar unidades
        public event System.Action<Army>? Moved;               // tras moverse a un nuevo hex

        // ── Visual ───────────────────────────────────────────────────────────
        private bool        _selected;
        private float       _pulse;
        private OmniLight3D _light = null!;
        private Label3D     _label = null!;

        // ================================================================
        //  GODOT
        // ================================================================

        public override void _Ready()
        {
            RemainingMovement = MaxMovement;
            BuildVisuals();
        }

        public override void _Process(double delta)
        {
            if (!_selected) return;
            _pulse = (_pulse + (float)delta * 3f) % Mathf.Tau;
            float p = (Mathf.Sin(_pulse) + 1f) * 0.5f;
            _light.LightEnergy = 0.6f + p * 1.2f;
            _light.OmniRange   = 3.0f + p * 2.0f;
        }

        // ================================================================
        //  COMPOSICIÓN
        // ================================================================

        /// <summary>Añade una unidad al ejército, ocultando su modelo 3D individual.</summary>
        public void AddUnit(Unit unit)
        {
            if (_units.Contains(unit)) return;
            _units.Add(unit);
            unit.Visible = false;
            unit.SetLogicalPosition(Q, R);
            CompositionChanged?.Invoke(this);
            UpdateLabel();
        }

        /// <summary>
        /// Elimina una unidad del ejército, restaurando su modelo 3D.
        /// El llamador es responsable de posicionar la unidad en el mapa.
        /// </summary>
        public bool RemoveUnit(Unit unit)
        {
            if (!_units.Remove(unit)) return false;
            unit.Visible = true;
            CompositionChanged?.Invoke(this);
            UpdateLabel();
            return true;
        }

        /// <summary>Número de unidades en el ejército.</summary>
        public int Count => _units.Count;

        // ================================================================
        //  POSICIÓN Y MOVIMIENTO
        // ================================================================

        /// <summary>Coloca el ejército en un hex, sincronizando posición lógica de las unidades.</summary>
        public void PlaceAt(int q, int r, float tileHeight)
        {
            Q = q; R = r;
            Position = HexTile3D.AxialToWorld(q, r)
                     + new Vector3(0f, tileHeight + HexTile3D.TokenHover, 0f);
            foreach (var u in _units)
                u.SetLogicalPosition(q, r);
        }

        public void Select(bool value)
        {
            _selected          = value;
            _pulse             = 0f;
            _light.Visible     = value;
            _light.LightEnergy = 0.6f;
        }

        public void ResetMovement()
        {
            RemainingMovement = MaxMovement;
            IsReadyForTurn    = false;
            UpdateLabel();
        }

        public void ConsumeMovement(float cost)
        {
            RemainingMovement = Mathf.Max(0f, RemainingMovement - cost);
            IsReadyForTurn    = true;
            UpdateLabel();
        }

        public void ConsumeAllMovement()
        {
            RemainingMovement = 0;
            IsReadyForTurn    = true;
            UpdateLabel();
        }

        public void SkipTurn()
        {
            RemainingMovement = 0;
            IsReadyForTurn    = true;
            UpdateLabel();
        }

        /// <summary>
        /// Mueve el ejército hex a hex usando Tween, consumiendo puntos de movimiento.
        /// Actualiza Q/R y la posición lógica de todas las unidades contenidas.
        /// </summary>
        public async Task MoveTo(List<HexCoord> path, MapManager map)
        {
            if (IsMoving || path.Count < 2) return;
            IsMoving = true;

            for (int i = 1; i < path.Count; i++)
            {
                if (!IsInstanceValid(this)) break;
                var step  = path[i];
                float cost = map.GetEffectiveCost(step.Q, step.R);
                if (RemainingMovement < cost) break;

                ConsumeMovement(cost);
                Q = step.Q; R = step.R;
                foreach (var u in _units)
                    u.SetLogicalPosition(Q, R);

                float destH = map.GetTileHeight(step.Q, step.R);
                var   dest  = HexTile3D.AxialToWorld(step.Q, step.R)
                            + new Vector3(0f, destH + HexTile3D.TokenHover, 0f);

                var tween = CreateTween();
                tween.SetEase(Tween.EaseType.InOut);
                tween.SetTrans(Tween.TransitionType.Sine);
                tween.TweenProperty(this, "position", dest, 0.22f);
                await ToSignal(tween, Tween.SignalName.Finished);
                UpdateLabel();
                Moved?.Invoke(this);
            }

            IsMoving = false;
        }

        // ================================================================
        //  COMBATE — Daño al ejército
        // ================================================================

        /// <summary>
        /// Aplica daño al ejército: el campeón absorbe el golpe primero.
        /// Si el campeón muere, se elimina del ejército. Retorna true si el ejército sigue vivo.
        /// </summary>
        public bool TakeDamage(int amount)
        {
            var champ = Champion;
            if (champ == null) return false;

            champ.TakeDamage(amount);
            if (champ.CurrentHP <= 0)
                _units.Remove(champ);    // campeón muerto — no restaurar visual (ya destruido)

            CompositionChanged?.Invoke(this);
            UpdateLabel();
            return _units.Count > 0;
        }

        // ================================================================
        //  VISUALES
        // ================================================================

        private void BuildVisuals()
        {
            Scale = new Vector3(1.0f, 1.0f, 1.0f);

            // Intentar cargar GLB del estandarte
            string glbName = CivIndex == 0 ? "banner-blue" : "banner-red";
            string glbPath = $"res://assets/buildings/{glbName}.glb";

            if (ResourceLoader.Exists(glbPath))
            {
                try
                {
                    var scene = GD.Load<PackedScene>(glbPath);
                    if (scene != null)
                    {
                        var inst = scene.Instantiate<Node3D>();
                        inst.Scale    = Vector3.One * 0.55f;
                        inst.Position = Vector3.Zero;
                        AddChild(inst);
                        goto SkipProcedural;
                    }
                }
                catch { }
            }

            // ── Fallback: estandarte procedural ──────────────────────────────
            BuildProceduralBanner();

            SkipProcedural:

            // ── Sombra ────────────────────────────────────────────────────────
            float groundY = -(HexTile3D.TokenHover - 0.04f);
            AddChild(MeshInst(
                new CylinderMesh { TopRadius = 0.70f, BottomRadius = 0.70f,
                                   Height = 0.03f, RadialSegments = 12 },
                UnlitColor(new Color(0f, 0f, 0f, 0.35f)),
                new Vector3(0f, groundY, 0f)));

            // ── Label ─────────────────────────────────────────────────────────
            _label = new Label3D
            {
                Text                  = "",
                FontSize              = 46,
                PixelSize             = 0.0070f,
                Billboard             = BaseMaterial3D.BillboardModeEnum.Enabled,
                AlphaScissorThreshold = 0.10f,
                Modulate              = Colors.White,
                Position              = new Vector3(0f, 2.60f, 0f),
                NoDepthTest           = true,
                HorizontalAlignment   = HorizontalAlignment.Center,
            };
            AddChild(_label);

            // ── Luz de selección ──────────────────────────────────────────────
            _light = new OmniLight3D
            {
                LightColor  = CivColor.Lightened(0.20f),
                LightEnergy = 0.6f,
                OmniRange   = 3.5f,
                Visible     = false,
                Position    = new Vector3(0f, 1.80f, 0f),
            };
            AddChild(_light);
        }

        private void BuildProceduralBanner()
        {
            // Base del estandarte: disco de piedra
            AddChild(MeshInst(
                new CylinderMesh { TopRadius = 0.52f, BottomRadius = 0.60f,
                                   Height = 0.12f, RadialSegments = 12 },
                SolidColor(new Color(0.52f, 0.48f, 0.44f), roughness: 0.88f),
                new Vector3(0f, 0.06f, 0f)));

            // Aro metálico de base
            AddChild(MeshInst(
                new CylinderMesh { TopRadius = 0.50f, BottomRadius = 0.54f,
                                   Height = 0.14f, RadialSegments = 12 },
                SolidColor(CivColor.Lerp(new Color(0.8f, 0.8f, 0.8f), 0.4f),
                            roughness: 0.22f, metallic: 0.72f),
                new Vector3(0f, 0.07f, 0f)));

            // Asta principal
            float poleH = 2.20f;
            AddChild(MeshInst(
                new CylinderMesh { TopRadius = 0.030f, BottomRadius = 0.038f,
                                   Height = poleH, RadialSegments = 6 },
                SolidColor(new Color(0.76f, 0.70f, 0.60f), roughness: 0.28f, metallic: 0.60f),
                new Vector3(0f, 0.12f + poleH * 0.5f, 0f)));

            // Punta de la asta (lanza)
            AddChild(MeshInst(
                new CylinderMesh { TopRadius = 0f, BottomRadius = 0.040f,
                                   Height = 0.22f, RadialSegments = 4 },
                SolidColor(new Color(0.80f, 0.80f, 0.84f), roughness: 0.18f, metallic: 0.90f),
                new Vector3(0f, 0.12f + poleH + 0.11f, 0f)));

            float flagBotY = 0.12f + poleH - 0.72f;   // bandera ocupa los 0.72f superiores del asta

            // Bandera principal (civ color) — billboard
            var flagMat = new StandardMaterial3D
            {
                AlbedoColor        = CivColor,
                Roughness          = 0.65f,
                BillboardMode      = BaseMaterial3D.BillboardModeEnum.Enabled,
                BillboardKeepScale = true,
            };
            var flagInst = new MeshInstance3D
            {
                Mesh             = new QuadMesh { Size = new Vector2(0.80f, 0.55f) },
                MaterialOverride = flagMat,
                Position         = new Vector3(0.40f, flagBotY + 0.275f, 0f),
            };
            AddChild(flagInst);

            // Emblema central en la bandera (rombo blanco)
            var emblemMat = new StandardMaterial3D
            {
                AlbedoColor        = Colors.White.Lerp(CivColor, 0.18f),
                Roughness          = 0.60f,
                BillboardMode      = BaseMaterial3D.BillboardModeEnum.Enabled,
                BillboardKeepScale = true,
            };
            var emblemInst = new MeshInstance3D
            {
                Mesh             = new QuadMesh { Size = new Vector2(0.28f, 0.28f) },
                MaterialOverride = emblemMat,
                Position         = new Vector3(0.40f, flagBotY + 0.275f, 0.010f),
            };
            AddChild(emblemInst);

            // Anillas decorativas en el asta
            for (int i = 0; i < 3; i++)
            {
                float ry = 0.28f + i * 0.58f;
                AddChild(MeshInst(
                    new CylinderMesh { TopRadius = 0.044f, BottomRadius = 0.044f,
                                       Height = 0.045f, RadialSegments = 8 },
                    SolidColor(new Color(0.88f, 0.78f, 0.22f), roughness: 0.20f, metallic: 0.80f),
                    new Vector3(0f, 0.12f + ry, 0f)));
            }
        }

        private void UpdateLabel()
        {
            if (_label == null) return;
            _label.Text = _units.Count > 0
                ? $"⚔  ×{_units.Count}"
                : "";
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static MeshInstance3D MeshInst(Mesh mesh, StandardMaterial3D mat, Vector3 pos)
            => new() { Mesh = mesh, MaterialOverride = mat, Position = pos };

        private static StandardMaterial3D SolidColor(Color color,
                                                      float roughness = 0.65f,
                                                      float metallic  = 0.0f)
            => new() { AlbedoColor = color, Roughness = roughness, Metallic = metallic };

        private static StandardMaterial3D UnlitColor(Color color)
            => new()
            {
                AlbedoColor     = color,
                ShadingMode     = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency    = BaseMaterial3D.TransparencyEnum.Alpha,
            };
    }
}
