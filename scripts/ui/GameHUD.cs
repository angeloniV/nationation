using Godot;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Natiolation.Cities;
using Natiolation.Core;
using Natiolation.Map;
using Natiolation.Units;
using Tech = Natiolation.Core.Technology;

namespace Natiolation.UI
{
	/// <summary>
	/// HUD principal — diseño estilo Civilización.
	///
	/// Top bar  : título | oro | turno
	/// Bot bar  : [Terreno] | [Panel dinámico] | [Fin de turno]
	/// Toast    : notificaciones flotantes centradas (eventos de ciudad, etc.)
	///
	/// Panel dinámico muestra acciones contextuales según la unidad seleccionada y el tile.
	/// </summary>
	public partial class GameHUD : CanvasLayer
	{
		private UnitManager _unitManager = null!;
		private CityManager _cityManager = null!;
		private MapManager  _map         = null!;
		private NationpediaPanel _nationpedia = null!;
		private TechTreePanel    _techTree    = null!;

		// ── UI refs ──────────────────────────────────────────────────────
		private Label         _turnLabel         = null!;
		private Label         _goldLabel         = null!;
		private Label         _scienceLabel      = null!;
		private Label         _researchTopLabel  = null!;
		private ProgressBar   _researchTopBar    = null!;
		private Label         _tileNameLabel   = null!;
		private Label         _tileYieldsLabel = null!;

		private Control          _unitInfoPanel      = null!;
		private Label            _unitNameLabel      = null!;
		private Label            _unitMovesLabel     = null!;
		private VBoxContainer    _unitActionsColumn  = null!;

		private Control       _cityInfoPanel   = null!;
		private Label         _cityNameLabel   = null!;
		private Label         _cityPopLabel    = null!;
		private ProgressBar   _foodBar         = null!;
		private Label         _foodLabel       = null!;
		private ProgressBar   _prodBar         = null!;
		private Label         _prodLabel       = null!;
		private HBoxContainer _unitQueueRow     = null!;
		private HBoxContainer _buildingQueueRow = null!;
		private Label         _cityBuildingsLabel = null!;

		private Control       _hintPanel          = null!;
		private Control       _armyInfoPanel      = null!;
		private Label         _armyCountLabel     = null!;
		private VBoxContainer _armyUnitsList      = null!;
		private Army?         _displayedArmy      = null;
		private Control       _researchActivePanel = null!;
		private Label         _researchNameLabel   = null!;
		private ProgressBar   _researchBar         = null!;
		private Label         _researchProgressLabel = null!;
		private Control       _techPickerPanel     = null!;
		private VBoxContainer _techPickerList      = null!;

		// ── Toast ────────────────────────────────────────────────────────
		private PanelContainer _toastPanel   = null!;
		private Label          _toastLabel   = null!;
		private readonly Queue<string> _toastQueue = new();
		private bool           _toastShowing = false;

		private int _selCityQ = -1, _selCityR = -1;

		// ── Delegates almacenados para poder desuscribirse (lambdas anónimas son irrecuperables) ──
		private UnitManager.ResearchRequiredEventHandler? _onResearchRequired;
		private UnitManager.OpenTechPickerEventHandler?   _onOpenTechPicker;

		// ── Paleta ───────────────────────────────────────────────────────
		private static readonly Color BgDark    = new(0.04f, 0.06f, 0.10f, 0.96f);
		private static readonly Color BgSection = new(0.08f, 0.10f, 0.16f, 1.00f);
		private static readonly Color BgInset   = new(0.11f, 0.14f, 0.20f, 1.00f);

		private static readonly Color Gold      = new(1.00f, 0.82f, 0.14f);
		private static readonly Color TextMain  = new(0.94f, 0.94f, 0.97f);
		private static readonly Color TextDim   = new(0.58f, 0.62f, 0.70f);
		private static readonly Color TextHint  = new(0.82f, 0.87f, 0.96f);

		private static readonly Color CFood    = new(0.28f, 0.88f, 0.38f);
		private static readonly Color CProd    = new(0.98f, 0.68f, 0.14f);
		private static readonly Color CGold    = new(1.00f, 0.85f, 0.20f);
		private static readonly Color CMov     = new(0.62f, 0.80f, 1.00f);
		private static readonly Color CAction  = new(0.48f, 0.76f, 1.00f);
		private static readonly Color CFort    = new(1.00f, 0.82f, 0.20f);   // dorado para fortify
		private static readonly Color CScience = new(0.28f, 0.90f, 0.96f);   // turquesa ciencia

		private static readonly Color BtnRed    = new(0.55f, 0.09f, 0.07f);
		private static readonly Color BtnRedH   = new(0.75f, 0.14f, 0.10f);
		private static readonly Color BtnRedP   = new(0.38f, 0.05f, 0.04f);
		private static readonly Color BtnBlue   = new(0.14f, 0.26f, 0.55f);
		private static readonly Color BtnBlueH  = new(0.20f, 0.36f, 0.72f);
		private static readonly Color BtnGreen  = new(0.18f, 0.42f, 0.20f);
		private static readonly Color BtnGreenH = new(0.26f, 0.56f, 0.28f);

		// ================================================================
		//  GODOT
		// ================================================================

		public override void _Ready()
		{
			// Paneles flotantes PRIMERO: UIOverlay._EnterTree ya corrió antes que este _Ready,
			// así los campos están poblados cuando los lambdas de los botones se crean en BuildUI().
			var overlay  = GetNode<UIOverlayLayer>("/root/Main/UIOverlay");
			_nationpedia = overlay.Nationpedia;
			_techTree    = overlay.TechTree;

			_unitManager = GetNode<UnitManager>("/root/Main/UnitManager");
			_cityManager = GetNode<CityManager>("/root/Main/CityManager");
			_map         = GetNode<MapManager> ("/root/Main/MapManager");
			Layer = 10;
			BuildUI();
			BuildToast();
			WireSignals();
		}

		public override void _UnhandledInput(InputEvent @event)
		{
			if (@event is InputEventKey key && key.Pressed && !key.Echo)
			{
				if (key.Keycode == Key.N)
				{
					_nationpedia.Toggle();
					GetViewport().SetInputAsHandled();
				}
				else if (key.Keycode == Key.T)
				{
					_techTree.Toggle();
					GetViewport().SetInputAsHandled();
				}
			}
		}

		// ================================================================
		//  CONSTRUCCIÓN
		// ================================================================

		private void BuildUI()
		{
			var root = new VBoxContainer();
			root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			root.MouseFilter = Control.MouseFilterEnum.Ignore;
			AddChild(root);

			root.AddChild(BuildTopBar());

			var spacer = new Control();
			spacer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
			spacer.MouseFilter       = Control.MouseFilterEnum.Ignore;
			root.AddChild(spacer);

			root.AddChild(BuildBottomBar());
		}

		// ── Top bar ──────────────────────────────────────────────────────

