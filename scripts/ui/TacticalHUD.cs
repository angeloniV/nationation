using Godot;
using System.Collections.Generic;
using Natiolation.Core;
using Natiolation.Units;

namespace Natiolation.UI
{
    /// <summary>
    /// HUD de combate táctico — visible solo durante TacticalBattleManager.IsActive.
    ///
    /// Layout:
    ///   [Panel Izq — Atacantes]   [Centro transparente — clic en 3D]   [Panel Der — Defensores]
    ///   [Barra inferior — "FIN DE TURNO TÁCTICO"]
    ///
    /// Escucha eventos de TacticalBattleManager y se auto-refresca.
    /// </summary>
    public partial class TacticalHUD : Control
    {
        private const float PanelW = 240f;
        private const float BarH   = 64f;

        // ── Paneles ──────────────────────────────────────────────────────────
        private Panel        _leftPanel  = null!;
        private Panel        _rightPanel = null!;
        private Panel        _bottomBar  = null!;

        private VBoxContainer _atkList   = null!;
        private VBoxContainer _defList   = null!;
        private Label         _turnLabel = null!;
        private Button        _endTurnBtn= null!;

        // ── Referencia al manager ─────────────────────────────────────────────
        private TacticalBattleManager? _battle;

        // ================================================================
        //  GODOT
        // ================================================================

        public override void _Ready()
        {
            // Ocupa toda la pantalla pero el centro ignora el mouse (deja pasar los clics al 3D)
            SetAnchorsPreset(LayoutPreset.FullRect);
            MouseFilter = MouseFilterEnum.Ignore;
            Visible     = false;

            BuildPanels();
            WireBattleEvents();
        }

        // ================================================================
        //  CONSTRUCCIÓN DE PANELES
        // ================================================================

