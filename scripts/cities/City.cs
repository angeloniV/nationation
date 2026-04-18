using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Natiolation.Map;
using Natiolation.Units;

namespace Natiolation.Cities
{
    /// <summary>
    /// Ciudad 3D fundada por un Colono.
    ///
    /// Escala: HexSize = 4.0.  La ciudad ocupa casi todo el tile.
    ///   • Platform disc         : radio 3.10  (aro exterior en color civico)
    ///   • Murallas hexagonales  : 6 paneles + 6 torres en vértices, radio 2.72
    ///   • Ayuntamiento central  : cuerpo 1.55×2.10×1.55 + tejado pirámide + 2 torres
    ///   • 4 casas alrededor
    ///   • Mástil + bandera del color de la civilización
    ///   • Label3D con el nombre, siempre visible (NoDepthTest)
    ///
    /// La ciudad no responde al fog of war — una vez vista, siempre se dibuja.
    /// </summary>
    public partial class City : Node3D
    {
        // ── Datos ────────────────────────────────────────────────────────
        public string CityName   { get; private set; } = "";
        public int    CivIndex   { get; private set; }
        public Color  CivColor   { get; private set; }
        public int    Q          { get; private set; }
        public int    R          { get; private set; }
        public int    Population { get; private set; } = 1;

        /// <summary>Radio de visión que aporta la ciudad al fog of war.</summary>
        public int SightRange => 3;

        // ── Economía ────────────────────────────────────────────────────
        public int           FoodStored       { get; private set; } = 0;
        public int           ProdStored       { get; private set; } = 0;
        public int           FoodPerTurn      { get; private set; } = 2;
        public int           ProdPerTurn      { get; private set; } = 1;
        public int           GoldPerTurn      { get; private set; } = 0;
        public int           SciencePerTurn   { get; private set; } = 1;
        public UnitType?     BuildingUnit     { get; private set; } = null;
        public BuildingType? BuildingBuilding { get; private set; } = null;

        // ── Edificios ────────────────────────────────────────────────────
        private readonly HashSet<BuildingType> _buildings = new();
        public IReadOnlySet<BuildingType> Buildings => _buildings;

        public int MaintenanceCost => _buildings.Sum(b => BuildingTypeData.GetStats(b).MaintenanceCost);

        /// <summary>Resultado de un turno de producción: unidad o edificio completado.</summary>
        public record struct ProductionResult(bool IsUnit, int TypeInt);

        /// <summary>Comida necesaria para crecer: crece con la población.</summary>
        public int FoodThreshold => 10 + Population * 5;

        /// <summary>Costo de producción del elemento en cola (0 si cola vacía).</summary>
        public int BuildCost
        {
            get
            {
                if (BuildingUnit.HasValue)     return UnitTypeData.GetStats(BuildingUnit.Value).ProductionCost;
                if (BuildingBuilding.HasValue) return BuildingTypeData.GetStats(BuildingBuilding.Value).ProductionCost;
                return 0;
            }
        }

        private Label3D _nameLabel       = null!;
        private Node3D  _buildingVisuals = null!;
        private int     _visualStage     = -1;

        /// <summary>
        /// Etapa visual:  0=campamento(pop 1)  1=aldea(2-3)  2=ciudad(4-6)  3=metrópolis(7+)
        /// </summary>
        private int CurrentStage => Population switch { 1 => 0, <= 3 => 1, <= 6 => 2, _ => 3 };

        // ================================================================
        //  INIT
        // ================================================================

        /// <summary>Posiciona la ciudad y construye sus visuales iniciales.</summary>
        public void Init(string name, int q, int r, int civIndex, Color civColor, float tileHeight)
        {
            CityName = name;
            Q        = q;
            R        = r;
            CivIndex = civIndex;
            CivColor = civColor;

            Position = HexTile3D.AxialToWorld(q, r) + new Vector3(0f, tileHeight, 0f);

            // Label persiste a través de los rebuilds
            _nameLabel = MakeLabel();
            AddChild(_nameLabel);

            // Contenedor para edificios (persiste entre rebuilds de etapa)
            _buildingVisuals = new Node3D();
            AddChild(_buildingVisuals);

            _visualStage = CurrentStage;
            BuildVisualsForStage(_visualStage);
        }

        /// <summary>
        /// Reconstruye los visuales si la población ha cambiado de etapa (aldea → ciudad, etc.).
        /// Llamado por CityManager tras cada crecimiento de población.
        /// </summary>
        public void TryRebuildVisuals()
        {
            int stage = CurrentStage;
            if (stage == _visualStage) return;
            _visualStage = stage;

            // Quitar todos los hijos visuales pero conservar el label y los edificios
            foreach (var child in GetChildren())
            {
                if (child == _nameLabel || child == _buildingVisuals) continue;
                RemoveChild(child);
                child.QueueFree();
            }

            BuildVisualsForStage(stage);
        }

        // ================================================================
        //  ECONOMÍA
        // ================================================================