		private Control BuildTopBar()
		{
			var panel = Inset(BgDark);
			panel.CustomMinimumSize = new Vector2(0, 80);
			panel.MouseFilter       = Control.MouseFilterEnum.Ignore;

			var m   = Margin(panel, 20, 20, 8, 8);
			var row = HBox(16);
			row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			row.SizeFlagsVertical   = Control.SizeFlags.ExpandFill;
			m.AddChild(row);

			// Título
			var title = Lbl("NATIOLATION", 26, Gold);
			title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			row.AddChild(title);

			// ── Panel de investigación activa (siempre visible) ──────────
			var resBox = new PanelContainer();
			resBox.MouseFilter = Control.MouseFilterEnum.Ignore;
			var resStyle = new StyleBoxFlat { BgColor = new Color(0.06f, 0.14f, 0.20f, 0.92f) };
			resStyle.SetCornerRadiusAll(6);
			resStyle.ContentMarginLeft = resStyle.ContentMarginRight = 14;
			resStyle.ContentMarginTop  = resStyle.ContentMarginBottom = 6;
			resBox.AddThemeStyleboxOverride("panel", resStyle);
			resBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			resBox.CustomMinimumSize   = new Vector2(260, 0);

			var resCol = VBox(4);
			resCol.MouseFilter = Control.MouseFilterEnum.Ignore;
			resBox.AddChild(resCol);

			_researchTopLabel = Lbl("Sin investigación activa  [T]", 17, CScience);
			resCol.AddChild(_researchTopLabel);

			_researchTopBar = new ProgressBar();
			_researchTopBar.CustomMinimumSize   = new Vector2(0, 10);
			_researchTopBar.ShowPercentage       = false;
			_researchTopBar.SizeFlagsHorizontal  = Control.SizeFlags.ExpandFill;
			var rFill2 = new StyleBoxFlat { BgColor = CScience.Darkened(0.20f) }; rFill2.SetCornerRadiusAll(3);
			var rBg2   = new StyleBoxFlat { BgColor = BgInset                  }; rBg2.SetCornerRadiusAll(3);
			_researchTopBar.AddThemeStyleboxOverride("fill",       rFill2);
			_researchTopBar.AddThemeStyleboxOverride("background", rBg2);
			_researchTopBar.MinValue = 0; _researchTopBar.MaxValue = 1; _researchTopBar.Value = 0;
			resCol.AddChild(_researchTopBar);

			row.AddChild(resBox);

			// ── Economía ──────────────────────────────────────────────────
			_goldLabel = Lbl("💰  50   (+0/t)", 18, Gold);
			_goldLabel.HorizontalAlignment = HorizontalAlignment.Right;
			_goldLabel.CustomMinimumSize   = new Vector2(180, 0);
			row.AddChild(_goldLabel);

			_scienceLabel = Lbl("🔬  0   (+1/t)", 18, CScience);
			_scienceLabel.HorizontalAlignment = HorizontalAlignment.Right;
			_scienceLabel.CustomMinimumSize   = new Vector2(160, 0);
			row.AddChild(_scienceLabel);

			_turnLabel = Lbl("TURNO  1", 20, TextMain);
			_turnLabel.HorizontalAlignment = HorizontalAlignment.Right;
			_turnLabel.CustomMinimumSize   = new Vector2(120, 0);
			row.AddChild(_turnLabel);

			// Tech Tree button
			var btnTech = new Button { Text = "🔬" };
			btnTech.TooltipText       = "Árbol de Tecnologías  [T]";
			btnTech.CustomMinimumSize = new Vector2(40, 0);
			btnTech.AddThemeStyleboxOverride("normal",  RoundedBtn(BtnBlue,  6));
			btnTech.AddThemeStyleboxOverride("hover",   RoundedBtn(BtnBlueH, 6));
			btnTech.AddThemeStyleboxOverride("pressed", RoundedBtn(BtnBlue,  6));
			btnTech.AddThemeStyleboxOverride("focus",   RoundedBtn(BtnBlue,  6));
			btnTech.Pressed += () => _techTree?.Toggle();
			row.AddChild(btnTech);

			// Nationpedia button
			var btnNation = new Button { Text = "📖" };
			btnNation.TooltipText       = "Nationpedia  [N]";
			btnNation.CustomMinimumSize = new Vector2(40, 0);
			btnNation.AddThemeStyleboxOverride("normal",  RoundedBtn(BtnBlue,  6));
			btnNation.AddThemeStyleboxOverride("hover",   RoundedBtn(BtnBlueH, 6));
			btnNation.AddThemeStyleboxOverride("pressed", RoundedBtn(BtnBlue,  6));
			btnNation.AddThemeStyleboxOverride("focus",   RoundedBtn(BtnBlue,  6));
			btnNation.Pressed += () => _nationpedia.Toggle();
			row.AddChild(btnNation);

			return panel;
		}

		// ── Bottom bar ───────────────────────────────────────────────────

		private Control BuildBottomBar()
		{
			var panel = Inset(BgDark);
			panel.CustomMinimumSize = new Vector2(0, 190);
			panel.MouseFilter       = Control.MouseFilterEnum.Stop;

			var m   = Margin(panel, 16, 16, 10, 10);
			var row = HBox(12);
			m.AddChild(row);

			row.AddChild(BuildSection(BuildTileInfo(), 220));
			row.AddChild(Divider());

			var center = BuildCenter();
			center.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			row.AddChild(center);

			row.AddChild(Divider());
			row.AddChild(BuildEndTurnBtn());

			return panel;
		}

		private static Control BuildSection(Control inner, float minW = 0)
		{
			var box = new PanelContainer();
			box.MouseFilter = Control.MouseFilterEnum.Ignore;
			if (minW > 0) box.CustomMinimumSize = new Vector2(minW, 0);
			box.AddThemeStyleboxOverride("panel", RoundedPanel(BgSection, 6));

			var m = Margin(box, 14, 14, 10, 10);
			m.AddChild(inner);
			return box;
		}

		// ── Terreno ───────────────────────────────────────────────────────

		private Control BuildTileInfo()
		{
			var col = VBox(6);
			col.MouseFilter    = Control.MouseFilterEnum.Ignore;
			col.ClipContents   = true;

			col.AddChild(Header("TERRENO"));

			_tileNameLabel = Lbl("—", 26, TextMain);
			_tileNameLabel.AutowrapMode          = TextServer.AutowrapMode.Off;
			_tileNameLabel.ClipText              = true;
			_tileNameLabel.SizeFlagsHorizontal   = Control.SizeFlags.Fill;
			col.AddChild(_tileNameLabel);

			_tileYieldsLabel = Lbl("", 19, TextDim);
			_tileYieldsLabel.AutowrapMode        = TextServer.AutowrapMode.Off;
			_tileYieldsLabel.ClipText            = true;
			_tileYieldsLabel.SizeFlagsHorizontal = Control.SizeFlags.Fill;
			col.AddChild(_tileYieldsLabel);

			return col;
		}

		// ── Panel dinámico ────────────────────────────────────────────────

		private Control BuildCenter()
		{
			var col = VBox(0);
			col.MouseFilter = Control.MouseFilterEnum.Ignore;

			// ── Unidad ────────────────────────────────────────────────────
			_unitInfoPanel             = VBox(6);
			_unitInfoPanel.Visible     = false;
			_unitInfoPanel.MouseFilter = Control.MouseFilterEnum.Ignore;

			_unitInfoPanel.AddChild(Header("UNIDAD SELECCIONADA"));
			_unitNameLabel  = Lbl("", 26, TextMain);
			_unitMovesLabel = Lbl("", 20, CMov);
			_unitActionsColumn = VBox(8);
			_unitActionsColumn.MouseFilter = Control.MouseFilterEnum.Ignore;

			_unitInfoPanel.AddChild(_unitNameLabel);
			_unitInfoPanel.AddChild(_unitMovesLabel);
			_unitInfoPanel.AddChild(_unitActionsColumn);

			var uBox = BuildSection(_unitInfoPanel);
			uBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			uBox.Visible             = false;
			_unitInfoPanel.SetMeta("container", uBox);
			col.AddChild(uBox);

			// ── Ciudad ────────────────────────────────────────────────────
			_cityInfoPanel         = BuildCityPanel();
			_cityInfoPanel.Visible = false;

			var cBox = BuildSection(_cityInfoPanel);
			cBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			cBox.Visible             = false;
			_cityInfoPanel.SetMeta("container", cBox);
			col.AddChild(cBox);

			// ── Ejército ──────────────────────────────────────────────────
			_armyInfoPanel         = BuildArmyPanel();
			_armyInfoPanel.Visible = false;

			var aBox = BuildSection(_armyInfoPanel);
			aBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			aBox.Visible             = false;
			_armyInfoPanel.SetMeta("container", aBox);
			col.AddChild(aBox);

			// ── Panel investigación activa ────────────────────────────────
			_researchActivePanel         = BuildResearchActivePanel();
			_researchActivePanel.Visible = false;

			var raBox = BuildSection(_researchActivePanel);
			raBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			raBox.Visible             = false;
			_researchActivePanel.SetMeta("container", raBox);
			col.AddChild(raBox);

			// ── Tech picker ───────────────────────────────────────────────
			_techPickerPanel         = BuildTechPickerPanel();
			_techPickerPanel.Visible = false;

			var tpBox = BuildSection(_techPickerPanel);
			tpBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			tpBox.Visible             = false;
			_techPickerPanel.SetMeta("container", tpBox);
			col.AddChild(tpBox);

			// ── Controles ─────────────────────────────────────────────────
			_hintPanel             = BuildHintPanel();
			_hintPanel.MouseFilter = Control.MouseFilterEnum.Ignore;

			var hBox = BuildSection(_hintPanel);
			hBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			_hintPanel.SetMeta("container", hBox);
			col.AddChild(hBox);

			return col;
		}

