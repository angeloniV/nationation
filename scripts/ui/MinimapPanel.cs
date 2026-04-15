using Godot;
using Natiolation.Cities;
using Natiolation.Core;
using Natiolation.Map;
using Natiolation.Units;

namespace Natiolation.UI
{
	/// <summary>
	/// Minimapa en la esquina inferior derecha.
	///
	/// Arquitectura:
	///   • Terreno: Image de 240×120 px generada una vez en _Ready (4 px por tile-unit).
	///   • Elementos dinámicos (unidades, cámara): Control._Draw() con QueueRedraw() cada frame.
	///   • Click / drag en el minimapa desplaza la cámara.
	///
	/// Geometría de conversión:
	///   col_unit = q + r * 0.5          → minimap_x = col_unit * TileW
	///   row_unit = r                     → minimap_y = row_unit * TileH
	///   world.X  = HexSize*√3 * col_unit → col_unit = world.X / (HexSize*√3)
	///   world.Z  = HexSize*1.5 * row_unit→ row_unit = world.Z / (HexSize*1.5)
	/// </summary>
	public partial class MinimapPanel : CanvasLayer
	{
		// ── Constantes de layout ─────────────────────────────────────────
		private const int TileW     = 4;    // px por col-unit en el minimapa
		private const int TileH     = 4;    // px por row-unit en el minimapa
		private const int MapW      = 60;   // tiles columnas (debe coincidir con MapManager)
		private const int MapH      = 40;   // tiles filas

		// Ancho total = (MapW + MapH*0.5) * TileW = 80 * 4 = 320
		// Alto total  = MapH * TileH              = 40 * 4 = 160
		private const int MinimapW  = 320;
		private const int MinimapH  = 160;

		// Escalas mundo→pixel
		private static readonly float ColScale = HexTile3D.HexSize * MathF.Sqrt(3f); // mundo-X por col-unit
		private static readonly float RowScale = HexTile3D.HexSize * 1.5f;           // mundo-Z por row-unit

		// ── Refs ────────────────────────────────────────────────────────
		private MapManager  _map    = null!;
		private UnitManager _units  = null!;
		private MapCamera   _camera = null!;
		private CityManager _cities = null!;

		private ImageTexture _terrainTex = null!;
		private MinimapView  _view       = null!;   // control de dibujo dinámico

		private bool _dragging      = false;
		private bool _terrainDirty = true;   // arranca sucia → se genera en el primer draw

		// ================================================================

		public override void _Ready()
		{
			_map    = GetNode<MapManager>  ("/root/Main/MapManager");
			_units  = GetNode<UnitManager> ("/root/Main/UnitManager");
			_camera = GetNode<MapCamera>   ("/root/Main/MapCamera");
			_cities = GetNode<CityManager> ("/root/Main/CityManager");

			// Escuchar cambios de fog of war
			_map.TilesRevealed += () => _terrainDirty = true;

			BuildUI();
		}

		// ================================================================
		//  CONSTRUCCIÓN DE UI
		// ================================================================

		private void BuildUI()
		{
			// ── Panel exterior ──────────────────────────────────────────
			var outer = new PanelContainer();
			outer.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
			outer.GrowHorizontal = Control.GrowDirection.Begin;
			outer.GrowVertical   = Control.GrowDirection.Begin;
			outer.OffsetRight  = -12f;
			outer.OffsetBottom = -220f;   // justo encima del bottom bar del HUD (altura 190 + margen)
			outer.CustomMinimumSize = new Vector2(MinimapW + 16, MinimapH + 30);
			outer.AddThemeStyleboxOverride("panel", PanelStyle());
			AddChild(outer);

			var vbox = new VBoxContainer();
			vbox.AddThemeConstantOverride("separation", 4);
			outer.AddChild(vbox);

			// Título
			var title = new Label { Text = "MAPA" };
			title.AddThemeColorOverride("font_color",    new Color(1.00f, 0.82f, 0.14f));
			title.AddThemeFontSizeOverride("font_size",  11);
			title.HorizontalAlignment = HorizontalAlignment.Center;
			vbox.AddChild(title);

			// Área de dibujo
			var margin = new MarginContainer();
			margin.AddThemeConstantOverride("margin_left",   6);
			margin.AddThemeConstantOverride("margin_right",  6);
			margin.AddThemeConstantOverride("margin_bottom", 6);
			vbox.AddChild(margin);

			_view = new MinimapView
			{
				Panel             = this,
				CustomMinimumSize = new Vector2(MinimapW, MinimapH),
			};
			_view.MouseFilter = Control.MouseFilterEnum.Stop;
			_view.GuiInput += OnMinimapInput;
			margin.AddChild(_view);
		}

