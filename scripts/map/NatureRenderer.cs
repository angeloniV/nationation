using Godot;
using System.Collections.Generic;
using System.Linq;

namespace Natiolation.Map
{
    /// <summary>
    /// Renderiza toda la vegetación y decoración estática del mapa usando
    /// MultiMeshInstance3D en lugar de nodos individuales.
    ///
    /// Fog of War por tile:
    ///   • Los assets se registran con RegisterForTile(path, q, r, transform).
    ///   • Solo se muestran los tiles que han sido explorados (SetTileExplored).
    ///   • Esto garantiza que los árboles respeten el fog sin coste por frame.
    ///
    /// Árboles blancos fix:
    ///   • NO se aplica MaterialOverride en el MultiMeshInstance3D.
    ///   • El Mesh extraído del GLB retiene sus materiales de superficie originales.
    /// </summary>
    public partial class NatureRenderer : Node3D
    {
        public static NatureRenderer? Instance { get; private set; }

        // Transforms por GLB, indexados por tile (q, r) para soporte de fog
        private readonly Dictionary<string, Dictionary<(int q, int r), Transform3D>> _tileTransforms = new();

        // Tiles que el jugador ya ha explorado (fog revelado al menos una vez)
        private readonly HashSet<(int q, int r)> _exploredTiles = new();

        // MultiMeshInstance3D activo por ruta de GLB
        private readonly Dictionary<string, MultiMeshInstance3D> _meshInstances = new();

        // Caché de meshes extraídos de los GLBs
        private static readonly Dictionary<string, Mesh?> _meshCache = new();

        // ── Ciclo de vida ────────────────────────────────────────────────

        public override void _EnterTree()
        {
            Instance = this;
        }

        public override void _ExitTree()
        {
            if (Instance == this) Instance = null;
        }

        // ================================================================
        //  API PÚBLICA
        // ================================================================

        /// <summary>
        /// Registra un asset de naturaleza asociado al tile (q, r).
        /// El asset NO se muestra hasta que SetTileExplored(q, r) sea llamado.
        /// Llamado por HexTile3D.TrySpawnNature al revelarse un tile por primera vez.
        /// </summary>
        public void RegisterForTile(string glbPath, int q, int r, Transform3D worldTransform)
        {
            if (!_tileTransforms.TryGetValue(glbPath, out var dict))
            {
                dict = new Dictionary<(int, int), Transform3D>();
                _tileTransforms[glbPath] = dict;
            }
            // Un tile puede tener un único transform por GLB (el más reciente gana)
            dict[(q, r)] = worldTransform;
            // Si el tile ya fue explorado antes de registrar (ej. reload), mostrar inmediatamente
            if (_exploredTiles.Contains((q, r)))
                RebuildMultiMesh(glbPath);
        }

        /// <summary>
        /// Marca el tile (q, r) como explorado y reconstruye los MultiMeshes afectados.
        /// Llamado por HexTile3D.SetVisible cuando un tile se explora por primera vez.
        /// </summary>
        public void SetTileExplored(int q, int r)
        {
            if (!_exploredTiles.Add((q, r))) return;   // ya estaba explorado

            // Reconstruir solo los GLBs que tienen datos para este tile
            foreach (var (path, dict) in _tileTransforms)
                if (dict.ContainsKey((q, r)))
                    RebuildMultiMesh(path);
        }

        // ================================================================
        //  INTERNAL
        // ================================================================

        private void RebuildMultiMesh(string glbPath)
        {
            var mesh = GetOrExtractMesh(glbPath);
            if (mesh == null) return;

            // Filtrar: solo los tiles explorados tienen sus assets visibles
            var visibleTransforms = _tileTransforms.TryGetValue(glbPath, out var dict)
                ? dict.Where(kv => _exploredTiles.Contains(kv.Key))
                      .Select(kv => kv.Value)
                      .ToList()
                : new List<Transform3D>();

            if (!_meshInstances.TryGetValue(glbPath, out var mmi))
            {
                var mm = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    Mesh            = mesh,
                };
                mmi = new MultiMeshInstance3D { Multimesh = mm };
                // Sin MaterialOverride → los materiales del GLB se usan directamente.
                // Esto preserva colores, texturas y roughness originales del asset.
                AddChild(mmi);
                _meshInstances[glbPath] = mmi;
            }

            var multiMesh = mmi.Multimesh;
            multiMesh.InstanceCount = visibleTransforms.Count;
            for (int i = 0; i < visibleTransforms.Count; i++)
                multiMesh.SetInstanceTransform(i, visibleTransforms[i]);
        }

        /// <summary>
        /// Extrae el primer Mesh encontrado dentro del GLB (cacheado).
        /// Libera el nodo temporal pero retiene el Mesh resource (con sus materiales).
        /// </summary>
        private static Mesh? GetOrExtractMesh(string glbPath)
        {
            if (_meshCache.TryGetValue(glbPath, out var cached)) return cached;

            if (!ResourceLoader.Exists(glbPath))
            {
                _meshCache[glbPath] = null;
                return null;
            }

            var scene = GD.Load<PackedScene>(glbPath);
            if (scene == null)
            {
                _meshCache[glbPath] = null;
                return null;
            }

            var inst = scene.Instantiate();
            var mi   = FindFirstMeshInstance(inst);
            var mesh = mi?.Mesh;
            inst.Free();   // liberar nodo; el Mesh resource sigue vivo (referenciado por caché)

            _meshCache[glbPath] = mesh;
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