		// ── Panel de ejército ─────────────────────────────────────────────

		private Control BuildArmyPanel()
		{
			var col = VBox(6);
			col.MouseFilter = Control.MouseFilterEnum.Ignore;

			col.AddChild(Header("EJÉRCITO SELECCIONADO"));

			_armyCountLabel = Lbl("", 22, TextMain);
			col.AddChild(_armyCountLabel);

			col.AddChild(Lbl("Unidades — click [Desplegar] y luego click D en hex adyacente:", 14, TextDim));

			var scroll = new ScrollContainer();
			scroll.CustomMinimumSize   = new Vector2(0, 80);
			scroll.SizeFlagsVertical   = Control.SizeFlags.ExpandFill;
			scroll.MouseFilter         = Control.MouseFilterEnum.Stop;

			_armyUnitsList = VBox(4);
			_armyUnitsList.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			scroll.AddChild(_armyUnitsList);
			col.AddChild(scroll);

			return col;
		}

		private void RefreshArmyPanel(Army army)
		{
			_displayedArmy = army;
			_armyCountLabel.Text = $"⚔  {army.Count} unidades  |  ⚡ {army.RemainingMovement}/{army.MaxMovement}";
			_armyCountLabel.AddThemeColorOverride("font_color", army.CivColor.Lightened(0.22f));

			foreach (var child in _armyUnitsList.GetChildren()) child.QueueFree();

			foreach (var unit in army.Units)
			{
				var row = HBox(10);
				row.MouseFilter = Control.MouseFilterEnum.Ignore;

				var stats    = UnitTypeData.GetStats(unit.UnitType);
				bool isChamp = unit == army.Champion;

				// Icono + nombre
				string icon = isChamp ? "⚔" : "•";
				var nameLabel = Lbl($"{icon}  {stats.DisplayName}", 17,
									isChamp ? Gold : TextMain);
				nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
				row.AddChild(nameLabel);

				// Barra HP
				var hpBar = new ProgressBar();
				hpBar.CustomMinimumSize   = new Vector2(80, 12);
				hpBar.ShowPercentage      = false;
				hpBar.SizeFlagsVertical   = Control.SizeFlags.ShrinkCenter;
				hpBar.MinValue = 0; hpBar.MaxValue = unit.MaxHP; hpBar.Value = unit.CurrentHP;
				float hpRatio  = unit.MaxHP > 0 ? (float)unit.CurrentHP / unit.MaxHP : 0f;
				var hpColor    = hpRatio > 0.5f ? CFood : (hpRatio > 0.25f ? Gold : new Color(0.9f, 0.2f, 0.2f));
				var hpFill     = new StyleBoxFlat { BgColor = hpColor }; hpFill.SetCornerRadiusAll(2);
				var hpBg       = new StyleBoxFlat { BgColor = BgInset };  hpBg.SetCornerRadiusAll(2);
				hpBar.AddThemeStyleboxOverride("fill",       hpFill);
				hpBar.AddThemeStyleboxOverride("background", hpBg);
				row.AddChild(hpBar);

				// Botón Desplegar
				var deployBtn = new Button { Text = "Desplegar" };
				deployBtn.CustomMinimumSize = new Vector2(80, 0);
				deployBtn.AddThemeStyleboxOverride("normal",  RoundedBtn(BtnBlue,  5));
				deployBtn.AddThemeStyleboxOverride("hover",   RoundedBtn(BtnBlueH, 5));
				deployBtn.AddThemeStyleboxOverride("pressed", RoundedBtn(BtnBlue,  5));
				deployBtn.AddThemeStyleboxOverride("focus",   RoundedBtn(BtnBlue,  5));
				deployBtn.AddThemeColorOverride("font_color", Colors.White);
				deployBtn.AddThemeFontSizeOverride("font_size", 13);
				var capturedUnit = unit;
				deployBtn.Pressed += () =>
				{
					if (_displayedArmy != null)
						_unitManager.PrepareDeployUnit(capturedUnit);
				};
				row.AddChild(deployBtn);

				_armyUnitsList.AddChild(row);
			}
		}

		private Control BuildResearchActivePanel()
		{
			var col = VBox(8);
			col.MouseFilter = Control.MouseFilterEnum.Ignore;
			col.AddChild(Header("INVESTIGACIÓN"));

			_researchNameLabel = Lbl("Ninguna investigación activa", 20, CScience);
			col.AddChild(_researchNameLabel);

			_researchBar = new ProgressBar();
			_researchBar.CustomMinimumSize   = new Vector2(0, 14);
			_researchBar.ShowPercentage      = false;
			_researchBar.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			var rFill = new StyleBoxFlat { BgColor = CScience }; rFill.SetCornerRadiusAll(3);
			var rBg   = new StyleBoxFlat { BgColor = BgInset  }; rBg.SetCornerRadiusAll(3);
			_researchBar.AddThemeStyleboxOverride("fill",       rFill);
			_researchBar.AddThemeStyleboxOverride("background", rBg);
			col.AddChild(_researchBar);

			_researchProgressLabel = Lbl("🔬  0 / 0  (? turnos)", 15, TextDim);
			col.AddChild(_researchProgressLabel);

			var changeBtn = new Button { Text = "Cambiar tecnología  →" };
			changeBtn.AddThemeStyleboxOverride("normal",  RoundedBtn(BtnBlue,  6));
			changeBtn.AddThemeStyleboxOverride("hover",   RoundedBtn(BtnBlueH, 6));
			changeBtn.AddThemeStyleboxOverride("pressed", RoundedBtn(BtnBlue,  6));
			changeBtn.AddThemeStyleboxOverride("focus",   RoundedBtn(BtnBlue,  6));
			changeBtn.AddThemeColorOverride("font_color", Colors.White);
			changeBtn.AddThemeFontSizeOverride("font_size", 14);
			changeBtn.Pressed += () => SetActivePanel(4);
			col.AddChild(changeBtn);

			return col;
		}

		private Control BuildTechPickerPanel()
		{
			var col = VBox(8);
			col.MouseFilter = Control.MouseFilterEnum.Ignore;
			col.AddChild(Header("ELEGIR TECNOLOGÍA  [T]"));

			var scroll = new ScrollContainer();
			scroll.CustomMinimumSize   = new Vector2(0, 100);
			scroll.SizeFlagsVertical   = Control.SizeFlags.ExpandFill;
			scroll.MouseFilter         = Control.MouseFilterEnum.Stop;

			_techPickerList = VBox(6);
			_techPickerList.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			scroll.AddChild(_techPickerList);
			col.AddChild(scroll);

			return col;
		}

