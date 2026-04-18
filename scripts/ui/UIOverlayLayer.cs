using Godot;

namespace Natiolation.UI
{
    /// <summary>
    /// CanvasLayer dedicado que aloja los paneles flotantes de UI (Nationpedia, Tech Tree).
    /// Vive directamente bajo Main (Node3D), por lo que sus Control hijos siempre se anclan
    /// al viewport con independencia de la cámara y de otros CanvasLayers.
    ///
    /// Árbol de nodos resultante:
    ///   Main
    ///   ├─ GameHUD    (CanvasLayer layer=10)
    ///   ├─ MinimapPanel (CanvasLayer layer=11)
    ///   └─ UIOverlay  (CanvasLayer layer=15)  ← este nodo
    ///       ├─ NationpediaPanel  (Control fullrect)
    ///       └─ TechTreePanel     (Control fullrect)
    /// </summary>
    public partial class UIOverlayLayer : CanvasLayer
    {
        public NationpediaPanel Nationpedia { get; private set; } = null!;
        public TechTreePanel    TechTree    { get; private set; } = null!;

        // _EnterTree corre ANTES que cualquier _Ready() de hermanos.
        // Esto garantiza que Nationpedia y TechTree existen cuando
        // GameHUD._Ready() los busca via GetNode<UIOverlayLayer>.
        public override void _EnterTree()
        {
            Layer = 15;

            Nationpedia = new NationpediaPanel();
            AddChild(Nationpedia);

            TechTree = new TechTreePanel();
            AddChild(TechTree);
        }
    }
}