        private void BuildPanels()
        {
            var styleDark = MakeStyleBox(new Color(0.06f, 0.08f, 0.12f, 0.88f));
            var styleBar  = MakeStyleBox(new Color(0.04f, 0.06f, 0.10f, 0.94f));

            // ── Panel izquierdo (atacantes) ──────────────────────────────────
            _leftPanel = new Panel { CustomMinimumSize = new Vector2(PanelW, 0) };
            _leftPanel.SetAnchorsPreset(LayoutPreset.LeftWide);
            _leftPanel.OffsetTop    = 0f;
            _leftPanel.OffsetBottom = -BarH;
            _leftPanel.OffsetRight  = PanelW;
            _leftPanel.AddThemeStyleboxOverride("panel", styleDark);
            _leftPanel.MouseFilter = MouseFilterEnum.Stop;
            AddChild(_leftPanel);

            var atkScroll = new ScrollContainer();
            atkScroll.SetAnchorsPreset(LayoutPreset.FullRect);
            atkScroll.OffsetTop  = 36f;
            atkScroll.OffsetLeft = 6f;
            atkScroll.OffsetRight = -6f;
            atkScroll.OffsetBottom = -6f;
            _leftPanel.AddChild(atkScroll);

            var atkTitle = new Label { Text = "⚔  ATACANTES", HorizontalAlignment = HorizontalAlignment.Center };
            atkTitle.AddThemeColorOverride("font_color", new Color(0.7f, 0.85f, 1.0f));
            atkTitle.Position = new Vector2(0f, 6f);
            atkTitle.Size     = new Vector2(PanelW, 28f);
            _leftPanel.AddChild(atkTitle);

            _atkList = new VBoxContainer();
            _atkList.SetAnchorsPreset(LayoutPreset.FullRect);
            atkScroll.AddChild(_atkList);

            // ── Panel derecho (defensores) ───────────────────────────────────
            _rightPanel = new Panel { CustomMinimumSize = new Vector2(PanelW, 0) };
            _rightPanel.SetAnchorsPreset(LayoutPreset.RightWide);
            _rightPanel.OffsetTop    = 0f;
            _rightPanel.OffsetBottom = -BarH;
            _rightPanel.OffsetLeft   = -PanelW;
            _rightPanel.AddThemeStyleboxOverride("panel", styleDark);
            _rightPanel.MouseFilter = MouseFilterEnum.Stop;
            AddChild(_rightPanel);

            var defScroll = new ScrollContainer();
            defScroll.SetAnchorsPreset(LayoutPreset.FullRect);
            defScroll.OffsetTop    = 36f;
            defScroll.OffsetLeft   = 6f;
            defScroll.OffsetRight  = -6f;
            defScroll.OffsetBottom = -6f;
            _rightPanel.AddChild(defScroll);

            var defTitle = new Label { Text = "🛡  DEFENSORES", HorizontalAlignment = HorizontalAlignment.Center };
            defTitle.AddThemeColorOverride("font_color", new Color(1.0f, 0.7f, 0.7f));
            defTitle.Position = new Vector2(0f, 6f);
            defTitle.Size     = new Vector2(PanelW, 28f);
            _rightPanel.AddChild(defTitle);

            _defList = new VBoxContainer();
            _defList.SetAnchorsPreset(LayoutPreset.FullRect);
            defScroll.AddChild(_defList);

            // ── Barra inferior ───────────────────────────────────────────────
            _bottomBar = new Panel();
            _bottomBar.SetAnchorsPreset(LayoutPreset.BottomWide);
            _bottomBar.OffsetTop = -BarH;
            _bottomBar.AddThemeStyleboxOverride("panel", styleBar);
            _bottomBar.MouseFilter = MouseFilterEnum.Stop;
            AddChild(_bottomBar);

            _turnLabel = new Label
            {
                Text                = "TURNO: ATACANTES",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            _turnLabel.AddThemeColorOverride("font_color", Colors.White);
            _turnLabel.SetAnchorsPreset(LayoutPreset.FullRect);
            _turnLabel.OffsetRight = -180f;
            _bottomBar.AddChild(_turnLabel);

            _endTurnBtn = new Button
            {
                Text     = "FIN DE TURNO TÁCTICO",
                Size     = new Vector2(200f, 44f),
                Position = new Vector2(0f, 10f),
            };
            _endTurnBtn.SetAnchorsPreset(LayoutPreset.CenterRight);
            _endTurnBtn.OffsetLeft   = -210f;
            _endTurnBtn.OffsetRight  = -10f;
            _endTurnBtn.OffsetTop    = -22f;
            _endTurnBtn.OffsetBottom = 22f;
            _endTurnBtn.Pressed += OnEndTurnPressed;
            _bottomBar.AddChild(_endTurnBtn);
        }

        private static StyleBoxFlat MakeStyleBox(Color color)
        {
            var sb = new StyleBoxFlat
            {
                BgColor           = color,
                CornerRadiusTopLeft    = 0,
                CornerRadiusTopRight   = 0,
                CornerRadiusBottomLeft = 0,
                CornerRadiusBottomRight= 0,
            };
            return sb;
        }

        // ================================================================
        //  EVENTOS DE BATALLA
        // ================================================================

        private void WireBattleEvents()
        {
            TacticalBattleManager.BattleStarted += OnBattleStarted;
            TacticalBattleManager.BattleEnded   += OnBattleEnded;
        }

        private void OnBattleStarted(TacticalBattleManager battle)
        {
            _battle = battle;
            _battle.UnitSelected += OnUnitSelected;
            _battle.TurnChanged  += OnTurnChanged;
            _battle.UnitDamaged  += OnUnitDamaged;

            Visible = true;
            RefreshLists();
            UpdateTurnLabel();
        }

        private void OnBattleEnded(bool attackersWon)
        {
            if (_battle != null)
            {
                _battle.UnitSelected -= OnUnitSelected;
                _battle.TurnChanged  -= OnTurnChanged;
                _battle.UnitDamaged  -= OnUnitDamaged;
                _battle = null;
            }
            Visible = false;
        }

        private void OnUnitSelected(TacticalUnit? tu)
        {
            RefreshLists();
        }

        private void OnTurnChanged()
        {
            RefreshLists();
            UpdateTurnLabel();
        }

        private void OnUnitDamaged(TacticalUnit tu, int damage)
        {
            RefreshLists();
        }

        // ================================================================
        //  REFRESCO DE LISTAS
        // ================================================================

        private void RefreshLists()
        {
            if (_battle == null) return;

            ClearChildren(_atkList);
            ClearChildren(_defList);

            var selected = _battle.SelectedUnit;
            var reachable = selected != null ? _battle.GetReachableHexes(selected) : null;
            var attackable = selected != null ? _battle.GetAttackableHexes(selected) : null;

            foreach (var tu in _battle.Attackers)
                _atkList.AddChild(BuildUnitRow(tu, isSelected: tu == selected,
                    canAct: _battle.IsAttackersTurn && !tu.IsDone));

            foreach (var tu in _battle.Defenders)
                _defList.AddChild(BuildUnitRow(tu, isSelected: false,
                    isHighlighted: attackable?.Contains(tu.Pos) ?? false,
                    canAct: false));
        }

        private static Control BuildUnitRow(TacticalUnit tu,
            bool isSelected = false,
            bool isHighlighted = false,
            bool canAct = false)
        {
            var stats    = UnitTypeData.GetStats(tu.Unit.UnitType);
            var container = new VBoxContainer();
            container.AddThemeConstantOverride("separation", 2);

            // Nombre + estado
            string doneTag = tu.IsDone ? " ✓" : (tu.HasAttacked ? " (sin ataque)" : "");
            var nameLabel = new Label { Text = $"{stats.Symbol} {stats.DisplayName}{doneTag}" };
            nameLabel.AddThemeColorOverride("font_color",
                isSelected   ? new Color(1.0f, 0.95f, 0.4f) :
                isHighlighted? new Color(1.0f, 0.5f, 0.5f) :
                canAct       ? Colors.White : new Color(0.5f, 0.5f, 0.5f));
            container.AddChild(nameLabel);

            // HP bar
            var hpBar = new ProgressBar
            {
                Value      = (double)tu.Unit.CurrentHP,
                MaxValue   = (double)tu.Unit.MaxHP,
                CustomMinimumSize = new Vector2(PanelW - 20f, 10f),
                ShowPercentage = false,
            };
            container.AddChild(hpBar);

            // Mov restante
            var movLabel = new Label
            {
                Text = $"  Mov: {tu.MovRemaining}  HP: {tu.Unit.CurrentHP}/{tu.Unit.MaxHP}",
            };
            movLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
            movLabel.AddThemeFontSizeOverride("font_size", 11);
            container.AddChild(movLabel);

            // Separador
            container.AddChild(new HSeparator());

            return container;
        }

        private static void ClearChildren(Control parent)
        {
            foreach (var child in parent.GetChildren())
                child.QueueFree();
        }

        // ================================================================
        //  TURNO
        // ================================================================

        private void UpdateTurnLabel()
        {
            if (_battle == null) return;
            bool isPlayerTurn = _battle.IsAttackersTurn;
            _turnLabel.Text      = isPlayerTurn ? "TURNO: ATACANTES (tú)" : "TURNO: DEFENSORES (IA)";
            _endTurnBtn.Disabled = !isPlayerTurn;
        }

        private void OnEndTurnPressed()
        {
            _battle?.EndPlayerTurn();
        }

        // ================================================================
        //  DESTRUCTOR
        // ================================================================

        public override void _ExitTree()
        {
            TacticalBattleManager.BattleStarted -= OnBattleStarted;
            TacticalBattleManager.BattleEnded   -= OnBattleEnded;

            // Si el nodo se destruye mientras hay una batalla activa (ej. hot-reload,
            // cambio de escena forzado), limpiar los eventos del manager de batalla.
            if (_battle != null)
            {
                _battle.UnitSelected -= OnUnitSelected;
                _battle.TurnChanged  -= OnTurnChanged;
                _battle.UnitDamaged  -= OnUnitDamaged;
                _battle = null;
            }
        }
    }
}