		private void RebuildTechPicker()
		{
			foreach (var child in _techPickerList.GetChildren()) child.QueueFree();

			var gm = GameManager.Instance;

			foreach (Tech tech in System.Enum.GetValues<Tech>())
			{
				if (gm.HasTech(tech)) continue;   // ya investigada, no mostrar

				var stats     = TechnologyData.GetStats(tech);
				bool canRes   = gm.CanResearch(tech);
				bool isCurrent= gm.CurrentResearch == tech;

				var row = HBox(12);
				row.MouseFilter = Control.MouseFilterEnum.Ignore;

				var nameLabel = Lbl(stats.DisplayName, 17, canRes ? TextMain : TextDim);
				nameLabel.CustomMinimumSize = new Vector2(130, 0);
				row.AddChild(nameLabel);

				row.AddChild(Lbl($"🔬 {stats.ResearchCost}", 15, CScience));

				if (!canRes && stats.Prerequisites.Length > 0)
				{
					var reqNames = string.Join(", ", System.Array.ConvertAll(
						stats.Prerequisites, p => TechnologyData.GetStats(p).DisplayName));
					row.AddChild(Lbl($"  req: {reqNames}", 13, TextDim));
				}
				else
				{
					// Botón INVESTIGAR
					var btn = new Button { Text = isCurrent ? "✓ EN PROGRESO" : "INVESTIGAR" };
					btn.CustomMinimumSize = new Vector2(120, 0);
					var btnCol  = isCurrent ? BtnGreen  : BtnBlue;
					var btnColH = isCurrent ? BtnGreenH : BtnBlueH;
					btn.AddThemeStyleboxOverride("normal",  RoundedBtn(btnCol,  6));
					btn.AddThemeStyleboxOverride("hover",   RoundedBtn(btnColH, 6));
					btn.AddThemeStyleboxOverride("pressed", RoundedBtn(btnCol,  6));
					btn.AddThemeStyleboxOverride("focus",   RoundedBtn(btnCol,  6));
					btn.AddThemeColorOverride("font_color", Colors.White);
					btn.AddThemeFontSizeOverride("font_size", 13);
					var cap = tech;
					btn.Pressed += () =>
					{
						gm.SetResearch(cap);
						SetActivePanel(3);
						RefreshResearchPanel();
						RefreshResearchTopBar();
					};
					row.AddChild(btn);
				}

				_techPickerList.AddChild(row);
			}
		}

		private void RefreshResearchTopBar()
		{
			var gm = GameManager.Instance;
			if (gm.CurrentResearch == null)
			{
				_researchTopLabel.Text  = "Sin investigación  [T]";
				_researchTopBar.MinValue = 0; _researchTopBar.MaxValue = 1; _researchTopBar.Value = 0;
			}
			else
			{
				var stats  = TechnologyData.GetStats(gm.CurrentResearch.Value);
				int sci    = _cityManager.GetTotalSciencePerTurn(0);
				int turns  = sci > 0
					? Mathf.CeilToInt((stats.ResearchCost - gm.ScienceStored) / (float)sci)
					: 999;
				_researchTopLabel.Text   = $"🔬  {stats.DisplayName}  ({turns}t restantes)";
				_researchTopBar.MinValue = 0;
				_researchTopBar.MaxValue = stats.ResearchCost;
				_researchTopBar.Value    = gm.ScienceStored;
			}
		}

		private void RefreshResearchPanel()
		{
			var gm = GameManager.Instance;
			if (gm.CurrentResearch == null)
			{
				_researchNameLabel.Text    = "Ninguna investigación activa";
				_researchBar.MinValue      = 0;
				_researchBar.MaxValue      = 1;
				_researchBar.Value         = 0;
				_researchProgressLabel.Text = "🔬  —";
			}
			else
			{
				var stats = TechnologyData.GetStats(gm.CurrentResearch.Value);
				int sci   = _cityManager.GetTotalSciencePerTurn(0);
				int turns = sci > 0
					? Mathf.CeilToInt((stats.ResearchCost - gm.ScienceStored) / (float)sci)
					: 999;

				_researchNameLabel.Text     = $"Investigando: {stats.DisplayName}";
				_researchBar.MinValue       = 0;
				_researchBar.MaxValue       = stats.ResearchCost;
				_researchBar.Value          = gm.ScienceStored;
				_researchProgressLabel.Text = $"🔬  {gm.ScienceStored} / {stats.ResearchCost}  ({turns} turnos)";
			}
		}

		private void OnTechResearched(int techInt)
		{
			RefreshResearchTopBar();
			var tech  = (Tech)techInt;
			var stats = TechnologyData.GetStats(tech);

			var unlocked = new System.Collections.Generic.List<string>();
			foreach (var u in stats.UnlocksUnits)
				unlocked.Add(UnitTypeData.GetStats(u).DisplayName);
			foreach (var b in stats.UnlocksBuildings)
				unlocked.Add(BuildingTypeData.GetStats(b).DisplayName);

			string extra = unlocked.Count > 0 ? $"  Desbloquea: {string.Join(", ", unlocked)}" : "";
			ShowToast($"🔬  ¡{stats.DisplayName} investigada!{extra}");

			// Refrescar cola de producción de ciudad abierta
			if (_selCityQ >= 0)
			{
				var city = _cityManager.GetCityAt(_selCityQ, _selCityR);
				if (city != null) RefreshCityPanel(city);
			}

			// Refrescar el Tech Tree si está abierto
			_techTree?.Refresh();

			// Abrir el tech picker automáticamente si hay tecnologías disponibles
			if (GameManager.Instance.GetAvailableTechs().Any())
			{
				ShowToast("🔬  Elige la siguiente tecnología a investigar");
				SetActivePanel(4);
			}
		}

		private Control BuildHintPanel()
		{
			var col = VBox(8);
			col.MouseFilter = Control.MouseFilterEnum.Ignore;
			col.AddChild(Header("CONTROLES GENERALES"));

			var row = HBox(24);
			row.MouseFilter = Control.MouseFilterEnum.Ignore;
			row.AddChild(KeyBadge("Click", "seleccionar"));
			row.AddChild(KeyBadge("Click D", "cancelar"));
			row.AddChild(KeyBadge("Espacio", "siguiente unidad"));
			row.AddChild(KeyBadge("Enter", "fin de turno"));
			row.AddChild(KeyBadge("WASD", "cámara"));
			row.AddChild(KeyBadge("Scroll", "zoom"));
			col.AddChild(row);

			return col;
		}

		// ── Panel de ciudad ───────────────────────────────────────────────

		private Control BuildCityPanel()
		{
			var col = VBox(4);
			col.MouseFilter = Control.MouseFilterEnum.Ignore;

			var row1 = HBox(12);
			row1.MouseFilter = Control.MouseFilterEnum.Ignore;
			_cityNameLabel = Lbl("", 24, Gold);
			_cityNameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			row1.AddChild(_cityNameLabel);
			_cityPopLabel = Lbl("", 17, TextDim);
			row1.AddChild(_cityPopLabel);
			col.AddChild(row1);

			col.AddChild(BuildResourceRow("🌾", CFood, out _foodBar, out _foodLabel));
			col.AddChild(BuildResourceRow("⚒", CProd, out _prodBar, out _prodLabel));

			_cityBuildingsLabel = Lbl("", 16, TextDim);
			col.AddChild(_cityBuildingsLabel);

			_unitQueueRow = HBox(6);
			_unitQueueRow.MouseFilter = Control.MouseFilterEnum.Ignore;
			col.AddChild(_unitQueueRow);

			_buildingQueueRow = HBox(6);
			_buildingQueueRow.MouseFilter = Control.MouseFilterEnum.Ignore;
			col.AddChild(_buildingQueueRow);

			return col;
		}

