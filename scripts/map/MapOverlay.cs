using System;
using Godot;
using System.Collections.Generic;

namespace Natiolation.Map
{
    /// <summary>
    /// Overlays 3D del mapa: tiles alcanzables, preview de camino, hover, selección de unidad.
    ///
    /// Usa MultiMeshInstance3D para tiles alcanzables/camino (un draw call por tipo),
    /// MeshInstance3D para hover/selección, y ArrayMesh dinámico para perímetro de territorio.
    ///
    /// Todos los overlays usan ShadingModeEnum.Unshaded para ser independientes de la luz.
    /// </summary>
    public partial class MapOverlay : Node3D
    {
        private MapManager _map = null!;

        // Geometría compartida (estáticos — generados una vez)
        private static ArrayMesh? _flatHexMesh;
        private static ArrayMesh? _outlineHexMesh;
        private static ArrayMesh? _selectionRingMesh;

        // MultiMesh para tiles alcanzables
        private MultiMeshInstance3D _reachInst = null!;

        // MultiMesh para camino
        private MultiMeshInstance3D _pathInst = null!;

        // MeshInstance para hover (un solo tile)
        private MeshInstance3D _hoverInst = null!;

        // ── Indicador de unidad seleccionada ─────────────────────────────
        private MeshInstance3D         _selectionInst = null!;
        private StandardMaterial3D     _selectionMat  = null!;
        private HexCoord?              _selectedHex   = null;
        private float                  _selPulse      = 0f;

        // Bordes de territorio por civ — mesh dinámico con solo el perímetro exterior
        private const int MaxCivs = 4;
        private readonly MeshInstance3D[]    _terrBorderInst = new MeshInstance3D[MaxCivs];
        private readonly HashSet<HexCoord>[] _territorySets  = new HashSet<HexCoord>[MaxCivs];

        private static readonly Color[] TerritoryColors =
        {
            new(0.18f, 0.42f, 0.95f, 0.92f),
            new(0.90f, 0.22f, 0.18f, 0.92f),
            new(0.22f, 0.72f, 0.25f, 0.92f),
            new(0.82f, 0.72f, 0.12f, 0.92f),
        };

        private HexCoord?         _hovered   = null;
        private HashSet<HexCoord> _reachable = new();
        private List<HexCoord>    _path      = new();

        // Colores
        private static readonly Color ReachColor      = new(0.20f, 0.92f, 0.28f, 0.38f);
        private static readonly Color PathColor       = new(1.00f, 0.82f, 0.10f, 0.50f);
        private static readonly Color HoverColor      = new(1.00f, 1.00f, 1.00f, 0.55f);
        private static readonly Color SelectionColor  = new(1.00f, 0.86f, 0.20f, 0.90f);  // dorado

        // ================================================================

        public override void _Ready()
        {
            _map = GetNode<MapManager>("/root/Main/MapManager");

            EnsureMeshes();
            BuildInstances();
        }

        /// <summary>Anima el anillo de selección con pulso dorado.</summary>
        public override void _Process(double delta)
        {
            if (!_selectionInst.Visible) return;
            _selPulse += (float)delta * 3.2f;
            float t     = (MathF.Sin(_selPulse) + 1f) * 0.5f;   // 0..1
            float alpha = 0.55f + 0.40f * t;
            float scale = 0.92f + 0.10f * t;
            _selectionMat.AlbedoColor = SelectionColor with { A = alpha };
            _selectionInst.Scale = new Vector3(scale, 1f, scale);
        }

        // ================================================================
        //  API PÚBLICA (misma que la versión 2D)
        // ================================================================

        public void SetHovered(HexCoord? h)
        {
            _hovered = h;
            RefreshHover();
        }

        public void SetReachable(HashSet<HexCoord> tiles)
        {
            _reachable = tiles;
            RefreshReachable();
            // Mostrar grilla hex solo cuando hay tiles alcanzables (movimiento de unidad)
            TerrainRenderer.Instance?.ShowGrid(tiles.Count > 0);
        }

