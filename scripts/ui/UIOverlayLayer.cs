using Godot;
using Natiolation.Core;

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
    ///       ├─ TechTreePanel     (Control fullrect)
    ///       ├─ _darknessOverlay  (ColorRect fullrect — táctico)
    ///       └─ TacticalHUD      (Control fullrect — táctico)
    /// </summary>
    public partial class UIOverlayLayer : CanvasLayer
    {
        public NationpediaPanel Nationpedia  { get; private set; } = null!;
        public TechTreePanel    TechTree     { get; private set; } = null!;
        public TacticalHUD      TacticalHUD  { get; private set; } = null!;

        private ColorRect _darknessOverlay = null!;

        // _EnterTree corre ANTES que cualquier _Ready() de hermanos.
        // Esto garantiza que Nationpedia, TechTree y TacticalHUD existen cuando
        // GameHUD._Ready() los busca via GetNode<UIOverlayLayer>.
        public override void _EnterTree()
        {
            Layer = 15;

            Nationpedia = new NationpediaPanel();
            AddChild(Nationpedia);

            TechTree = new TechTreePanel();
            AddChild(TechTree);

            // ── Oscurecimiento del mapa durante batalla táctica ──────────────
            _darknessOverlay = new ColorRect
            {
                Color       = new Color(0f, 0f, 0f, 0f),  // empieza transparente
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _darknessOverlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_darknessOverlay);

            // ── HUD táctico (oculto hasta que empiece una batalla) ───────────
            TacticalHUD = new TacticalHUD();
            AddChild(TacticalHUD);

            // ── Instanciar TacticalBattleManager como hermano de Main ────────
            // Se añade al padre de UIOverlayLayer (que es Main) para que tenga
            // acceso al GetWorld3D() del viewport.
            CallDeferred(MethodName.AddTacticalManager);
        }

        public override void _Ready()
        {
            // Suscribirse a eventos de batalla para animar el overlay de oscuridad
            TacticalBattleManager.BattleStarted += OnBattleStarted;
            TacticalBattleManager.BattleEnded   += OnBattleEnded;
        }

        public override void _ExitTree()
        {
            TacticalBattleManager.BattleStarted -= OnBattleStarted;
            TacticalBattleManager.BattleEnded   -= OnBattleEnded;
        }

        private void AddTacticalManager()
        {
            // Solo crear si no existe ya (por ejemplo, en hot-reload)
            if (TacticalBattleManager.Instance != null) return;
            var tbm = new TacticalBattleManager();
            GetParent().AddChild(tbm);
        }

        private void OnBattleStarted(TacticalBattleManager _)
        {
            // Animar ColorRect de 0 → 0.65 de alpha (oscurecimiento dramático)
            var tween = CreateTween();
            tween.TweenProperty(_darknessOverlay, "color",
                new Color(0f, 0f, 0f, 0.65f), 0.45f)
                .SetTrans(Tween.TransitionType.Sine)
                .SetEase(Tween.EaseType.Out);
        }

        private void OnBattleEnded(bool _)
        {
            // Animar ColorRect de vuelta a 0 alpha
            var tween = CreateTween();
            tween.TweenProperty(_darknessOverlay, "color",
                new Color(0f, 0f, 0f, 0f), 0.35f)
                .SetTrans(Tween.TransitionType.Sine)
                .SetEase(Tween.EaseType.In);
        }
    }
}