		private Control BuildResourceRow(string icon, Color barColor,
										  out ProgressBar bar, out Label label)
		{
			var row = HBox(8);
			row.MouseFilter = Control.MouseFilterEnum.Ignore;

			var ic = Lbl(icon, 16, barColor);
			ic.CustomMinimumSize = new Vector2(24, 0);
			row.AddChild(ic);

			bar = new ProgressBar();
			bar.CustomMinimumSize    = new Vector2(0, 14);
			bar.ShowPercentage       = false;
			bar.SizeFlagsHorizontal  = Control.SizeFlags.ExpandFill;
			var fill = new StyleBoxFlat { BgColor = barColor }; fill.SetCornerRadiusAll(3);
			var bg   = new StyleBoxFlat { BgColor = BgInset  }; bg.SetCornerRadiusAll(3);
			bar.AddThemeStyleboxOverride("fill", fill);
			bar.AddThemeStyleboxOverride("background", bg);
			row.AddChild(bar);

			label = Lbl("", 14, TextDim);
			label.CustomMinimumSize   = new Vector2(140, 0);
			label.HorizontalAlignment = HorizontalAlignment.Right;
			row.AddChild(label);

			return row;
		}

		// ── Botón fin de turno ────────────────────────────────────────────

		private Control BuildEndTurnBtn()
		{
			var col = VBox(0);
			col.MouseFilter = Control.MouseFilterEnum.Ignore;

			var btn = new Button { Text = "FIN\nDE\nTURNO" };
			btn.CustomMinimumSize = new Vector2(110, 148);
			btn.AddThemeStyleboxOverride("normal",  RoundedBtn(BtnRed,  8));
			btn.AddThemeStyleboxOverride("hover",   RoundedBtn(BtnRedH, 8));
			btn.AddThemeStyleboxOverride("pressed", RoundedBtn(BtnRedP, 8));
			btn.AddThemeStyleboxOverride("focus",   RoundedBtn(BtnRed,  8));
			btn.AddThemeColorOverride("font_color", Colors.White);
			btn.AddThemeFontSizeOverride("font_size", 17);
			btn.Pressed += () => _unitManager.EndTurn();
			col.AddChild(btn);

			return col;
		}

		// ================================================================
		//  TOAST
		// ================================================================

		private void BuildToast()
		{
			// Contenedor flotante — hijo directo del CanvasLayer (fuera del VBox)
			_toastPanel = new PanelContainer();
			_toastPanel.MouseFilter  = Control.MouseFilterEnum.Ignore;
			_toastPanel.AnchorLeft   = 0.50f;
			_toastPanel.AnchorRight  = 0.50f;
			_toastPanel.AnchorTop    = 0f;
			_toastPanel.AnchorBottom = 0f;
			_toastPanel.OffsetLeft   = -290f;
			_toastPanel.OffsetRight  =  290f;
			_toastPanel.OffsetTop    =  88f;
			_toastPanel.OffsetBottom = 142f;
			_toastPanel.Modulate     = new Color(1f, 1f, 1f, 0f);   // invisible al inicio

			var style = new StyleBoxFlat { BgColor = new Color(0.06f, 0.10f, 0.20f, 0.95f) };
			style.SetCornerRadiusAll(8);
			style.SetBorderWidthAll(1);
			style.BorderColor        = new Color(0.60f, 0.76f, 1.00f, 0.55f);
			style.ContentMarginLeft  = style.ContentMarginRight  = 22;
			style.ContentMarginTop   = style.ContentMarginBottom = 10;
			_toastPanel.AddThemeStyleboxOverride("panel", style);

			_toastLabel = new Label();
			_toastLabel.HorizontalAlignment = HorizontalAlignment.Center;
			_toastLabel.VerticalAlignment   = VerticalAlignment.Center;
			_toastLabel.AddThemeFontSizeOverride("font_size", 18);
			_toastLabel.AddThemeColorOverride("font_color", Colors.White);
			_toastLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
			_toastPanel.AddChild(_toastLabel);

			AddChild(_toastPanel);   // añadir al CanvasLayer, no al VBox
		}

		/// <summary>Encola un mensaje de notificación flotante.</summary>
		public void ShowToast(string message)
		{
			_toastQueue.Enqueue(message);
			if (!_toastShowing) _ = ShowNextToastAsync();
		}

		private async Task ShowNextToastAsync()
		{
			if (_toastQueue.Count == 0) { _toastShowing = false; return; }
			_toastShowing  = true;
			_toastLabel.Text = _toastQueue.Dequeue();

			// Fade in
			var tween = CreateTween();
			tween.TweenProperty(_toastPanel, "modulate", Colors.White, 0.25f);
			await ToSignal(tween, Tween.SignalName.Finished);

			// Esperar 3 s
			await ToSignal(GetTree().CreateTimer(3.0), SceneTreeTimer.SignalName.Timeout);

			// Fade out
			tween = CreateTween();
			tween.TweenProperty(_toastPanel, "modulate", new Color(1f, 1f, 1f, 0f), 0.35f);
			await ToSignal(tween, Tween.SignalName.Finished);

			// Siguiente mensaje
			_ = ShowNextToastAsync();
		}

		// ================================================================
		//  ACCIONES CONTEXTUALES
		// ================================================================

		private void RebuildUnitActions(UnitType type, int q, int r, int remaining, bool isFortified)
		{
			foreach (var child in _unitActionsColumn.GetChildren()) child.QueueFree();

			var stats    = UnitTypeData.GetStats(type);
			var tileType = _map.GetTileType(q, r);
			var existing = _map.GetImprovement(q, r);

			if (isFortified)
			{
				// Unidad fortificada — mostrar estado y cómo salir
				var row = HBox(20);
				row.MouseFilter = Control.MouseFilterEnum.Ignore;
				row.AddChild(Lbl("🛡  FORTIFICADA — mueve para desactivar", 17, CFort));
				row.AddChild(Spacer());
				row.AddChild(KeyBadge("Click D", "deseleccionar"));
				_unitActionsColumn.AddChild(row);
				return;
			}

			if (stats.CanFoundCity)
			{
				// ── Colono ────────────────────────────────────────────────
				var row = HBox(20);
				row.MouseFilter = Control.MouseFilterEnum.Ignore;
				bool canFound = _cityManager.CanFoundAt(q, r);
				row.AddChild(ActionBadge("B", "Fundar Ciudad", canFound));
				if (!canFound)
					row.AddChild(Lbl("  (demasiado cerca de otra ciudad)", 15, TextDim));
				row.AddChild(Spacer());
				var skipBtn1 = SkipButton(); row.AddChild(skipBtn1);
				row.AddChild(KeyBadge("Click D", "deseleccionar"));
				_unitActionsColumn.AddChild(row);
			}
			else if (stats.CanBuildImprovements)
			{
				// ── Constructor ───────────────────────────────────────────
				bool tValid   = tileType.HasValue;
				bool notOcean = tValid && tileType != TileType.Ocean && tileType != TileType.Coast;
				bool notMount = tValid && tileType != TileType.Mountains;
				bool notForest= tValid && tileType != TileType.Forest;
				bool isPlainsGrass = tValid && (tileType == TileType.Plains || tileType == TileType.Grassland);

				bool canIrr  = notOcean && notMount && notForest
							   && existing != TileImprovement.Irrigation
							   && existing != TileImprovement.Farm;
				bool canFarm = isPlainsGrass
							   && existing != TileImprovement.Farm
							   && existing != TileImprovement.Irrigation;
				bool canRoad = notOcean && existing != TileImprovement.Road;
				bool canMine = tValid && (tileType == TileType.Hills || tileType == TileType.Mountains)
							   && existing != TileImprovement.Mine;

				var row = HBox(20);
				row.MouseFilter = Control.MouseFilterEnum.Ignore;

				if (!canIrr && !canFarm && !canRoad && !canMine)
					row.AddChild(Lbl("Sin mejoras disponibles en este terreno", 17, TextDim));
				else
				{
					if (canIrr)  row.AddChild(ActionBadge("I", "Riego",  true));
					if (canFarm) row.AddChild(ActionBadge("G", "Granja", true));
					if (canRoad) row.AddChild(ActionBadge("R", "Camino", true));
					if (canMine) row.AddChild(ActionBadge("M", "Mina",   true));
				}

				row.AddChild(Spacer());
				var skipBtn2 = SkipButton(); row.AddChild(skipBtn2);
				row.AddChild(KeyBadge("Click D", "deseleccionar"));
				_unitActionsColumn.AddChild(row);
			}
			else
			{
				// ── Unidades de combate ───────────────────────────────────
				var row = HBox(20);
				row.MouseFilter = Control.MouseFilterEnum.Ignore;
				if (remaining > 0)
					row.AddChild(ActionBadge("F", "Fortificar", true));
				row.AddChild(Spacer());
				var skipBtn3 = SkipButton(); row.AddChild(skipBtn3);
				row.AddChild(KeyBadge("Espacio", "siguiente unidad"));
				row.AddChild(KeyBadge("Click D", "deseleccionar"));
				_unitActionsColumn.AddChild(row);
			}
		}