        public void SetPath(List<HexCoord> path)
        {
            _path = path;
            RefreshPath();
        }

        public void ClearAll()
        {
            _hovered = null;
            _reachable.Clear();
            _path.Clear();
            RefreshHover();
            RefreshReachable();
            RefreshPath();
            // Ocultar grilla hex al limpiar selección
            TerrainRenderer.Instance?.ShowGrid(false);
        }

        /// <summary>
        /// Muestra u oculta el anillo dorado de selección bajo una unidad.
        /// Llamado por UnitManager al seleccionar/deseleccionar.
        /// </summary>
        public void SetSelectedUnit(HexCoord? hex)
        {
            _selectedHex = hex;
            if (hex == null)
            {
                _selectionInst.Visible = false;
                return;
            }
            float y = _map.GetTileHeight(hex.Q, hex.R) + 0.07f;
            _selectionInst.Position = HexTile3D.AxialToWorld(hex.Q, hex.R)
                                    + new Vector3(0f, y, 0f);
            _selectionInst.Scale   = Vector3.One;
            _selectionInst.Visible = true;
            _selPulse = 0f;
        }

        /// <summary>Actualiza la posición del anillo de selección (tras mover la unidad).</summary>
        public void MoveSelectedUnit(HexCoord hex)
        {
            _selectedHex = hex;
            float y = _map.GetTileHeight(hex.Q, hex.R) + 0.07f;
            _selectionInst.Position = HexTile3D.AxialToWorld(hex.Q, hex.R)
                                    + new Vector3(0f, y, 0f);
        }

        /// <summary>
        /// Actualiza el overlay de territorio para una civilización específica.
        /// Solo dibuja el borde exterior del territorio (perímetro), no anillos internos.
        /// </summary>
        public void SetTerritoryTiles(int civIndex, HashSet<HexCoord> tiles)
        {
            if (civIndex < 0 || civIndex >= MaxCivs) return;
            _territorySets[civIndex] = tiles;
            var mesh = BuildPerimeterMesh(tiles);
            _terrBorderInst[civIndex].Mesh = mesh;
        }

        // ================================================================
        //  REFRESH
        // ================================================================

        private void RefreshHover()
        {
            if (_hovered == null)
            {
                _hoverInst.Visible = false;
                return;
            }
            float y = TileY(_hovered) + 0.06f;
            _hoverInst.Position = HexTile3D.AxialToWorld(_hovered.Q, _hovered.R)
                                + new Vector3(0f, y, 0f);
            _hoverInst.Visible = true;
        }

        private void RefreshReachable()
        {
            UpdateMultiMesh(_reachInst, _reachable);
        }

        private void RefreshPath()
        {
            // El path incluye el tile origen en [0]; mostramos desde [1]
            var pathTiles = new HashSet<HexCoord>();
            for (int i = 1; i < _path.Count; i++)
                pathTiles.Add(_path[i]);
            UpdateMultiMesh(_pathInst, pathTiles);
        }

