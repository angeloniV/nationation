using Godot;
using System.Collections.Generic;

namespace Natiolation.Map
{
    /// <summary>
    /// Renderiza toda la vegetación y decoración estática del mapa usando
    /// MultiMeshInstance3D — un draw call por GLB o por tipo de mesh primitivo.
    ///
    /// Dos canales de registro:
    ///   • RegisterForTile(glbPath, q, r, t)  — assets GLB (árboles, rocas Kenney)
    ///   • RegisterMeshForTile(key, mesh, mat, q, r, t) — meshes procedurales compartidos
    ///
    /// Fog of War: los assets solo se muestran cuando el tile fue explorado.
    /// Árboles blancos: NO se aplica MaterialOverride — GLB preserva sus materiales.
    /// </summary>
    public partial class NatureRenderer : Node3D
    {
        public static NatureRenderer? Instance { get; private set; }

        // Transforms por clave (GLB path o key semántico), indexados por tile
        // Lista<Transform3D> para soportar múltiples instancias del mismo asset en un tile
        private readonly Dictionary<string, Dictionary<(int q, int r), List<Transform3D>>> _tileTransforms = new();

        // Para claves de meshes procedurales: el (Mesh, Material) que usan
        private readonly Dictionary<string, (Mesh mesh, Material mat)> _keyMeshes = new();

        // Tiles explorados (fog revelado al menos una vez)
        private readonly HashSet<(int q, int r)> _exploredTiles = new();

        // MultiMeshInstance3D activo por clave
        private readonly Dictionary<string, MultiMeshInstance3D> _meshInstances = new();

        // Caché de Mesh extraídos de GLBs
        private static readonly Dictionary<string, Mesh?> _glbMeshCache = new();

        // ── Ciclo de vida ────────────────────────────────────────────────

        public override void _EnterTree() => Instance = this;
        public override void _ExitTree()  { if (Instance == this) Instance = null; }

        // ================================================================
        //  API PÚBLICA — GLB
        // ================================================================

        /// <summary>
        /// Registra un asset GLB asociado al tile (q, r).
        /// Soporta varios transforms del mismo GLB en el mismo tile (varios árboles).
        /// </summary>
        public void RegisterForTile(string glbPath, int q, int r, Transform3D worldTransform)
        {
            GetOrCreateList(glbPath, q, r).Add(worldTransform);
            if (_exploredTiles.Contains((q, r)))
                RebuildMultiMesh(glbPath);
        }

        // ================================================================
        //  API PÚBLICA — MESHES PROCEDURALES COMPARTIDOS
        // ================================================================

        /// <summary>
        /// Registra un mesh procedimental compartido asociado al tile (q, r).
        /// 'key' agrupa instancias con el mismo mesh+material en un único MultiMesh.
        /// El transform debe incluir la escala en su Basis.
        /// </summary>
        public void RegisterMeshForTile(string key, Mesh sharedMesh, Material sharedMat,
                                         int q, int r, Transform3D worldTransform)
        {
            _keyMeshes[key] = (sharedMesh, sharedMat);
            GetOrCreateList(key, q, r).Add(worldTransform);
            if (_exploredTiles.Contains((q, r)))
                RebuildMultiMesh(key);
        }

        // ================================================================
        //  FOG OF WAR
        // ================================================================

        /// <summary>
        /// Marca el tile (q, r) como explorado y reconstruye los MultiMeshes afectados.
        /// </summary>
        public void SetTileExplored(int q, int r)
        {
            if (!_exploredTiles.Add((q, r))) return;
            foreach (var (key, dict) in _tileTransforms)
                if (dict.ContainsKey((q, r)))
                    RebuildMultiMesh(key);
        }

        // ================================================================
        //  INTERNAL
        // ================================================================

        private List<Transform3D> GetOrCreateList(string key, int q, int r)
        {
            if (!_tileTransforms.TryGetValue(key, out var dict))
            {
                dict = new Dictionary<(int, int), List<Transform3D>>();
                _tileTransforms[key] = dict;
            }
            if (!dict.TryGetValue((q, r), out var list))
            {
                list = new List<Transform3D>();
                dict[(q, r)] = list;
            }
            return list;
        }

        private void RebuildMultiMesh(string key)
        {
            var mesh = ResolveMesh(key);
            if (mesh == null) return;

            // Acumular todos los transforms de tiles explorados
            var visible = new List<Transform3D>();
            if (_tileTransforms.TryGetValue(key, out var dict))
                foreach (var kv in dict)
                    if (_exploredTiles.Contains(kv.Key))
                        visible.AddRange(kv.Value);

            if (!_meshInstances.TryGetValue(key, out var mmi))
            {
                var mm = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    Mesh            = mesh,
                };
                mmi = new MultiMeshInstance3D { Multimesh = mm };

                // Para meshes procedurales: asignar el material compartido
                if (_keyMeshes.TryGetValue(key, out var km) && km.mat != null)
                    mmi.MaterialOverride = km.mat;
                else
                    // Para GLBs: material blanco con VertexColorUseAsAlbedo = true
                    // para que los colores de vértice del .glb se usen como albedo.
                    mmi.MaterialOverride = new StandardMaterial3D
                    {
                        VertexColorUseAsAlbedo = true,
                        AlbedoColor            = Colors.White,
                    };

                AddChild(mmi);
                _meshInstances[key] = mmi;
            }

            mmi.Multimesh.InstanceCount = visible.Count;
            for (int i = 0; i < visible.Count; i++)
                mmi.Multimesh.SetInstanceTransform(i, visible[i]);
        }

        /// <summary>
        /// Devuelve el Mesh para una clave: primero busca en _keyMeshes (procedural),
        /// luego intenta extraerlo del GLB.
        /// </summary>
        private Mesh? ResolveMesh(string key)
        {
            if (_keyMeshes.TryGetValue(key, out var km)) return km.mesh;
            return GetOrExtractGlbMesh(key);
        }

        private static Mesh? GetOrExtractGlbMesh(string glbPath)
        {
            if (_glbMeshCache.TryGetValue(glbPath, out var cached)) return cached;

            if (!ResourceLoader.Exists(glbPath))
            {
                _glbMeshCache[glbPath] = null;
                return null;
            }
            var scene = GD.Load<PackedScene>(glbPath);
            if (scene == null) { _glbMeshCache[glbPath] = null; return null; }

            var inst = scene.Instantiate();
            var mi   = FindFirstMeshInstance(inst);
            var mesh = mi?.Mesh;
            inst.Free();

            _glbMeshCache[glbPath] = mesh;
            return mesh;
        }

        private static MeshInstance3D? FindFirstMeshInstance(Node root)
        {
            if (root is MeshInstance3D mi) return mi;
            foreach (var child in root.GetChildren())
            {
                var found = FindFirstMeshInstance(child);
                if (found != null) return found;
            }
            return null;
        }
    }
}