		// ================================================================
		//  SIGNALS
		// ================================================================

		private void WireSignals()
		{
			_unitManager.UnitSelected    += OnUnitSelected;
			_unitManager.UnitDeselected  += OnUnitDeselected;
			_unitManager.TileHovered     += OnTileHovered;
			_unitManager.CitySelected    += OnCitySelected;
			_unitManager.CityDeselected  += OnCityDeselected;
			_unitManager.CombatEvent     += ShowToast;
			// Lambdas almacenadas como campos para poder desuscribirse en _ExitTree
			_onResearchRequired = () => SetActivePanel(4);
			_onOpenTechPicker   = () => SetActivePanel(4);
			_unitManager.ResearchRequired += _onResearchRequired;
			_unitManager.OpenTechPicker   += _onOpenTechPicker;

			// C# events para ejércitos (Action, no Godot signal)
			_unitManager.ArmySelectedEvent   += OnArmySelected;
			_unitManager.ArmyDeselectedEvent += OnArmyDeselected;

			// Señales de GameManager — granulares: cada recurso tiene su propia señal
			// para que el HUD no necesite leer el estado global en _Process ni en OnTurnChanged.
			GameManager.Instance.TurnChanged    += OnTurnChanged;
			GameManager.Instance.GoldChanged    += OnGoldChanged;
			GameManager.Instance.ScienceChanged += OnScienceChanged;
			GameManager.Instance.TechResearched += OnTechResearched;

			_cityManager.CityEvent += ShowToast;
		}

		/// <summary>
		/// Desuscribe todos los eventos externos para permitir que el GC colecte este nodo
		/// cuando sea eliminado con QueueFree(). Sin esto, los managers (UnitManager, GameManager,
		/// CityManager) retienen una referencia fuerte a GameHUD — memory leak garantizado.
		/// </summary>
		public override void _ExitTree()
		{
			// ── UnitManager signals ──────────────────────────────────────────
			if (_unitManager != null)
			{
				_unitManager.UnitSelected    -= OnUnitSelected;
				_unitManager.UnitDeselected  -= OnUnitDeselected;
				_unitManager.TileHovered     -= OnTileHovered;
				_unitManager.CitySelected    -= OnCitySelected;
				_unitManager.CityDeselected  -= OnCityDeselected;
				_unitManager.CombatEvent     -= ShowToast;
				if (_onResearchRequired != null) _unitManager.ResearchRequired -= _onResearchRequired;
				if (_onOpenTechPicker   != null) _unitManager.OpenTechPicker   -= _onOpenTechPicker;
				_unitManager.ArmySelectedEvent   -= OnArmySelected;
				_unitManager.ArmyDeselectedEvent -= OnArmyDeselected;
			}

			// ── GameManager events ───────────────────────────────────────────
			if (GameManager.Instance != null)
			{
				GameManager.Instance.TurnChanged    -= OnTurnChanged;
				GameManager.Instance.GoldChanged    -= OnGoldChanged;
				GameManager.Instance.ScienceChanged -= OnScienceChanged;
				GameManager.Instance.TechResearched -= OnTechResearched;
			}

			// ── CityManager signals ──────────────────────────────────────────
			if (_cityManager != null)
				_cityManager.CityEvent -= ShowToast;

			// ── Army CompositionChanged (si uno está seleccionado al destruirse) ─
			if (_displayedArmy != null)
				_displayedArmy.CompositionChanged -= OnArmyCompositionChanged;
		}

		private void OnArmySelected(Army army)
		{
			SetActivePanel(5);
			RefreshArmyPanel(army);

			// Suscribirse a cambios de composición mientras el ejército esté seleccionado
			army.CompositionChanged += OnArmyCompositionChanged;
		}

		private void OnArmyDeselected()
		{
			if (_displayedArmy != null)
				_displayedArmy.CompositionChanged -= OnArmyCompositionChanged;
			_displayedArmy = null;
			SetActivePanel(0);
		}

		private void OnArmyCompositionChanged(Army army)
		{
			if (army == _displayedArmy)
				RefreshArmyPanel(army);
		}

		private void OnUnitSelected(string name, Color civColor, int remaining, int max,
									 int unitTypeInt, int q, int r, bool isFortified)
		{
			SetActivePanel(1);

			_unitNameLabel.Text = name;
			_unitNameLabel.AddThemeColorOverride("font_color", civColor.Lightened(0.22f));
			_unitMovesLabel.Text = MoveDots(remaining, max);

			RebuildUnitActions((UnitType)unitTypeInt, q, r, remaining, isFortified);
		}

		private void OnUnitDeselected()
		{
			SetActivePanel(0);
		}

		private void OnCitySelected(int q, int r)
		{
			_selCityQ = q; _selCityR = r;
			var city = _cityManager.GetCityAt(q, r);
			if (city == null) return;
			SetActivePanel(2);
			RefreshCityPanel(city);
		}

		private void OnCityDeselected()
		{
			if (_selCityQ >= 0) SetActivePanel(0);
			_selCityQ = _selCityR = -1;
		}

		private void OnTileHovered(string tileName, int food, int prod, int moveCost)
		{
			_tileNameLabel.Text = tileName;

			var parts = new List<string>();
			if (food     > 0) parts.Add(RichYield("🌾", food));
			if (prod     > 0) parts.Add(RichYield("⚒", prod));
			if (moveCost > 1) parts.Add($"👣  Mov {moveCost}");

			_tileYieldsLabel.Text = parts.Count > 0 ? string.Join("    ", parts) : "Sin rendimiento";
		}

		// ── Handlers de recursos — cada uno reacciona a su propia señal ────────

		/// <summary>
		/// Solo actualiza el contador de turno y los paneles contextuales.
		/// Los labels de gold/science reaccionan a sus propias señales (GoldChanged, ScienceChanged).
		/// </summary>
		private void OnTurnChanged(int turn)
		{
			_turnLabel.Text = $"TURNO  {turn}";

			// Refrescar barra de investigación (los turnos restantes dependen del turno)
			RefreshResearchTopBar();
			if (_researchActivePanel.Visible) RefreshResearchPanel();

			// Refrescar panel de ciudad si está abierto (producción/comida avanzaron)
			if (_selCityQ >= 0 && _cityInfoPanel.Visible)
			{
				var city = _cityManager.GetCityAt(_selCityQ, _selCityR);
				if (city != null) RefreshCityPanel(city);
			}
		}