        private void UpdateMultiMesh(MultiMeshInstance3D inst, IEnumerable<HexCoord> tiles)
        {
            var list = new List<HexCoord>(tiles);
            var mm   = inst.Multimesh;
            mm.InstanceCount = list.Count;

            for (int i = 0; i < list.Count; i++)
            {
                float y  = TileY(list[i]) + 0.06f;
                var   pos = HexTile3D.AxialToWorld(list[i].Q, list[i].R)
                          + new Vector3(0f, y, 0f);
                mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, pos));
            }
        }

        // ================================================================
        //  CONSTRUCCION
        // ================================================================

        private void BuildInstances()
        {
            // Territorio primero → renderiza POR DEBAJO de los overlays de selección
            for (int i = 0; i < MaxCivs; i++)
            {
                _territorySets[i] = new HashSet<HexCoord>();
                _terrBorderInst[i] = new MeshInstance3D
                {
                    MaterialOverride = OverlayMat(TerritoryColors[i]),
                    Mesh             = new ArrayMesh(),  // empieza vacío
                };
                AddChild(_terrBorderInst[i]);
            }

            _reachInst = MakeMultiMesh(_flatHexMesh!, ReachColor);
            AddChild(_reachInst);

            _pathInst = MakeMultiMesh(_flatHexMesh!, PathColor);
            AddChild(_pathInst);

            _hoverInst = MakeSingleMesh(_outlineHexMesh!, HoverColor);
            _hoverInst.Visible = false;
            AddChild(_hoverInst);

            // Anillo dorado de selección — renderiza encima del hover
            _selectionMat  = OverlayMat(SelectionColor);
            _selectionInst = new MeshInstance3D
            {
                Mesh             = _selectionRingMesh!,
                MaterialOverride = _selectionMat,
                Visible          = false,
            };
            AddChild(_selectionInst);
        }

        private static MultiMeshInstance3D MakeMultiMesh(ArrayMesh mesh, Color color)
        {
            var mat = OverlayMat(color);
            var mm  = new MultiMesh
            {
                Mesh            = mesh,
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                InstanceCount   = 0,
            };
            var inst = new MultiMeshInstance3D
            {
                Multimesh        = mm,
                MaterialOverride = mat,
            };
            return inst;
        }

        private static MeshInstance3D MakeSingleMesh(ArrayMesh mesh, Color color)
        {
            return new MeshInstance3D
            {
                Mesh             = mesh,
                MaterialOverride = OverlayMat(color),
            };
        }

        private static StandardMaterial3D OverlayMat(Color color)
        {
            return new StandardMaterial3D
            {
                AlbedoColor      = color,
                Transparency     = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode      = BaseMaterial3D.ShadingModeEnum.Unshaded,
                NoDepthTest      = false,
                DepthDrawMode    = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                CullMode         = BaseMaterial3D.CullModeEnum.Disabled,
            };
        }

        // ================================================================
        //  GENERACIÓN DE MESHES (estáticos, compartidos)
        // ================================================================

        private static void EnsureMeshes()
        {
            _flatHexMesh       ??= BuildFlatHex(inset: 0.30f);
            _outlineHexMesh    ??= BuildHexRing(outerInset: 0.20f, width: 0.40f);
            _selectionRingMesh ??= BuildCircleRing(outerRadius: 1.10f, innerRadius: 0.65f, segments: 48);
        }

        /// <summary>Hexágono plano relleno (para reachable y path).</summary>
        private static ArrayMesh BuildFlatHex(float inset)
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            st.SetNormal(Vector3.Up);

            var verts = HexTile3D.HexRing(0f, 1f - inset / HexTile3D.HexSize);
            var center = Vector3.Zero;

            for (int i = 0; i < 6; i++)
            {
                st.SetColor(Colors.White); st.AddVertex(center);
                st.SetColor(Colors.White); st.AddVertex(verts[i]);
                st.SetColor(Colors.White); st.AddVertex(verts[(i + 1) % 6]);
            }
            return st.Commit();
        }

        /// <summary>Anillo hexagonal para hover (solo el borde).</summary>
        private static ArrayMesh BuildHexRing(float outerInset, float width)
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            st.SetNormal(Vector3.Up);

            float outerScale = 1f - outerInset / HexTile3D.HexSize;
            float innerScale = outerScale - width / HexTile3D.HexSize;
            var   outer = HexTile3D.HexRing(0f, outerScale);
            var   inner = HexTile3D.HexRing(0f, innerScale);

            for (int i = 0; i < 6; i++)
            {
                int j = (i + 1) % 6;
                // Quad: outer[i], outer[j], inner[j], inner[i]
                Quad(st, outer[i], outer[j], inner[j], inner[i]);
            }
            return st.Commit();
        }

        private static void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            st.SetColor(Colors.White);
            st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
            st.AddVertex(a); st.AddVertex(c); st.AddVertex(d);
        }

        /// <summary>
        /// Anillo circular suave (para el indicador de unidad seleccionada).
        /// Usa <paramref name="segments"/> quads para una circunferencia uniforme.
        /// </summary>
        private static ArrayMesh BuildCircleRing(float outerRadius, float innerRadius, int segments)
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            st.SetNormal(Vector3.Up);

            for (int i = 0; i < segments; i++)
            {
                float a0 = 2f * MathF.PI * i       / segments;
                float a1 = 2f * MathF.PI * (i + 1) / segments;

                var o0 = new Vector3(MathF.Cos(a0) * outerRadius, 0f, MathF.Sin(a0) * outerRadius);
                var o1 = new Vector3(MathF.Cos(a1) * outerRadius, 0f, MathF.Sin(a1) * outerRadius);
                var i0 = new Vector3(MathF.Cos(a0) * innerRadius, 0f, MathF.Sin(a0) * innerRadius);
                var i1 = new Vector3(MathF.Cos(a1) * innerRadius, 0f, MathF.Sin(a1) * innerRadius);

                st.SetColor(Colors.White); st.AddVertex(o0);
                st.SetColor(Colors.White); st.AddVertex(o1);
                st.SetColor(Colors.White); st.AddVertex(i1);
                st.SetColor(Colors.White); st.AddVertex(o0);
                st.SetColor(Colors.White); st.AddVertex(i1);
                st.SetColor(Colors.White); st.AddVertex(i0);
            }

            return st.Commit();
        }

        // ================================================================
        //  PERIMETER MESH
        // ================================================================

        /// <summary>
        /// Construye un ArrayMesh con quads solo en los bordes exteriores del territorio.
        /// Cada borde hex que NO tiene vecino en el mismo territorio genera un quad.
        /// Mapping edge d → neighbor: 0→(0,+1), 1→(-1,+1), 2→(-1,0), 3→(0,-1), 4→(+1,-1), 5→(+1,0)
        /// </summary>
        private ArrayMesh BuildPerimeterMesh(HashSet<HexCoord> tiles)
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            st.SetNormal(Vector3.Up);

            // Neighbor direction per edge index
            int[] nDQ = { 0, -1, -1, 0,  1, 1 };
            int[] nDR = { 1,  1,  0, -1, -1, 0 };

            float outerScale = 1f - 0.02f / HexTile3D.HexSize;
            float innerScale = outerScale - 0.62f / HexTile3D.HexSize;

            foreach (var hex in tiles)
            {
                var world  = HexTile3D.AxialToWorld(hex.Q, hex.R);
                float y    = _map.GetTileHeight(hex.Q, hex.R) + 0.14f;
                var outer  = HexTile3D.HexRing(y, outerScale);
                var inner  = HexTile3D.HexRing(y, innerScale);

                for (int d = 0; d < 6; d++)
                {
                    var nb = new HexCoord(hex.Q + nDQ[d], hex.R + nDR[d]);
                    if (tiles.Contains(nb)) continue;   // borde interno — no dibujar

                    // Quad en el borde d: outer[d]→outer[d+1], inner[d+1]→inner[d]
                    var a = world + outer[d];
                    var b = world + outer[(d + 1) % 6];
                    var c = world + inner[(d + 1) % 6];
                    var dd = world + inner[d];

                    st.SetColor(Colors.White); st.AddVertex(a);
                    st.SetColor(Colors.White); st.AddVertex(b);
                    st.SetColor(Colors.White); st.AddVertex(c);
                    st.SetColor(Colors.White); st.AddVertex(a);
                    st.SetColor(Colors.White); st.AddVertex(c);
                    st.SetColor(Colors.White); st.AddVertex(dd);
                }
            }

            return st.Commit();
        }

        // ================================================================
        //  HELPERS
        // ================================================================

        private float TileY(HexCoord hex)
            => _map.GetTileHeight(hex.Q, hex.R);
    }
}