        /// <summary>
        /// Calcula FoodPerTurn y ProdPerTurn para este turno basándose en los tiles trabajados.
        ///
        /// Regla: centro de ciudad siempre produce 2 comida + 1 producción.
        /// Trabaja hasta Population tiles adyacentes, ordenados por (comida + producción)
        /// descendente para maximizar el rendimiento.
        /// </summary>
        public void CalculateYields(MapManager map)
        {
            FoodPerTurn = 2;   // bonus base del centro
            ProdPerTurn = 1;
            GoldPerTurn = 0;

            // Vecinos adyacentes con sus rendimientos (+bono de río si aplica)
            var neighbors = new List<(int food, int prod, int gold)>();
            int[] dq = {  1, -1,  0,  0,  1, -1 };
            int[] dr = {  0,  0,  1, -1, -1,  1 };
            for (int i = 0; i < 6; i++)
            {
                int nq = Q + dq[i], nr = R + dr[i];
                var t = map.GetTileType(nq, nr);
                if (t == null) continue;

                int river = map.HasRiverEdge(nq, nr) ? 1 : 0;   // +1🌾 +1💰 por río adyacente
                var imp   = map.GetImprovement(nq, nr);
                int iFood = imp switch {
                    TileImprovement.Irrigation => 1,
                    TileImprovement.Farm       => 2,
                    _                          => 0,
                };
                int iProd = imp == TileImprovement.Mine ? 1 : 0;
                neighbors.Add((
                    t.Value.FoodYield() + river + iFood,
                    t.Value.ProductionYield() + iProd,
                    t.Value.GoldYield() + river
                ));
            }

            // Ordenar: mayor (comida+producción) primero; en empate, más comida primero
            neighbors.Sort((a, b) =>
            {
                int ta = a.food + a.prod, tb = b.food + b.prod;
                return ta != tb ? tb.CompareTo(ta) : b.food.CompareTo(a.food);
            });

            // Trabajar los mejores tiles según población
            int worked = Mathf.Min(Population, neighbors.Count);
            for (int i = 0; i < worked; i++)
            {
                FoodPerTurn += neighbors[i].food;
                ProdPerTurn += neighbors[i].prod;
                GoldPerTurn += neighbors[i].gold;
            }

            // ── Bonos de edificios ─────────────────────────────────────
            if (_buildings.Contains(BuildingType.Workshop))  ProdPerTurn += 2;
            if (_buildings.Contains(BuildingType.Forge))     ProdPerTurn += 1;
            if (_buildings.Contains(BuildingType.Temple))    GoldPerTurn += 1;
            if (_buildings.Contains(BuildingType.Market))    GoldPerTurn  = (int)(GoldPerTurn * 1.5f);
            if (_buildings.Contains(BuildingType.Harbor) && IsCoastal(map)) GoldPerTurn += 2;

            SciencePerTurn = 1;
            if (_buildings.Contains(BuildingType.Library))    SciencePerTurn += 3;
            if (_buildings.Contains(BuildingType.University)) SciencePerTurn += 5;
        }

        /// <summary>Establece (o cancela) la unidad en cola. Resetea el progreso.</summary>
        public void SetProductionQueue(UnitType? type)
        {
            BuildingUnit     = type;
            BuildingBuilding = null;
            ProdStored       = 0;
        }

        /// <summary>Establece (o cancela) el edificio en cola. Resetea el progreso.</summary>
        public void SetProductionQueue(BuildingType? type)
        {
            BuildingBuilding = type;
            BuildingUnit     = null;
            ProdStored       = 0;
        }

        /// <summary>
        /// Procesa un turno: acumula comida y producción.
        /// Retorna el resultado si algo se completó, null en caso contrario.
        /// </summary>
        public ProductionResult? ProcessTurn(MapManager map)
        {
            CalculateYields(map);

            // ── Crecimiento ────────────────────────────────────────────
            FoodStored += FoodPerTurn;
            if (FoodStored >= FoodThreshold)
            {
                int overflow = FoodStored - FoodThreshold;
                int keepBonus = _buildings.Contains(BuildingType.Granary) ? FoodThreshold / 2 : 0;
                FoodStored = keepBonus + overflow;
                Population++;
                GD.Print($"[City] '{CityName}' creció a población {Population}! " +
                         $"(umbral siguiente: {FoodThreshold})");
                CalculateYields(map);
            }

            // ── Producción ─────────────────────────────────────────────
            if (!BuildingUnit.HasValue && !BuildingBuilding.HasValue) return null;

            ProdStored += ProdPerTurn;

            if (BuildingUnit.HasValue)
            {
                int cost = UnitTypeData.GetStats(BuildingUnit.Value).ProductionCost;
                if (ProdStored >= cost)
                {
                    ProdStored = 0;
                    var finished = BuildingUnit.Value;
                    BuildingUnit = null;
                    GD.Print($"[City] '{CityName}' terminó de producir {finished}!");
                    return new ProductionResult(true, (int)finished);
                }
            }
            else if (BuildingBuilding.HasValue)
            {
                int cost = BuildingTypeData.GetStats(BuildingBuilding.Value).ProductionCost;
                if (ProdStored >= cost)
                {
                    ProdStored = 0;
                    var finished = BuildingBuilding.Value;
                    BuildingBuilding = null;
                    _buildings.Add(finished);
                    GD.Print($"[City] '{CityName}' construyó {finished}!");
                    return new ProductionResult(false, (int)finished);
                }
            }

            return null;
        }

        // ================================================================
        //  EDIFICIOS — VISUAL 3D INCREMENTAL
        // ================================================================