		/// <summary>
		/// Actualiza el label de oro. Reacciona a GameManager.GoldChanged
		/// (emitida por ApplyGoldDelta y LoadFrom — nunca hay polling en _Process).
		/// </summary>
		private void OnGoldChanged(int amount, int delta)
		{
			string sign     = delta >= 0 ? "+" : "";
			_goldLabel.Text = $"💰  {amount}   ({sign}{delta} / t)";
			_goldLabel.AddThemeColorOverride("font_color", delta >= 0 ? CGold : new Color(1f, 0.36f, 0.26f));
		}

		/// <summary>
		/// Actualiza el label de ciencia y la barra de investigación.
		/// Reacciona a GameManager.ScienceChanged.
		/// </summary>
		private void OnScienceChanged(int amount, int delta)
		{
			int sciPerTurn     = _cityManager.GetTotalSciencePerTurn(0);
			_scienceLabel.Text = $"🔬  {amount}   (+{sciPerTurn}/t)";

			// La ciencia acumulada afecta los turnos restantes de investigación
			RefreshResearchTopBar();
			if (_researchActivePanel.Visible) RefreshResearchPanel();
		}

		// ================================================================
		//  PANEL ACTIVO  (0=hint  1=unit  2=city)
		// ================================================================

		private void SetActivePanel(int which)
		{
			bool hasU  = which == 1;
			bool hasC  = which == 2;
			bool hasRA = which == 3;
			bool hasTP = which == 4;
			bool hasA  = which == 5;   // ejército
			bool hasH  = which == 0;

			GetContainerOf(_unitInfoPanel).Visible        = hasU;
			_unitInfoPanel.Visible                        = hasU;
			GetContainerOf(_cityInfoPanel).Visible        = hasC;
			_cityInfoPanel.Visible                        = hasC;
			GetContainerOf(_armyInfoPanel).Visible        = hasA;
			_armyInfoPanel.Visible                        = hasA;
			GetContainerOf(_researchActivePanel).Visible  = hasRA;
			_researchActivePanel.Visible                  = hasRA;
			GetContainerOf(_techPickerPanel).Visible      = hasTP;
			_techPickerPanel.Visible                      = hasTP;
			GetContainerOf(_hintPanel).Visible            = hasH;
			_hintPanel.Visible                            = hasH;

			if (hasRA) RefreshResearchPanel();
			if (hasTP) RebuildTechPicker();
		}

		private static Control GetContainerOf(Control inner)
			=> inner.HasMeta("container")
			   ? (Control)inner.GetMeta("container").AsGodotObject()
			   : inner;

		// ================================================================
		//  REFRESH DE CIUDAD
		// ================================================================

		private void RefreshCityPanel(City city)
		{
			_cityNameLabel.Text = $"★  {city.CityName.ToUpper()}";

			string goldStr = city.GoldPerTurn > 0 ? $"   💰 +{city.GoldPerTurn}" : "";
			string maintStr = city.MaintenanceCost > 0 ? $"  (-{city.MaintenanceCost} mant.)" : "";
			_cityPopLabel.Text = $"Pop {city.Population}   🌾 +{city.FoodPerTurn}   ⚒ +{city.ProdPerTurn}{goldStr}{maintStr}";

			_foodBar.MinValue = 0; _foodBar.MaxValue = city.FoodThreshold; _foodBar.Value = city.FoodStored;
			int tg = city.FoodPerTurn > 0
				? Mathf.CeilToInt((city.FoodThreshold - city.FoodStored) / (float)city.FoodPerTurn) : 999;
			_foodLabel.Text = $"{city.FoodStored} / {city.FoodThreshold}  ({tg} t)";

			if (city.BuildingUnit.HasValue)
			{
				var s = UnitTypeData.GetStats(city.BuildingUnit.Value);
				_prodBar.MinValue = 0; _prodBar.MaxValue = s.ProductionCost; _prodBar.Value = city.ProdStored;
				int tb = city.ProdPerTurn > 0
					? Mathf.CeilToInt((s.ProductionCost - city.ProdStored) / (float)city.ProdPerTurn) : 999;
				_prodLabel.Text = $"{s.DisplayName}  {city.ProdStored}/{s.ProductionCost}  ({tb} t)";
			}
			else if (city.BuildingBuilding.HasValue)
			{
				var s = BuildingTypeData.GetStats(city.BuildingBuilding.Value);
				_prodBar.MinValue = 0; _prodBar.MaxValue = s.ProductionCost; _prodBar.Value = city.ProdStored;
				int tb = city.ProdPerTurn > 0
					? Mathf.CeilToInt((s.ProductionCost - city.ProdStored) / (float)city.ProdPerTurn) : 999;
				_prodLabel.Text = $"🏛 {s.DisplayName}  {city.ProdStored}/{s.ProductionCost}  ({tb} t)";
			}
			else
			{
				_prodBar.MinValue = 0; _prodBar.MaxValue = 1; _prodBar.Value = 0;
				_prodLabel.Text   = "Sin producción activa";
			}

			// Edificios construidos
			if (city.Buildings.Count > 0)
			{
				var names = new System.Collections.Generic.List<string>();
				foreach (var b in city.Buildings)
					names.Add(BuildingTypeData.GetStats(b).DisplayName);
				_cityBuildingsLabel.Text = "🏛 " + string.Join(", ", names);
			}
			else
			{
				_cityBuildingsLabel.Text = "";
			}

			RebuildQueueButtons(city);
		}

		private void RebuildQueueButtons(City city)
		{
			var gm = GameManager.Instance;

			// ── Fila de unidades ──────────────────────────────────────────
			foreach (var child in _unitQueueRow.GetChildren()) child.QueueFree();

			var cbNone = QueueBtn("—", !city.BuildingUnit.HasValue && !city.BuildingBuilding.HasValue);
			cbNone.Pressed += () => { _cityManager.SetProductionQueue(city, (UnitType?)null); RefreshCityPanel(city); };
			_unitQueueRow.AddChild(cbNone);

			foreach (UnitType t in System.Enum.GetValues<UnitType>())
			{
				var s        = UnitTypeData.GetStats(t);
				var reqTech  = TechnologyData.RequiredTechForUnit(t);
				bool unlocked= reqTech == null || gm.HasTech(reqTech.Value);
				bool act     = city.BuildingUnit == t;

				string label = unlocked
					? $"{s.DisplayName}\n{s.ProductionCost} ⚒"
					: $"{s.DisplayName}\nreq: {TechnologyData.GetStats(reqTech!.Value).DisplayName}";

				var btn = QueueBtn(label, act, unlocked);
				if (unlocked)
				{
					var cap = t;
					btn.Pressed += () => { _cityManager.SetProductionQueue(city, cap); RefreshCityPanel(city); };
				}
				_unitQueueRow.AddChild(btn);
			}

			// ── Fila de edificios ─────────────────────────────────────────
			foreach (var child in _buildingQueueRow.GetChildren()) child.QueueFree();

			foreach (BuildingType bt in System.Enum.GetValues<BuildingType>())
			{
				if (city.Buildings.Contains(bt)) continue;   // ya construido
				var s        = BuildingTypeData.GetStats(bt);
				var reqTech  = TechnologyData.RequiredTechForBuilding(bt);
				bool unlocked= reqTech == null || gm.HasTech(reqTech.Value);
				bool act     = city.BuildingBuilding == bt;

				string label = unlocked
					? $"{s.DisplayName}\n{s.ProductionCost} ⚒"
					: $"{s.DisplayName}\nreq: {TechnologyData.GetStats(reqTech!.Value).DisplayName}";

				var btn = QueueBtn(label, act, unlocked);
				if (unlocked)
				{
					var cap = bt;
					btn.Pressed += () => { _cityManager.SetProductionQueue(city, cap); RefreshCityPanel(city); };
				}
				_buildingQueueRow.AddChild(btn);
			}
		}

