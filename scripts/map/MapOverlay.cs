using Godot;
using System.Collections.Generic;

namespace Natiolation.Map
{
    /// <summary>
    /// Overlays 3D del mapa: tiles alcanzables, preview de camino, hover.
    ///
    /// Usa MultiMeshInstance3D para tiles alcanzables/camino (un draw call por tipo),
    /// y un MeshInstance3D simple para el hover.
    ///
    /// Todos los overlays usan render_mode ShadingModeEnum.Unshaded para que
    /// el color no cambie con la iluminación.
    /// </summary>
    public partial class MapOverlay : Node3D
    {
        private MapManager _map = null!;

        // Geometría compartida
        private static ArrayMesh? _flatHexMesh;
        private static ArrayMesh? _outlineHexMesh;

        // MultiMesh para tiles alcanzables
        private MultiMeshInstance3D _reachInst = null!;

        // MultiMesh para camino
        private MultiMeshInstance3D _pathInst = null!;

        // MeshInstance para hover (un solo tile)
        private MeshInstance3D _hoverInst = null!;

        // MultiMesh de territorio — uno por civilización (máx 4)
        private const int MaxCivs = 4;
        private readonly MultiMeshInstance3D[] _terrInst = new MultiMeshInstance3D[MaxCivs];

        // Anillo de borde de territorio — más visible sin tapar el color del tile
        private static ArrayMesh? _territoryBorderMesh;

        private static readonly Color[] TerritoryColors =
        {
            new(0.18f, 0.42f, 0.95f, 0.85f),
            new(0.90f, 0.22f, 0.18f, 0.85f),
            new(0.22f, 0.72f, 0.25f, 0.85f),
            new(0.82f, 0.72f, 0.12f, 0.85f),
        };

        private HexCoord?         _hovered   = null;
        private HashSet<HexCoord> _reachable = new();
        private List<HexCoord>    _path      = new();

        // Colores
        private static readonly Color ReachColor = new(0.20f, 0.92f, 0.28f, 0.38f);
        private static readonly Color PathColor  = new(1.00f, 0.82f, 0.10f, 0.50f);
        private static readonly Color HoverColor = new(1.00f, 1.00f, 1.00f, 0.55f);

        // ================================================================

        public override void _Ready()
        {
            _map = GetNode<MapManager>("/root/Main/MapManager");

            EnsureMeshes();
            BuildInstances();
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
        /// Actualiza el overlay de territorio para una civilización específica.
        /// Llamado por CityManager al fundar ciudades o al crecer la población.
        /// </summary>
        public void SetTerritoryTiles(int civIndex, HashSet<HexCoord> tiles)
        {
            if (civIndex < 0 || civIndex >= MaxCivs) return;
            UpdateMultiMesh(_terrInst[civIndex], tiles);
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
                _terrInst[i] = MakeMultiMesh(_territoryBorderMesh!, TerritoryColors[i]);
                AddChild(_terrInst[i]);
            }

            _reachInst = MakeMultiMesh(_flatHexMesh!, ReachColor);
            AddChild(_reachInst);

            _pathInst = MakeMultiMesh(_flatHexMesh!, PathColor);
            AddChild(_pathInst);

            _hoverInst = MakeSingleMesh(_outlineHexMesh!, HoverColor);
            _hoverInst.Visible = false;
            AddChild(_hoverInst);
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
            _flatHexMesh         ??= BuildFlatHex(inset: 0.30f);
            _outlineHexMesh      ??= BuildHexRing(outerInset: 0.20f, width: 0.40f);
            _territoryBorderMesh ??= BuildHexRing(outerInset: 0.08f, width: 0.20f);
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

        // ================================================================
        //  HELPERS
        // ================================================================

        private float TileY(HexCoord hex)
            => _map.GetTileHeight(hex.Q, hex.R);
    }
}