		// ================================================================
		//  INPUT DEL MINIMAPA → MOVER CÁMARA
		// ================================================================

		private void OnMinimapInput(InputEvent @event)
		{
			if (@event is InputEventMouseButton mb)
			{
				if (mb.ButtonIndex == MouseButton.Left)
				{
					_dragging = mb.Pressed;
					if (_dragging) PanCameraTo(mb.Position);
				}
			}
			else if (@event is InputEventMouseMotion mm && _dragging)
			{
				PanCameraTo(mm.Position);
			}
		}

		private void PanCameraTo(Vector2 mmPos)
		{
			float worldX = mmPos.X / TileW * ColScale;
			float worldZ = mmPos.Y / TileH * RowScale;
			_camera.FocusOn(new Vector3(worldX, 0f, worldZ));
		}

		// ================================================================
		//  GENERACIÓN DE TEXTURA DE TERRENO
		// ================================================================

		private static readonly Color FogUnexplored = new(0.04f, 0.04f, 0.06f);  // nunca visto
		private static readonly Color FogExplored   = new(0.35f, 0.37f, 0.42f);  // visto pero en niebla

		private ImageTexture BuildTerrainTexture()
		{
			var img = Image.CreateEmpty(MinimapW, MinimapH, false, Image.Format.Rgba8);
			img.Fill(FogUnexplored);

			for (int q = 0; q < MapW; q++)
			{
				for (int r = 0; r < MapH; r++)
				{
					var tile  = _map.GetTile(q, r);
					var tType = _map.GetTileType(q, r);
					if (tile == null || tType == null) continue;

					Color color;
					if (tile.TileVisible)
						color = tType.Value.MapColor();                        // visible: color real
					else if (tile.WasExplored)
						color = tType.Value.MapColor().Lerp(FogExplored, 0.55f); // explorado: atenuado
					else
						continue;                                               // inexplorado: fondo ya puesto

					int startX = Mathf.RoundToInt((q + r * 0.5f) * TileW);
					int startY = r * TileH;

					for (int dy = 0; dy < TileH; dy++)
					{
						for (int dx = 0; dx < TileW; dx++)
						{
							int ix = startX + dx;
							int iy = startY + dy;
							if (ix >= 0 && ix < MinimapW && iy >= 0 && iy < MinimapH)
								img.SetPixel(ix, iy, color);
						}
					}
				}
			}

			return ImageTexture.CreateFromImage(img);
		}

		// ================================================================
		//  DIBUJO DINÁMICO (delegado a MinimapView)
		// ================================================================