        /// <summary>Añade un widget 3D pequeño para el edificio recién construido.</summary>
        public void AddBuildingVisual(BuildingType type)
        {
            // Radio de colocación y ángulo fijo por tipo de edificio
            (float angle, float radius) placement = type switch
            {
                BuildingType.Granary    => (  0f, 1.85f),
                BuildingType.Library    => ( 60f, 1.85f),
                BuildingType.Barracks   => (120f, 1.85f),
                BuildingType.Market     => (180f, 1.85f),
                BuildingType.Temple     => (240f, 1.85f),
                BuildingType.Workshop   => (300f, 1.85f),
                BuildingType.Forge      => ( 30f, 2.20f),
                BuildingType.University => ( 90f, 1.85f),
                BuildingType.Harbor     => (270f, 1.85f),
                BuildingType.CityWalls  => (  0f, 0f),
                _                       => (  0f, 1.85f),
            };

            float rad = Mathf.DegToRad(placement.angle);
            float bx  = MathF.Cos(rad) * placement.radius;
            float bz  = MathF.Sin(rad) * placement.radius;
            float by  = 0.22f;   // encima de la plataforma

            var stone = Mat(new Color(0.64f, 0.60f, 0.52f), rough: 0.84f);
            var roof  = Mat(new Color(0.44f, 0.24f, 0.16f), rough: 0.80f);
            var metal = Mat(new Color(0.68f, 0.66f, 0.70f), rough: 0.30f, metal: 0.80f);

            switch (type)
            {
                case BuildingType.Granary:
                {
                    var n = new Node3D(); n.Position = V(bx, by, bz); _buildingVisuals.AddChild(n);
                    var mi = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius=0.30f, BottomRadius=0.34f, Height=0.50f, RadialSegments=10 }, MaterialOverride=stone }; n.AddChild(mi);
                    var cap = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius=0f, BottomRadius=0.36f, Height=0.22f, RadialSegments=10 }, MaterialOverride=roof, Position=V(0,0.36f) }; n.AddChild(cap);
                    break;
                }
                case BuildingType.Library:
                {
                    var n = new Node3D(); n.Position = V(bx, by, bz); _buildingVisuals.AddChild(n);
                    var body = new MeshInstance3D { Mesh = new BoxMesh { Size=new Vector3(0.52f,0.42f,0.38f) }, MaterialOverride=stone, Position=V(0,0.21f) }; n.AddChild(body);
                    var top  = new MeshInstance3D { Mesh = new BoxMesh { Size=new Vector3(0.56f,0.06f,0.42f) }, MaterialOverride=Mat(new Color(0.48f,0.44f,0.40f),rough:0.88f), Position=V(0,0.45f) }; n.AddChild(top);
                    break;
                }
                case BuildingType.Barracks:
                {
                    var n = new Node3D(); n.Position = V(bx, by, bz); _buildingVisuals.AddChild(n);
                    var body = new MeshInstance3D { Mesh = new BoxMesh { Size=new Vector3(0.56f,0.38f,0.42f) }, MaterialOverride=stone, Position=V(0,0.19f) }; n.AddChild(body);
                    // Almenitas
                    for (int bi = -1; bi <= 1; bi += 2)
                    {
                        var merlon = new MeshInstance3D { Mesh = new BoxMesh { Size=new Vector3(0.12f,0.14f,0.10f) }, MaterialOverride=stone, Position=V(bi*0.18f, 0.45f, 0f) }; n.AddChild(merlon);
                    }
                    break;
                }
                case BuildingType.Temple:
                {
                    var n = new Node3D(); n.Position = V(bx, by, bz); _buildingVisuals.AddChild(n);
                    // Pilar Kenney; fallback procedural
                    if (!TrySpawnGlb("res://assets/buildings/pillar-stone.glb",
                                     V(0f, 0f, 0f), 0.52f, 0f, n))
                    {
                        var pillar = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius=0.08f, BottomRadius=0.10f, Height=0.56f, RadialSegments=6 }, MaterialOverride=Mat(new Color(0.90f,0.88f,0.80f),rough:0.72f), Position=V(0,0.28f) }; n.AddChild(pillar);
                    }
                    var disc = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius=0.18f, BottomRadius=0.18f, Height=0.06f, RadialSegments=10 }, MaterialOverride=metal, Position=V(0,0.59f) }; n.AddChild(disc);
                    break;
                }
                case BuildingType.Workshop:
                {
                    var n = new Node3D(); n.Position = V(bx, by, bz); _buildingVisuals.AddChild(n);
                    var body = new MeshInstance3D { Mesh = new BoxMesh { Size=new Vector3(0.50f,0.36f,0.40f) }, MaterialOverride=stone, Position=V(0,0.18f) }; n.AddChild(body);
                    // Chimenea Kenney; fallback procedural
                    if (!TrySpawnGlb("res://assets/buildings/chimney.glb",
                                     V(0.18f, 0.36f, 0f), 0.38f, 0f, n))
                    {
                        var chimney = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius=0.06f, BottomRadius=0.07f, Height=0.26f, RadialSegments=6 }, MaterialOverride=stone, Position=V(0.18f,0.49f) }; n.AddChild(chimney);
                    }
                    break;
                }
                case BuildingType.Forge:
                {
                    var n = new Node3D(); n.Position = V(bx, by, bz); _buildingVisuals.AddChild(n);
                    var body = new MeshInstance3D { Mesh = new BoxMesh { Size=new Vector3(0.46f,0.34f,0.38f) }, MaterialOverride=Mat(new Color(0.38f,0.30f,0.26f),rough:0.90f), Position=V(0,0.17f) }; n.AddChild(body);
                    var glow = new MeshInstance3D { Mesh = new SphereMesh { Radius=0.10f }, MaterialOverride=Mat(new Color(1.00f,0.50f,0.10f),rough:0.20f), Position=V(0,0.20f,0.20f) }; n.AddChild(glow);
                    break;
                }
                case BuildingType.Harbor:
                {
                    var n = new Node3D(); n.Position = V(bx, by, bz); _buildingVisuals.AddChild(n);
                    var dock = new MeshInstance3D { Mesh = new BoxMesh { Size=new Vector3(0.60f,0.08f,0.44f) }, MaterialOverride=Mat(new Color(0.52f,0.36f,0.18f),rough:0.82f), Position=V(0,0.04f) }; n.AddChild(dock);
                    var mast = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius=0.030f, BottomRadius=0.030f, Height=0.44f, RadialSegments=5 }, MaterialOverride=stone, Position=V(0,0.26f) }; n.AddChild(mast);
                    break;
                }
                case BuildingType.University:
                {
                    var n = new Node3D(); n.Position = V(bx, by, bz); _buildingVisuals.AddChild(n);
                    var tower = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius=0.16f, BottomRadius=0.18f, Height=0.62f, RadialSegments=8 }, MaterialOverride=stone, Position=V(0,0.31f) }; n.AddChild(tower);
                    var spire = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius=0f, BottomRadius=0.20f, Height=0.20f, RadialSegments=8 }, MaterialOverride=roof, Position=V(0,0.72f) }; n.AddChild(spire);
                    break;
                }
            }
        }

        private bool IsCoastal(MapManager map)
        {
            int[] dq = {  1, -1,  0,  0,  1, -1 };
            int[] dr = {  0,  0,  1, -1, -1,  1 };
            for (int i = 0; i < 6; i++)
            {
                var t = map.GetTileType(Q + dq[i], R + dr[i]);
                if (t == TileType.Ocean || t == TileType.Coast) return true;
            }
            return false;
        }

        // ================================================================
        //  VISUALES — SISTEMA POR ETAPAS
        // ================================================================

        private Label3D MakeLabel() => new Label3D
        {
            Text                  = CityName,
            FontSize              = 56,
            PixelSize             = 0.030f,
            Billboard             = BaseMaterial3D.BillboardModeEnum.Enabled,
            AlphaScissorThreshold = 0.10f,
            NoDepthTest           = true,
            Modulate              = Colors.White,
            OutlineModulate       = new Color(0f, 0f, 0f, 0.92f),
            OutlineSize           = 8,
            Position              = V(0, 7.0f),
            HorizontalAlignment   = HorizontalAlignment.Center,
        };

        private void BuildVisualsForStage(int stage)
        {
            // ── Paleta de materiales ────────────────────────────────────────
            var stone   = Mat(new Color(0.64f, 0.60f, 0.52f), rough: 0.84f);
            var stoneD  = Mat(new Color(0.46f, 0.42f, 0.38f), rough: 0.88f);
            var stoneL  = Mat(new Color(0.76f, 0.72f, 0.64f), rough: 0.80f);
            var roofR   = Mat(new Color(0.48f, 0.22f, 0.14f), rough: 0.78f);
            var roofG   = Mat(new Color(0.28f, 0.48f, 0.24f), rough: 0.76f);
            var roofB   = Mat(new Color(0.22f, 0.30f, 0.52f), rough: 0.76f);
            var plaster = Mat(new Color(0.84f, 0.78f, 0.62f), rough: 0.86f);
            var plasterW= Mat(new Color(0.92f, 0.88f, 0.76f), rough: 0.82f);
            var dark    = Mat(new Color(0.10f, 0.07f, 0.06f), rough: 0.90f);
            var wood    = Mat(new Color(0.50f, 0.32f, 0.14f), rough: 0.82f);
            var metal   = Mat(new Color(0.68f, 0.66f, 0.70f), rough: 0.28f, metal: 0.82f);
            var grass   = Mat(new Color(0.28f, 0.54f, 0.22f), rough: 0.90f);
            var civ     = Mat(CivColor,                        rough: 0.44f, metal: 0.20f);
            var civLit  = Mat(CivColor.Lightened(0.30f),       rough: 0.34f, metal: 0.25f);
            var glowMat = new StandardMaterial3D { AlbedoColor = new Color(1.00f, 0.95f, 0.60f),
                              ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };

            // ── Plataforma con escalones ──────────────────────────────────────
            float pOuter = stage switch { 0 => 2.65f, 1 => 3.05f, _ => 3.30f };
            float pH     = stage switch { 0 => 0.20f, 1 => 0.22f, _ => 0.25f };

            // Aro exterior cívico
            AddMI(new CylinderMesh { TopRadius = pOuter, BottomRadius = pOuter + 0.04f,
                      Height = pH + 0.04f, RadialSegments = 6 }, civ, V(0, pH * 0.5f));

            // Escalones de piedra (stage 1+)
            if (stage >= 1)
            {
                AddMI(new CylinderMesh { TopRadius = pOuter - 0.18f, BottomRadius = pOuter - 0.16f,
                          Height = pH + 0.06f, RadialSegments = 6 }, stone, V(0, pH * 0.5f + 0.01f));
                // Disco interior de tierra/adoquín
                AddMI(new CylinderMesh { TopRadius = pOuter - 0.55f, BottomRadius = pOuter - 0.52f,
                          Height = pH + 0.08f, RadialSegments = 6 }, stoneL, V(0, pH * 0.5f + 0.02f));
            }
            // Plaza interna (suelo adoquinado)
            if (stage >= 2)
            {
                AddMI(new CylinderMesh { TopRadius = 1.15f, BottomRadius = 1.15f,
                          Height = 0.05f, RadialSegments = 8 },
                      stoneL, V(0, pH + 0.025f));
            }

            float base_ = pH;

            // ── Murallas ────────────────────────────────────────────────────
            BuildWallsStaged(stage, base_, stoneD, roofR);

            // ── Ayuntamiento ─────────────────────────────────────────────────
            float thTop = BuildTownHallStaged(stage, base_, stone, stoneD, roofR, dark, metal);

            // ── Casas (más variadas) ──────────────────────────────────────────
            int houseCount = stage == 0 ? 1 : stage == 1 ? 3 : 5;
            BuildHousesStaged(houseCount, base_, stone, stoneL, plaster, plasterW, roofR, roofG, roofB, dark, wood);

            // ── Bandera ──────────────────────────────────────────────────────
            BuildFlagStaged(stage, thTop, civ, civLit, metal);

            // ── Faroles de calle (stage 1+) ───────────────────────────────────
            if (stage >= 1) BuildLanterns(base_, metal, glowMat, stage >= 2 ? 4 : 2);

            // ── Pozo/fuente central (stage 1+) ───────────────────────────────
            if (stage >= 1) BuildWell(base_, stone, metal);

            // ── Jardines/parques (stage 2+) ───────────────────────────────────
            if (stage >= 2) BuildGardens(base_, grass, stoneL);

            // ── Mercado extra (metrópolis) ────────────────────────────────────
            if (stage >= 3) BuildMarket(base_, plaster, roofG);
        }

        // ─────────────────────────────────────────────────────────────────────

        private void BuildWallsStaged(int stage, float base_,
                                       StandardMaterial3D stone, StandardMaterial3D roofMat)
        {
            int   panelCount = stage == 0 ? 3 : 6;
            float wallH      = stage == 0 ? 0.50f : stage == 1 ? 0.68f : 0.90f;
            float wallR      = stage == 0 ? 2.15f : stage == 1 ? 2.52f : 2.72f;
            float panelW     = stage == 0 ? 2.48f : stage == 1 ? 2.90f : 3.15f;
            int   towerCount = stage == 0 ? 0 : stage == 1 ? 3 : 6;
            float towerH     = stage >= 2 ? 1.24f : 0.82f;
            float towerR     = stage >= 2 ? 0.42f : 0.30f;

            for (int ii = 0; ii < panelCount; ii++)
            {
                int i = (stage == 0) ? ii * 2 : ii;   // stage 0 salta caras alternadas

                float fAng = i * MathF.PI / 3f + MathF.PI / 6f;
                float fx   = MathF.Cos(fAng) * wallR;
                float fz   = MathF.Sin(fAng) * wallR;
                float ry   = Mathf.RadToDeg(fAng) + 90f;

                var panel = AddMI(new BoxMesh { Size = new Vector3(panelW, wallH, 0.28f) },
                                  stone, V(fx, base_ + wallH * 0.5f, fz));
                panel.RotationDegrees = new Vector3(0f, ry, 0f);

                if (stage >= 1)
                {
                    var cren = AddMI(new BoxMesh { Size = new Vector3(0.44f, 0.30f, 0.34f) },
                                     stone, V(fx, base_ + wallH + 0.15f, fz));
                    cren.RotationDegrees = new Vector3(0f, ry, 0f);
                }
            }

            for (int ii = 0; ii < towerCount; ii++)
            {
                int i = (stage == 1) ? ii * 2 : ii;   // stage 1: vértices alternados

                float vAng = i * MathF.PI / 3f;
                float vx   = MathF.Cos(vAng) * (wallR + 0.18f);
                float vz   = MathF.Sin(vAng) * (wallR + 0.18f);

                // Torre cuadrada — más angular, menos "globo"
                AddMI(new BoxMesh { Size = new Vector3(towerR * 2.2f, towerH, towerR * 2.2f) },
                      stone, V(vx, base_ + towerH * 0.5f, vz));
                // Tejadillo piramidal de 4 lados
                AddMI(new CylinderMesh { TopRadius = 0f, BottomRadius = towerR + 0.14f,
                          Height = towerH * 0.35f, RadialSegments = 4 },
                      roofMat, V(vx, base_ + towerH + towerH * 0.175f, vz));
            }
        }

        /// <summary>Construye el ayuntamiento y devuelve la Y del tope del tejado.</summary>
        private float BuildTownHallStaged(int stage, float base_,
                                           StandardMaterial3D stone, StandardMaterial3D stoneD,
                                           StandardMaterial3D roofMat, StandardMaterial3D dark,
                                           StandardMaterial3D metal)
        {
            float thW = stage == 0 ? 0.76f : stage == 1 ? 1.14f : 1.58f;
            float thH = stage == 0 ? 0.95f : stage == 1 ? 1.52f : 2.15f;
            float rfH = stage == 0 ? 0.48f : stage == 1 ? 0.76f : 1.15f;
            float rfR = stage == 0 ? 0.57f : stage == 1 ? 0.86f : 1.18f;

            // Cuerpo
            AddMI(new BoxMesh { Size = new Vector3(thW, thH, thW) }, stone, V(0, base_ + thH * 0.5f));

            // ── Techo del ayuntamiento — Kenney GLB primero ──────────────
            float roofTopY = base_ + thH;
            string thRoofGlb = stage >= 2
                ? "res://assets/buildings/roof-high-point.glb"
                : stage == 1
                    ? "res://assets/buildings/roof-high.glb"
                    : "res://assets/buildings/roof-point.glb";
            float thRoofScale = stage == 0 ? thW * 0.68f
                              : stage == 1 ? thW * 0.62f
                              :              thW * 0.58f;
            if (!TrySpawnGlb(thRoofGlb, V(0, roofTopY, 0), thRoofScale))
            {
                // Fallback: pirámide 4 lados
                AddMI(new CylinderMesh { TopRadius = 0f, BottomRadius = rfR,
                          Height = rfH, RadialSegments = 4 }, roofMat, V(0, roofTopY + rfH * 0.5f));
            }

            // Torres laterales (progresivas)
            if (stage >= 1)
            {
                float tR = stage >= 2 ? 0.44f : 0.32f;
                float tH = stage >= 2 ? 1.78f : 1.28f;
                float tX = thW * 0.5f + tR * 1.2f;

                // Torre izquierda — cuerpo cuadrado + tejado GLB
                AddMI(new BoxMesh { Size = new Vector3(tR * 2.1f, tH, tR * 2.1f) },
                      stoneD, V(-tX, base_ + tH * 0.5f));
                float towerRoofGlb_scale = tR * 1.8f;
                if (!TrySpawnGlb("res://assets/buildings/roof-point.glb",
                                 V(-tX, base_ + tH, 0), towerRoofGlb_scale))
                {
                    AddMI(new CylinderMesh { TopRadius = 0f, BottomRadius = tR + 0.16f,
                              Height = (tR + 0.14f) * 1.2f, RadialSegments = 4 },
                          roofMat, V(-tX, base_ + tH + (tR + 0.14f) * 0.6f));
                }

                if (stage >= 2)
                {
                    // Torre derecha
                    AddMI(new BoxMesh { Size = new Vector3(tR * 2.1f, tH, tR * 2.1f) },
                          stoneD, V(+tX, base_ + tH * 0.5f));
                    if (!TrySpawnGlb("res://assets/buildings/roof-point.glb",
                                     V(+tX, base_ + tH, 0), towerRoofGlb_scale))
                    {
                        AddMI(new CylinderMesh { TopRadius = 0f, BottomRadius = tR + 0.16f,
                                  Height = (tR + 0.14f) * 1.2f, RadialSegments = 4 },
                              roofMat, V(+tX, base_ + tH + (tR + 0.14f) * 0.6f));
                    }
                }
            }

            // Ventana + puerta (desde stage 1)
            if (stage >= 1)
            {
                float wz = thW * 0.5f + 0.01f;
                AddMI(new BoxMesh { Size = new Vector3(0.28f, 0.36f, 0.04f) }, stoneD, V(0, base_ + thH * 0.65f, wz));
                AddMI(new BoxMesh { Size = new Vector3(0.38f, 0.50f, 0.04f) }, dark,   V(0, base_ + thH * 0.25f, wz));
            }

            return base_ + thH + rfH;   // Y del tope del tejado
        }

        private void BuildHousesStaged(int count, float base_,
                                        StandardMaterial3D stone,   StandardMaterial3D stoneL,
                                        StandardMaterial3D plaster, StandardMaterial3D plasterW,
                                        StandardMaterial3D roofR,   StandardMaterial3D roofG,
                                        StandardMaterial3D roofB,   StandardMaterial3D dark,
                                        StandardMaterial3D wood)
        {
            // Hasta 5 casas — posición, escala, material de pared, tipo de tejado GLB, rotación
            (float x, float z, float s, StandardMaterial3D wall, string roofGlb, StandardMaterial3D roofFallback, int style)[] defs =
            {
                (-1.68f,  1.40f, 0.92f, plaster,  "res://assets/buildings/roof-gable.glb",     roofG, 0),
                ( 1.72f,  1.30f, 0.84f, stone,    "res://assets/buildings/roof.glb",            roofR, 1),
                (-1.70f, -1.36f, 0.88f, plasterW, "res://assets/buildings/roof-gable.glb",     roofR, 0),
                ( 1.65f, -1.25f, 0.78f, stoneL,   "res://assets/buildings/roof-high.glb",      roofB, 2),
                ( 0.20f,  1.95f, 0.70f, plaster,  "res://assets/buildings/roof-point.glb",     roofG, 1),
            };

            for (int i = 0; i < count; i++)
            {
                var (x, z, s, wall, roofGlb, roofFallback, style) = defs[i];
                float hw = 0.90f * s, hh = 1.06f * s, hd = 0.90f * s;

                // ── Paredes (BoxMesh procedural) ─────────────────────────
                AddMI(new BoxMesh { Size = new Vector3(hw, hh, hd) }, wall, V(x, base_ + hh * 0.5f, z));

                // Puerta
                float doorFace = z > 0 ? z - hd * 0.5f : z + hd * 0.5f;
                AddMI(new BoxMesh { Size = new Vector3(0.22f * s, 0.36f * s, 0.04f) },
                      dark, V(x, base_ + hh * 0.20f, doorFace));

                // ── Tejado — Kenney GLB primero, fallback procedural ─────
                float roofY   = base_ + hh;
                float roofRot = (z > 0) ? 0f : 180f;
                if (!TrySpawnGlb(roofGlb, V(x, roofY, z), s * 0.52f, roofRot))
                {
                    // Fallback procedural según estilo
                    if (style == 0)
                    {
                        AddMI(new CylinderMesh { TopRadius = 0f, BottomRadius = hw * 0.88f,
                                  Height = 0.64f * s, RadialSegments = 4 },
                              roofFallback, V(x, roofY + 0.32f * s, z));
                    }
                    else if (style == 1)
                    {
                        AddMI(new BoxMesh { Size = new Vector3(hw * 1.05f, 0.50f * s, hd * 0.38f) },
                              roofFallback, V(x, roofY + 0.25f * s, z));
                        AddMI(new CylinderMesh { TopRadius = 0.07f * s, BottomRadius = 0.08f * s,
                                  Height = 0.28f * s, RadialSegments = 5 },
                              dark, V(x + hw * 0.28f, roofY + 0.42f * s, z - hd * 0.10f));
                    }
                    else
                    {
                        AddMI(new BoxMesh { Size = new Vector3(hw * 1.04f, 0.10f, hd * 1.04f) },
                              roofFallback, V(x, roofY + 0.05f, z));
                        AddMI(new BoxMesh { Size = new Vector3(hw * 0.50f, 0.36f * s, hd * 0.50f) },
                              wall, V(x + hw * 0.20f, roofY + 0.18f * s, z));
                    }
                }
            }
        }

        private void BuildLanterns(float base_, StandardMaterial3D pole, StandardMaterial3D glow, int count)
        {
            // Faroles colocados en puntos fijos alrededor de la plaza
            (float x, float z)[] spots = {
                (-0.80f,  0.80f), ( 0.80f,  0.80f),
                ( 0.80f, -0.80f), (-0.80f, -0.80f),
            };
            for (int i = 0; i < count && i < spots.Length; i++)
            {
                var (lx, lz) = spots[i];
                float py = base_ + 0.02f;

                // Farol Kenney (Fantasy Town Kit)
                if (TrySpawnGlb("res://assets/buildings/lantern.glb", V(lx, py, lz), 0.46f)) continue;

                // Fallback procedural
                AddMI(new CylinderMesh { TopRadius = 0.04f, BottomRadius = 0.05f, Height = 0.80f, RadialSegments = 5 },
                      pole, V(lx, py + 0.40f, lz));
                AddMI(new BoxMesh { Size = new Vector3(0.22f, 0.04f, 0.04f) },
                      pole, V(lx + 0.11f, py + 0.72f, lz));
                AddMI(new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.08f, Height = 0.12f, RadialSegments = 6 },
                      pole, V(lx + 0.22f, py + 0.76f, lz));
                AddMI(new SphereMesh { Radius = 0.055f, RadialSegments = 6, Rings = 4 },
                      glow, V(lx + 0.22f, py + 0.72f, lz));
            }
        }

        private void BuildWell(float base_, StandardMaterial3D stone, StandardMaterial3D metal)
        {
            // Pozo central con aro de piedra y polea
            float wx = 1.35f, wz = -1.40f;   // posición del pozo (cerca de una casa)
            float py = base_ + 0.02f;

            // Fuente Kenney (Fantasy Town Kit)
            if (TrySpawnGlb("res://assets/buildings/fountain-round.glb", V(wx, py, wz), 0.50f)) return;

            var wood  = Mat(new Color(0.48f, 0.30f, 0.12f), rough: 0.82f);
            // Aro del pozo
            AddMI(new CylinderMesh { TopRadius = 0.25f, BottomRadius = 0.28f, Height = 0.28f, RadialSegments = 8 },
                  stone, V(wx, py + 0.14f, wz));
            // Cubierta de madera
            AddMI(new CylinderMesh { TopRadius = 0f, BottomRadius = 0.30f, Height = 0.18f, RadialSegments = 8 },
                  wood, V(wx, py + 0.37f, wz));
            // Poste central
            AddMI(new CylinderMesh { TopRadius = 0.03f, BottomRadius = 0.03f, Height = 0.40f, RadialSegments = 5 },
                  wood, V(wx, py + 0.48f, wz));
            // Barra horizontal de la polea
            AddMI(new BoxMesh { Size = new Vector3(0.40f, 0.04f, 0.04f) },
                  metal, V(wx, py + 0.68f, wz));
        }

        private void BuildGardens(float base_, StandardMaterial3D grass, StandardMaterial3D stone)
        {
            // Pequeños parterres de jardín en las esquinas de la plaza
            (float x, float z, float a)[] beds = {
                (-1.55f, 0.60f, 20f), (1.55f, 0.60f, -20f),
            };
            foreach (var (gx, gz, ay) in beds)
            {
                var bed = AddMI(new BoxMesh { Size = new Vector3(0.60f, 0.10f, 0.32f) },
                                grass, V(gx, base_ + 0.05f, gz));
                bed.RotationDegrees = new Vector3(0, ay, 0);
                // Árbol/arbusto Kenney — fallback esfera
                if (!TrySpawnGlb("res://assets/buildings/tree-high-round.glb",
                                  V(gx, base_ + 0.02f, gz), 0.20f, ay))
                {
                    var bush = AddMI(new SphereMesh { Radius = 0.18f, RadialSegments = 6, Rings = 4 },
                                     grass, V(gx, base_ + 0.18f, gz));
                    bush.RotationDegrees = new Vector3(0, ay, 0);
                }
            }
        }

        private void BuildFlagStaged(int stage, float poleBot,
                                      StandardMaterial3D civ, StandardMaterial3D civLit,
                                      StandardMaterial3D metal)
        {
            float poleH      = stage == 0 ? 1.40f : stage == 1 ? 1.65f : 2.00f;
            float bannerScale = stage == 0 ? 0.80f : stage == 1 ? 0.92f : 1.05f;

            // Mástil metálico (siempre procedural — da el anclaje vertical)
            AddMI(new CylinderMesh { TopRadius = 0.070f, BottomRadius = 0.070f,
                      Height = poleH, RadialSegments = 6 }, metal, V(0, poleBot + poleH * 0.5f));

            // Banner Kenney — verde para civ 0 (jugador), rojo para civ 1 (IA), fallback procedural
            string bannerPath = CivIndex == 1
                ? "res://assets/buildings/banner-red.glb"
                : "res://assets/buildings/banner-green.glb";
            float  bannerY    = poleBot + poleH * 0.58f;

            if (!TrySpawnGlb(bannerPath, V(0, bannerY, 0), bannerScale, 0f))
            {
                // Fallback: rectángulo del color cívico
                float flagW = stage == 0 ? 0.80f : stage == 1 ? 0.92f : 1.05f;
                float flagH = stage == 0 ? 0.42f : stage == 1 ? 0.48f : 0.56f;
                AddMI(new BoxMesh { Size = new Vector3(flagW, flagH, 0.05f) },
                      civ, V(flagW * 0.5f, poleBot + poleH * 0.87f));
                AddMI(new BoxMesh { Size = new Vector3(flagW, flagH * 0.28f, 0.06f) },
                      civLit, V(flagW * 0.5f, poleBot + poleH * 0.73f));
            }
        }

        private void BuildMarket(float base_, StandardMaterial3D plaster, StandardMaterial3D roofG)
        {
            // Edificio de mercado — colocado en el borde opuesto al ayuntamiento
            AddMI(new BoxMesh { Size = new Vector3(1.20f, 0.82f, 0.90f) },
                  plaster, V(-2.10f, base_ + 0.41f, 0f));
            AddMI(new CylinderMesh { TopRadius = 0f, BottomRadius = 0.80f,
                      Height = 0.52f, RadialSegments = 4 },
                  roofG, V(-2.10f, base_ + 0.82f + 0.26f, 0f));

            // Puestos de mercado Kenney (Fantasy Town Kit)
            TrySpawnGlb("res://assets/buildings/stall-green.glb",
                         V(-1.42f, base_ + 0.02f,  0.70f), 0.36f, -35f);
            TrySpawnGlb("res://assets/buildings/stall-red.glb",
                         V(-1.42f, base_ + 0.02f, -0.70f), 0.36f,  35f);
            TrySpawnGlb("res://assets/buildings/cart-high.glb",
                         V(-2.72f, base_ + 0.02f,  0.42f), 0.30f,  15f);
        }

        // ================================================================
        //  HELPERS
        // ================================================================

        private MeshInstance3D AddMI(Mesh mesh, StandardMaterial3D mat, Vector3 pos)
        {
            var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = pos };
            AddChild(mi);
            return mi;
        }

        private static Vector3 V(float x, float y, float z = 0f) => new(x, y, z);

        private static StandardMaterial3D Mat(Color color, float rough = 0.65f, float metal = 0.05f)
            => new() { AlbedoColor = color, Roughness = rough, Metallic = metal };

        /// <summary>
        // ── Caché de PackedScene para assets GLB de Kenney ──────────────
        private static readonly Dictionary<string, PackedScene> _glbCache = new();

        /// <summary>
        /// Intenta cargar y colocar un asset GLB de Kenney.
        /// Retorna true si tuvo éxito (no se necesita fallback procedural).
        /// Usa caché estático y aplica VertexColorUseAsAlbedo para colores Kenney.
        /// </summary>
        private bool TrySpawnGlb(string assetPath, Vector3 pos, float scale,
                                  float rotY = 0f, Node3D? parent = null)
        {
            if (!_glbCache.TryGetValue(assetPath, out var scene))
            {
                if (!ResourceLoader.Exists(assetPath)) return false;
                scene = GD.Load<PackedScene>(assetPath);
                if (scene == null) return false;
                _glbCache[assetPath] = scene;
            }
            try
            {
                var node = scene.Instantiate();
                if (node is not Node3D inst3d)
                {
                    var wrap = new Node3D();
                    wrap.AddChild(node);
                    inst3d = wrap;
                }
                inst3d.Scale           = Vector3.One * scale;
                inst3d.Position        = pos;
                inst3d.RotationDegrees = new Vector3(0f, rotY, 0f);
                (parent ?? this).AddChild(inst3d);
                ApplyVertexColorFix(inst3d);
                return true;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[City] Error cargando GLB '{assetPath}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Activa VertexColorUseAsAlbedo en todos los MeshInstance3D descendientes.
        /// Necesario para que los modelos Kenney muestren sus colores de vértice.
        /// </summary>
        private static void ApplyVertexColorFix(Node3D root)
        {
            foreach (var child in root.FindChildren("*", "MeshInstance3D", true, false))
            {
                if (child is not MeshInstance3D mi) continue;
                int surfaces = mi.Mesh?.GetSurfaceCount() ?? 0;
                for (int s = 0; s < surfaces; s++)
                {
                    var mat = mi.GetActiveMaterial(s) as StandardMaterial3D;
                    if (mat == null || mat.VertexColorUseAsAlbedo) continue;
                    var dup = (StandardMaterial3D)mat.Duplicate();
                    dup.VertexColorUseAsAlbedo = true;
                    mi.SetSurfaceOverrideMaterial(s, dup);
                }
            }
        }
    }
}