		// ================================================================
		//  FACTORY HELPERS
		// ================================================================

		private static string RichYield(string icon, int val) => $"{icon} +{val}";

		private static Label Header(string text)
		{
			var l = new Label { Text = text };
			l.AddThemeColorOverride("font_color", Gold);
			l.AddThemeFontSizeOverride("font_size", 16);
			l.MouseFilter = Control.MouseFilterEnum.Ignore;
			return l;
		}

		private static Label Lbl(string text, int size, Color color)
		{
			var l = new Label { Text = text };
			l.AddThemeColorOverride("font_color", color);
			l.AddThemeFontSizeOverride("font_size", size);
			l.MouseFilter = Control.MouseFilterEnum.Ignore;
			return l;
		}

		private static Control KeyBadge(string key, string action)
		{
			var row = HBox(6);
			row.MouseFilter = Control.MouseFilterEnum.Ignore;

			var badge = new Label { Text = key };
			badge.AddThemeColorOverride("font_color", Colors.White);
			badge.AddThemeFontSizeOverride("font_size", 16);
			badge.MouseFilter = Control.MouseFilterEnum.Ignore;
			var s = new StyleBoxFlat { BgColor = new Color(0.22f, 0.28f, 0.44f) };
			s.SetCornerRadiusAll(4);
			s.ContentMarginLeft = 8; s.ContentMarginRight  = 8;
			s.ContentMarginTop  = 4; s.ContentMarginBottom = 4;
			badge.AddThemeStyleboxOverride("normal", s);

			row.AddChild(badge);
			row.AddChild(Lbl(action, 19, TextHint));
			return row;
		}

		private static Control ActionBadge(string key, string action, bool enabled)
		{
			var row = HBox(8);
			row.MouseFilter = Control.MouseFilterEnum.Ignore;

			Color badgeBg = enabled ? new Color(0.16f, 0.38f, 0.62f) : new Color(0.18f, 0.20f, 0.26f);
			Color textCol = enabled ? CAction : TextDim;

			var badge = new Label { Text = key };
			badge.AddThemeColorOverride("font_color", enabled ? Colors.White : new Color(0.5f, 0.5f, 0.5f));
			badge.AddThemeFontSizeOverride("font_size", 18);
			badge.MouseFilter = Control.MouseFilterEnum.Ignore;
			var s = new StyleBoxFlat { BgColor = badgeBg };
			s.SetCornerRadiusAll(5);
			s.ContentMarginLeft = 11; s.ContentMarginRight  = 11;
			s.ContentMarginTop  = 5;  s.ContentMarginBottom = 5;
			badge.AddThemeStyleboxOverride("normal", s);

			row.AddChild(badge);
			row.AddChild(Lbl(action, 22, textCol));
			return row;
		}

		private Control SkipButton()
		{
			var btn = new Button { Text = "[S] Saltear" };
			btn.AddThemeStyleboxOverride("normal",  RoundedBtn(new Color(0.22f, 0.24f, 0.32f), 5));
			btn.AddThemeStyleboxOverride("hover",   RoundedBtn(new Color(0.30f, 0.34f, 0.46f), 5));
			btn.AddThemeStyleboxOverride("pressed", RoundedBtn(new Color(0.16f, 0.18f, 0.26f), 5));
			btn.AddThemeStyleboxOverride("focus",   RoundedBtn(new Color(0.22f, 0.24f, 0.32f), 5));
			btn.AddThemeColorOverride("font_color", TextDim);
			btn.AddThemeFontSizeOverride("font_size", 14);
			btn.Pressed += () => _unitManager.TrySkipUnit();
			return btn;
		}

		private static Control Spacer()
		{
			var s = new Control();
			s.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			s.MouseFilter         = Control.MouseFilterEnum.Ignore;
			return s;
		}

		private static Button QueueBtn(string text, bool active, bool unlocked = true)
		{
			var btn = new Button { Text = text };
			btn.CustomMinimumSize = new Vector2(92, 64);
			btn.Disabled          = !unlocked;
			Color col, colH;
			if (!unlocked)      { col = new Color(0.18f, 0.20f, 0.26f); colH = col; }
			else if (active)    { col = BtnGreen;  colH = BtnGreenH; }
			else                { col = BtnBlue;   colH = BtnBlueH;  }
			btn.AddThemeStyleboxOverride("normal",   RoundedBtn(col,  6));
			btn.AddThemeStyleboxOverride("hover",    RoundedBtn(colH, 6));
			btn.AddThemeStyleboxOverride("pressed",  RoundedBtn(col,  6));
			btn.AddThemeStyleboxOverride("focus",    RoundedBtn(col,  6));
			btn.AddThemeStyleboxOverride("disabled", RoundedBtn(col,  6));
			btn.AddThemeColorOverride("font_color",          unlocked ? Colors.White : new Color(0.5f, 0.5f, 0.5f));
			btn.AddThemeColorOverride("font_disabled_color", new Color(0.5f, 0.5f, 0.5f));
			btn.AddThemeFontSizeOverride("font_size", 14);
			return btn;
		}

		private static string MoveDots(int remaining, int max)
		{
			var sb = new StringBuilder("Movimiento:  ");
			for (int i = 0; i < max; i++)
				sb.Append(i < remaining ? "● " : "○ ");
			return sb.ToString().TrimEnd();
		}

		// ── Layout helpers ────────────────────────────────────────────────

		private static PanelContainer Inset(Color bg)
		{
			var p = new PanelContainer();
			p.AddThemeStyleboxOverride("panel", RoundedPanel(bg, 0));
			return p;
		}

		private static MarginContainer Margin(Control parent, int l, int r, int t, int b)
		{
			var m = new MarginContainer();
			m.MouseFilter = Control.MouseFilterEnum.Ignore;
			m.AddThemeConstantOverride("margin_left",   l);
			m.AddThemeConstantOverride("margin_right",  r);
			m.AddThemeConstantOverride("margin_top",    t);
			m.AddThemeConstantOverride("margin_bottom", b);
			parent.AddChild(m);
			return m;
		}

		private static HBoxContainer HBox(int sep)
		{
			var h = new HBoxContainer();
			h.AddThemeConstantOverride("separation", sep);
			h.MouseFilter = Control.MouseFilterEnum.Ignore;
			return h;
		}

		private static VBoxContainer VBox(int sep)
		{
			var v = new VBoxContainer();
			v.AddThemeConstantOverride("separation", sep);
			v.MouseFilter = Control.MouseFilterEnum.Ignore;
			return v;
		}

		private static Control Divider()
		{
			var sep   = new VSeparator();
			var style = new StyleBoxFlat
			{
				BgColor            = new Color(0.30f, 0.36f, 0.50f, 0.40f),
				ContentMarginLeft  = 1,
				ContentMarginRight = 1,
			};
			sep.AddThemeStyleboxOverride("separator", style);
			return sep;
		}

		private static StyleBoxFlat RoundedPanel(Color bg, int radius)
		{
			var s = new StyleBoxFlat { BgColor = bg };
			s.SetCornerRadiusAll(radius);
			s.SetBorderWidthAll(0);
			return s;
		}

		private static StyleBoxFlat RoundedBtn(Color bg, int radius)
		{
			var s = new StyleBoxFlat { BgColor = bg };
			s.SetCornerRadiusAll(radius);
			s.ContentMarginLeft   = 8;
			s.ContentMarginRight  = 8;
			s.ContentMarginTop    = 6;
			s.ContentMarginBottom = 6;
			return s;
		}
	}
}
