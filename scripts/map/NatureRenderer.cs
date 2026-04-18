using Godot;
using System.Collections.Generic;

namespace Natiolation.Map
{
    /// <summary>
    /// Renderiza toda la vegetación y decoración estática del mapa usando
    /// MultiMeshInstance3D en lugar de nodos individuales.
    ///
    /// Un único MultiMesh por tipo de asset (ej. un MultiMesh para pinos,
    /// otro para rocas). Elimina miles de draw calls al consolidar toda
    /// la naturaleza de la misma especie en una única llamada de GPU.
    ///
    /// Flujo:
    ///   1. HexTile3D.TrySpawnNature llama RegisterAndRebuild() cuando un tile
    ///      es revelado por el fog-of-war (lazy loading idéntico al original).
    ///   2. RegisterAndRebuild acumula el Transform3D y reconstruye el buffer
    ///      del MultiMesh correspondiente (O(n) pero n es pequeño porque se
    ///      llama sólo en la fase de exploración, no cada frame).
    ///   3. El resultado: 1 draw call por especie en lugar de 1 por árbol.
    /// </summary>
    public partial class NatureRenderer : Node3D
    {
        public static NatureRenderer? Instance { get; private set; }

        // Transforms acumulados por ruta de GLB
        private readonly Dictionary<string, List<Transform3D>> _transforms = new();

        // MultiMeshInstance3D activo por ruta de GLB
        private readonly Dictionary<string, MultiMeshInstance3D> _meshInstances = new();

        // Caché de meshes extraídos de los GLBs (static: sobrevive entre escenas)
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
        /// Registra una instancia de naturaleza y reconstruye el MultiMesh
        /// del GLB correspondiente con el transform en espacio mundo.
        /// </summary>
        public void RegisterAndRebuild(string glbPath, Transform3D worldTransform)
        {
            if (!_transforms.TryGetValue(glbPath, out var list))
            {
                list = new List<Transform3D>();
                _transforms[glbPath] = list;
            }
            list.Add(worldTransform);

            RebuildMultiMesh(glbPath, list);
        }

        // ================================================================
        //  INTERNAL
        // ================================================================

        private void RebuildMultiMesh(string glbPath, List<Transform3D> transforms)
        {
            var mesh = GetOrExtractMesh(glbPath);
            if (mesh == null) return;

            // Crear o reutilizar el MultiMeshInstance3D
            if (!_meshInstances.TryGetValue(glbPath, out var mmi))
            {
                var mm = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    Mesh            = mesh,
                };

                mmi = new MultiMeshInstance3D { Multimesh = mm };

                // Vertex colors: misma fix que ApplyVertexColorFix pero a nivel de geometría
                var mat = new StandardMaterial3D
                {
                    VertexColorUseAsAlbedo = true,
                    Roughness              = 0.85f,
                };
                mmi.MaterialOverride = mat;
                AddChild(mmi);
                _meshInstances[glbPath] = mmi;
            }

            // Reconstruir el buffer de transforms
            var multiMesh = mmi.Multimesh;
            multiMesh.InstanceCount = transforms.Count;
            for (int i = 0; i < transforms.Count; i++)
                multiMesh.SetInstanceTransform(i, transforms[i]);
        }

        /// <summary>
        /// Extrae el primer Mesh encontrado dentro del GLB (cacheado).
        /// Libera el nodo temporal tras la extracción.
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
            inst.Free();   // liberar — sólo necesitábamos el Mesh resource

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