		internal void DrawDynamic(CanvasItem ci)
		{
			// 1. Textura de terreno — se regenera cuando el fog of war cambia
			if (_terrainDirty)
			{
				_terrainTex  = BuildTerrainTexture();
				_terrainDirty = false;
			}
			ci.DrawTextureRect(_terrainTex,
				new Rect2(Vector2.Zero, new Vector2(MinimapW, MinimapH)), false);

			// 2. Ciudades — estrella (cruz + diagonal) con color de civilización
			foreach (var city in _cities.AllCities)
			{
				var cp = TileToMinimap(city.Q, city.R);
				// Borde negro
				ci.DrawRect(new Rect2(cp - new Vector2(5f, 1.5f), new Vector2(10f, 3f)), Colors.Black, true);
				ci.DrawRect(new Rect2(cp - new Vector2(1.5f, 5f), new Vector2(3f, 10f)), Colors.Black, true);
				// Cruz blanca
				ci.DrawRect(new Rect2(cp - new Vector2(4f, 1f),   new Vector2(8f, 2f)), Colors.White, true);
				ci.DrawRect(new Rect2(cp - new Vector2(1f, 4f),   new Vector2(2f, 8f)), Colors.White, true);
				// Punto central en color de civilización
				ci.DrawRect(new Rect2(cp - Vector2.One * 2f, Vector2.One * 4f), city.CivColor, true);
			}

			// 3. Unidades — dot de 4×4 px con borde oscuro
			foreach (var unit in _units.AllUnits)
			{
				var mmPos = TileToMinimap(unit.Q, unit.R);
				ci.DrawRect(new Rect2(mmPos - Vector2.One * 3f, Vector2.One * 6f),
							Colors.Black, true);
				ci.DrawRect(new Rect2(mmPos - Vector2.One * 2f, Vector2.One * 4f),
							unit.CivColor, true);
			}

			// 4. Indicador de cámara — rectángulo blanco escalado con el zoom
			var camTarget = _camera.CameraTarget;
			var camPx     = WorldToMinimap(camTarget);
			float dist    = _camera.CameraDistance;

			// Aproximación del área visible basada en distancia y ángulo
			// A dist=28 (default), la vista cubre ~50 col-units × ~28 row-units en mundo
			float halfW = dist * 0.90f / ColScale * TileW;
			float halfH = dist * 0.52f / RowScale * TileH;

			var rectPos  = camPx - new Vector2(halfW, halfH);
			var rectSize = new Vector2(halfW * 2f, halfH * 2f);

			// Sombra del rect
			ci.DrawRect(new Rect2(rectPos + Vector2.One, rectSize),
						new Color(0f, 0f, 0f, 0.45f), false, 2f);
			// Rect principal
			ci.DrawRect(new Rect2(rectPos, rectSize),
						new Color(1f, 1f, 1f, 0.85f), false, 1.5f);

			// Cruz central
			ci.DrawLine(camPx - new Vector2(5, 0), camPx + new Vector2(5, 0),
						new Color(1f, 1f, 1f, 0.85f), 1.5f);
			ci.DrawLine(camPx - new Vector2(0, 5), camPx + new Vector2(0, 5),
						new Color(1f, 1f, 1f, 0.85f), 1.5f);
		}

		// ================================================================
		//  CONVERSIÓN DE COORDENADAS
		// ================================================================

		private static Vector2 TileToMinimap(int q, int r)
			=> new((q + r * 0.5f) * TileW + TileW * 0.5f, r * TileH + TileH * 0.5f);

		private static Vector2 WorldToMinimap(Vector3 world)
			=> new(world.X / ColScale * TileW, world.Z / RowScale * TileH);

		// ================================================================
		//  ESTILOS
		// ================================================================

		private static StyleBoxFlat PanelStyle()
		{
			var s = new StyleBoxFlat
			{
				BgColor = new Color(0.04f, 0.06f, 0.09f, 0.92f),
			};
			s.SetBorderWidthAll(1);
			s.BorderColor = new Color(1f, 0.82f, 0.14f, 0.40f);
			s.SetCornerRadiusAll(4);
			s.ContentMarginLeft   = 0;
			s.ContentMarginRight  = 0;
			s.ContentMarginTop    = 6;
			s.ContentMarginBottom = 0;
			return s;
		}

		// ================================================================
		//  CONTROL DE DIBUJO INTERNO
		// ================================================================

		/// <summary>Control lightweight que delega _Draw al panel padre y pide redraw cada frame.</summary>
		private sealed partial class MinimapView : Control
		{
			public MinimapPanel? Panel;

			public override void _Process(double delta) => QueueRedraw();

			public override void _Draw() => Panel?.DrawDynamic(this);
		}
	}
}
