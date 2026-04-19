using Godot;
using System;
using System.Collections.Generic;

namespace Natiolation.Map
{
	/// <summary>
	/// Celda hexagonal 3D — prisma generado con SurfaceTool.
	///
	/// Arquitectura:
	///   • Mesh compartido por TileType (cahé estático).
	///   • Material por-tile para control individual de fog-of-war vía AlbedoColor.
	///   • StaticBody3D con ConvexPolygon para picking exacto con raycast.
	///   • Decoraciones (árboles, picos) creadas al revelar el tile (lazy loading).
	/// </summary>
	public partial class HexTile3D : Node3D
	{
		public int      Q    { get; set; }
		public int      R    { get; set; }
		public TileType Type { get; set; } = TileType.Plains;

		public bool TileVisible  { get; private set; } = false;
		public bool WasExplored  { get; private set; } = false;

		// ── Constantes de escala ──────────────────────────────────────────
		public const float HexSize    = 4.0f;   // radio del circunscrito (vértice)
		public const float TokenHover = 0.55f;  // altura de unidades sobre la superficie

		// ── Alturas de terreno ────────────────────────────────────────────
		public static float GetHeight(TileType t) => t switch
		{
			TileType.Ocean     => 0.20f,
			TileType.Coast     => 0.40f,
			TileType.Plains    => 0.75f,
			TileType.Grassland => 0.80f,
			TileType.Desert    => 0.75f,
			TileType.Tundra    => 0.90f,
			TileType.Hills     => 2.20f,
			TileType.Forest    => 0.85f,
			TileType.Mountains => 4.00f,
			TileType.Arctic    => 1.10f,
			_                  => 0.75f
		};

		// ── Caché de meshes (uno por TileType) ───────────────────────────
		private static readonly Dictionary<TileType, ArrayMesh> _meshCache = new();

		private MeshInstance3D       _meshInst   = null!;
		private StandardMaterial3D   _mat        = null!;
		private Color                _baseColor;
		private bool                 _decorAdded = false;

		// Bordes de río en este tile (se asignan antes de Init)
		private List<int> _riverDirs = new();

		/// <summary>Llama antes de Init() para que AddDecorations() dibuje los bordes de río.</summary>
		public void SetRiverEdges(IEnumerable<int> dirs)
		{
			_riverDirs = new List<int>(dirs);
		}

		// ================================================================
		//  INICIALIZACION
		// ================================================================

		public void Init()
		{
			_baseColor = Type.MapColor();

			// Mesh compartido por tipo
			if (!_meshCache.TryGetValue(Type, out var mesh))
			{
				mesh = BuildPrism(GetHeight(Type), _baseColor);
				_meshCache[Type] = mesh;
			}

			// Material por-tile (controla fog-of-war con AlbedoColor)
			bool water = IsWater(Type);
			_mat = new StandardMaterial3D
			{
				VertexColorUseAsAlbedo = true,
				AlbedoColor            = Colors.Black,   // empieza inexplorado
				Roughness              = GetRoughness(Type),
				Metallic               = GetMetallic(Type),
			};
			if (water)
			{
				_mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
				_mat.AlbedoColor  = new Color(0f, 0f, 0f, 0f);
			}

			_meshInst = new MeshInstance3D
			{
				Mesh             = mesh,
				MaterialOverride = _mat,
				Visible          = false,  // TerrainRenderer gestiona el visual del terreno
			};
			AddChild(_meshInst);

			// Collision para raycast de mouse (picking exacto)
			AddCollision();
		}

		// ================================================================
		//  FOG OF WAR
		// ================================================================

		public void SetVisible(bool visible, bool explored)
		{
			bool wasExploredBefore = WasExplored;

			TileVisible = visible;
			WasExplored = explored || WasExplored;

			// Lazy-load de decoraciones al revelarse por primera vez
			// (TrySpawnNature dentro de AddDecorations registra en NatureRenderer)
			if (TileVisible && !_decorAdded)
			{
				AddDecorations();
				_decorAdded = true;
			}

			// Notificar al shader de terreno (TerrainRenderer lee este pixel cada frame)
			TerrainRenderer.Instance?.UpdateFog(Q, R, TileVisible, WasExplored);

			// Primera exploración → liberar los assets de NatureRenderer para este tile
			if (WasExplored && !wasExploredBefore)
				NatureRenderer.Instance?.SetTileExplored(Q, R);

			// Decoraciones procedurales (hijos de HexTile3D): visibles si explorado.
			// Esto coincide con el comportamiento del shader: tiles en niebla se ven
			// oscurecidos pero no desaparecen una vez explorados.
			SetDecorVisible(WasExplored);
		}

		private void ApplyFog()
		{
			bool water = IsWater(Type);

			if (!TileVisible && !WasExplored)
			{
				// INEXPLORADO — Unshaded + negro puro.
				// Sin Unshaded, el SSAO y la luz ambiente revelan la geometría
				// aunque AlbedoColor sea negro.
				_mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
				_mat.AlbedoColor = water ? new Color(0f, 0f, 0f, 0f)
										 : new Color(0.02f, 0.02f, 0.03f, 1f);
				SetDecorVisible(false);
			}
			else if (!TileVisible) // explorado pero en niebla
			{
				// NIEBLA — Unshaded + gris uniforme, sin que el lighting revele relieve
				_mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
				var fog = new Color(0.22f, 0.24f, 0.28f, water ? 0.55f : 1.0f);
				_mat.AlbedoColor = fog;
				SetDecorVisible(false);
			}
			else // visible
			{
				// VISIBLE — volvemos a shading normal para que la iluminación funcione
				_mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel;
				_mat.AlbedoColor = water ? new Color(1f, 1f, 1f, 0.84f) : Colors.White;
				SetDecorVisible(true);
			}
		}

		private void SetDecorVisible(bool show)
		{
			foreach (var child in GetChildren())
				if (child is Node3D n && !(n is MeshInstance3D m && m == _meshInst))
					n.Visible = show;
		}

		// ================================================================
		//  GENERACION DE MESH — PRISMA HEXAGONAL
		// ================================================================

		private static ArrayMesh BuildPrism(float h, Color topColor)
		{
			var st      = new SurfaceTool();
			st.Begin(Mesh.PrimitiveType.Triangles);

			// Cara superior ligeramente expandida para cubrir la unión con tiles vecinos
			// y evitar Z-fighting entre tiles del mismo tipo.
			var top    = HexRing(h, 1.005f);
			var bot    = HexRing(0f);
			var center = new Vector3(0f, h, 0f);

			// Cara superior — normal hacia arriba
			st.SetNormal(Vector3.Up);
			for (int i = 0; i < 6; i++)
				AddTri(st, center, top[i], top[(i + 1) % 6], topColor, topColor, topColor);

			// Caras laterales — degradado vertical: top=color terreno, bottom=tierra/roca
			// La cara inferior usa un color de tierra/roca para que parezca suelo natural.
			var earthBot = new Color(0.32f, 0.22f, 0.12f);  // tierra marrón
			for (int i = 0; i < 6; i++)
			{
				var a = top[i];
				var b = top[(i + 1) % 6];
				var c = bot[(i + 1) % 6];
				var d = bot[i];

				var  midXZ = new Vector3((a.X + b.X) * .5f, 0, (a.Z + b.Z) * .5f).Normalized();
				var  norm  = midXZ;
				// Variación de luz baked: caras hacia la "luz" (X+, Z-) son más claras
				float lf      = Mathf.Clamp(0.48f + midXZ.X * 0.28f - midXZ.Z * 0.10f, 0.28f, 0.82f);
				var   topSide = topColor.Darkened(1f - lf);
				// Base: tierra marrón con la misma variación de luz
				var   botSide = earthBot.Darkened(1f - lf);

				st.SetNormal(norm);
				// Degradado: vértices superiores (a,b) → topSide, inferiores (c,d) → botSide
				AddTri(st, a, b, c, topSide, topSide, botSide);
				AddTri(st, a, c, d, topSide, botSide, botSide);
			}

			st.GenerateTangents();
			return st.Commit();
		}

		private static void AddTri(SurfaceTool st,
								   Vector3 p0, Vector3 p1, Vector3 p2,
								   Color   c0, Color   c1, Color   c2)
		{
			st.SetColor(c0); st.AddVertex(p0);
			st.SetColor(c1); st.AddVertex(p1);
			st.SetColor(c2); st.AddVertex(p2);
		}

		public static Vector3[] HexRing(float y, float scale = 1f)
		{
			var v = new Vector3[6];
			for (int i = 0; i < 6; i++)
			{
				float a = MathF.PI / 180f * (60f * i + 30f);
				v[i] = new Vector3(HexSize * scale * MathF.Cos(a), y,
								   HexSize * scale * MathF.Sin(a));
			}
			return v;
		}

		// ================================================================
		//  COLLISION (para picking con raycast)
		// ================================================================

		private void AddCollision()
		{
			float h = GetHeight(Type);
			var   body = new StaticBody3D();
			body.SetMeta("hex_q", Q);
			body.SetMeta("hex_r", R);

			// Disco hexagonal delgado en la superficie del tile
			var pts = new Vector3[12];
			for (int i = 0; i < 6; i++)
			{
				float angle = MathF.PI / 180f * (60f * i + 30f);
				float x = HexSize * 0.92f * MathF.Cos(angle);
				float z = HexSize * 0.92f * MathF.Sin(angle);
				pts[i]     = new Vector3(x, h + 0.15f, z);
				pts[i + 6] = new Vector3(x, h - 0.15f, z);
			}
			var shape   = new CollisionShape3D { Shape = new ConvexPolygonShape3D { Points = pts } };
			body.AddChild(shape);
			AddChild(body);
		}

		// ================================================================
		//  DECORACIONES DE TERRENO (lazy, al revelar)
		// ================================================================

		private void AddDecorations()
		{
			float baseH = GetHeight(Type);
			switch (Type)
			{
				case TileType.Forest:    SpawnTrees(baseH);      break;
				case TileType.Mountains: SpawnPeaks(baseH);      break;
				case TileType.Hills:     SpawnHillDome(baseH);   break;
				case TileType.Desert:    SpawnDunes(baseH);      break;
				case TileType.Arctic:    SpawnIce(baseH);        break;
				case TileType.Grassland: SpawnGrass(baseH);      break;
				case TileType.Plains:    SpawnPlainStones(baseH); break;
				case TileType.Ocean:     SpawnWaterDetail(baseH, deep: true);  break;
				case TileType.Coast:     SpawnWaterDetail(baseH, deep: false); break;
				case TileType.Tundra:    SpawnTundra(baseH);     break;
			}
		}

		// ================================================================
		//  MEJORAS (irrigación, caminos, minas)
		// ================================================================

		/// <summary>
		/// Agrega el visual de una mejora de Constructor encima de este tile.
		/// Llamado desde MapManager.SetImprovement() cuando ya fue revelado.
		/// </summary>
		public void AddImprovementVisual(TileImprovement improvement)
		{
			float h = GetHeight(Type);
			switch (improvement)
			{
				case TileImprovement.Irrigation: SpawnIrrigation(h); break;
				case TileImprovement.Farm:        SpawnFarm(h);       break;
				case TileImprovement.Road:        SpawnRoad(h);       break;
				case TileImprovement.Mine:        SpawnMine(h);       break;
			}
		}

		private void SpawnIrrigation(float h)
		{
			var mat = SolidMat(new Color(0.22f, 0.62f, 0.88f), roughness: 0.15f);
			// 3 canales de riego cruzados (0°, 60°, 120°)
			for (int i = 0; i < 3; i++)
			{
				var strip = MeshAt(
					new BoxMesh { Size = new Vector3(HexSize * 0.70f, 0.06f, 0.22f) },
					mat, new Vector3(0f, h + 0.04f, 0f));
				strip.RotationDegrees = new Vector3(0f, i * 60f, 0f);
				AddChild(strip);
			}
		}

		private void SpawnFarm(float h)
		{
			// Campos de cultivo: 5 franjas paralelas verde-amarillo
			var soil = SolidMat(new Color(0.54f, 0.36f, 0.18f), roughness: 0.90f);
			var crop = SolidMat(new Color(0.62f, 0.76f, 0.22f), roughness: 0.80f);
			float rowSpacing = HexSize * 0.26f;
			for (int i = -2; i <= 2; i++)
			{
				// Franja de tierra
				AddChild(MeshAt(
					new BoxMesh { Size = new Vector3(HexSize * 0.80f, 0.05f, rowSpacing * 0.60f) },
					soil, new Vector3(0f, h + 0.03f, i * rowSpacing)));
				// Línea de cultivo
				AddChild(MeshAt(
					new BoxMesh { Size = new Vector3(HexSize * 0.78f, 0.07f, rowSpacing * 0.22f) },
					crop, new Vector3(0f, h + 0.06f, i * rowSpacing)));
			}
		}

		private void SpawnRoad(float h)
		{
			var mat = SolidMat(new Color(0.52f, 0.47f, 0.36f), roughness: 0.95f);
			// Franja central (eje Q)
			AddChild(MeshAt(
				new BoxMesh { Size = new Vector3(HexSize * 1.80f, 0.04f, 0.36f) },
				mat, new Vector3(0f, h + 0.02f, 0f)));
		}

		private void SpawnMine(float h)
		{
			var timber = SolidMat(new Color(0.38f, 0.24f, 0.10f), roughness: 0.88f);
			var ore    = SolidMat(new Color(0.62f, 0.58f, 0.52f), roughness: 0.40f, metallic: 0.70f);

			// Entrada de la mina (arco rectangular)
			AddChild(MeshAt(new BoxMesh { Size = new Vector3(0.80f, 0.68f, 0.12f) },
							timber, new Vector3(0f, h + 0.34f, 0.60f)));

			// Vigas de soporte
			AddChild(MeshAt(new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.07f,
								Height = 0.90f, RadialSegments = 4 },
							timber, new Vector3(-0.38f, h + 0.45f, 0.60f)));
			AddChild(MeshAt(new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.07f,
								Height = 0.90f, RadialSegments = 4 },
							timber, new Vector3( 0.38f, h + 0.45f, 0.60f)));

			// Pila de mineral
			AddChild(MeshAt(new SphereMesh { Radius = 0.34f, Height = 0.34f, RadialSegments = 7, Rings = 4 },
							ore, new Vector3(0.90f, h + 0.17f, -0.50f)));
		}

		private void SpawnTrees(float h)
		{
			// Mezcla de coníferas y árboles de hoja caduca — escala AAA
			var trunkBrown  = SolidMat(new Color(0.25f, 0.13f, 0.04f), roughness: 0.90f);
			var trunkGray   = SolidMat(new Color(0.38f, 0.30f, 0.22f), roughness: 0.88f);
			var darkGreen   = SolidMat(new Color(0.04f, 0.26f, 0.06f), roughness: 0.85f);
			var midGreen    = SolidMat(new Color(0.07f, 0.40f, 0.10f), roughness: 0.83f);
			var litGreen    = SolidMat(new Color(0.12f, 0.54f, 0.16f), roughness: 0.80f);
			var leafGold    = SolidMat(new Color(0.20f, 0.48f, 0.14f), roughness: 0.80f);
			var leafLit     = SolidMat(new Color(0.26f, 0.62f, 0.18f), roughness: 0.78f);

			// 7 árboles con posiciones semi-aleatorias reproducibles por tile
			// Las posiciones se distribuyen cubriendo el tile (radio ~3 unidades)
			int seed = Q * 73856093 ^ R * 19349663;
			(Vector3 p, float s, bool conifer)[] trees =
			{
				(new(-1.8f + (seed&3)*0.3f,   h,  -1.0f + ((seed>>2)&3)*0.3f), 1.00f, true ),
				(new( 1.6f - (seed&1)*0.4f,   h,   0.7f - ((seed>>4)&1)*0.4f), 0.90f, false),
				(new( 0.2f + (seed&2)*0.3f,   h,  -2.2f + ((seed>>6)&2)*0.3f), 1.15f, true ),
				(new(-1.0f + ((seed>>8)&1)*0.3f, h, 1.9f - ((seed>>9)&1)*0.3f), 0.82f, false),
				(new( 2.1f - (seed&3)*0.15f,  h,  -1.4f + ((seed>>3)&2)*0.2f), 0.95f, true ),
				(new(-2.3f + ((seed>>5)&1)*0.4f, h, 0.4f + ((seed>>7)&2)*0.2f), 0.75f, false),
				(new( 0.7f + ((seed>>1)&2)*0.2f, h, 1.2f - ((seed>>4)&2)*0.2f), 1.08f, true ),
			};

			// Árboles de la Nature Kit de Kenney (CC0)
			string[] coniferGlbs = {
				"res://assets/nature/tree_pineRoundA.glb",
				"res://assets/nature/tree_pineRoundB.glb",
				"res://assets/nature/tree_pineRoundC.glb",
				"res://assets/nature/tree_cone_fall.glb",
			};
			string[] deciduousGlbs = {
				"res://assets/nature/tree_simple.glb",
				"res://assets/nature/tree_pineRoundA.glb",
			};

			int tIdx = 0;
			foreach (var (p, s, conifer) in trees)
			{
				string[] glbList = conifer ? coniferGlbs : deciduousGlbs;
				string glbPath   = glbList[tIdx % glbList.Length];
				float rotY       = (Q * 17 + R * 31 + tIdx * 47 + (seed >> (tIdx & 7))) % 360;

				// NatureRenderer disponible: sin Node3D — solo registra el transform en el MultiMesh
				if (!TrySpawnNatureAt(glbPath, p, s * 1.8f, rotY))
				{
					// Fallback procedimental solo cuando NatureRenderer no está disponible
					var g        = new Node3D { Position = p };
					var trunkMat = conifer ? trunkBrown : trunkGray;
					if (conifer)
					{
						g.AddChild(MeshAt(new CylinderMesh { TopRadius=0.09f*s, BottomRadius=0.13f*s, Height=1.0f*s, RadialSegments=6 },
							trunkMat, new Vector3(0, 0.5f*s, 0)));
						float[] ly = { 0.80f, 1.32f, 1.74f, 2.10f };
						float[] br = { 0.88f, 0.68f, 0.50f, 0.32f };
						StandardMaterial3D[] cm = { darkGreen, darkGreen, midGreen, litGreen };
						for (int i = 0; i < 4; i++)
							g.AddChild(MeshAt(new CylinderMesh { TopRadius=0f, BottomRadius=br[i]*s, Height=0.82f*s, RadialSegments=7 },
								cm[i], new Vector3(0, ly[i]*s, 0)));
					}
					else
					{
						g.AddChild(MeshAt(new CylinderMesh { TopRadius=0.08f*s, BottomRadius=0.12f*s, Height=0.80f*s, RadialSegments=6 },
							trunkMat, new Vector3(0, 0.40f*s, 0)));
						g.AddChild(MeshAt(new SphereMesh { Radius=0.72f*s, RadialSegments=8, Rings=5 },
							leafGold, new Vector3(0, 1.30f*s, 0)));
						g.AddChild(MeshAt(new SphereMesh { Radius=0.52f*s, RadialSegments=7, Rings=4 },
							leafLit, new Vector3(0.22f*s, 1.52f*s, 0.18f*s)));
					}
					AddChild(g);
				}
				tIdx++;
			}
		}

		private void SpawnGrass(float h)
		{
			// Lazy-init de materiales compartidos
			_mGrassDark   ??= SolidMat(new Color(0.14f, 0.52f, 0.16f), roughness: 0.92f);
			_mGrassLit    ??= SolidMat(new Color(0.22f, 0.68f, 0.20f), roughness: 0.88f);
			_mGrassFlower ??= SolidMat(new Color(0.92f, 0.88f, 0.22f), roughness: 0.90f);

			(float x, float z, float s)[] tufts =
			{
				(-1.8f,  0.4f, 0.9f), ( 1.6f, -0.6f, 0.8f),
				( 0.2f, -1.8f, 1.0f), (-0.5f,  1.7f, 0.7f),
				( 2.0f,  1.0f, 0.75f),(-2.0f, -1.0f, 0.85f),
				( 0.8f,  0.2f, 0.6f), (-0.9f, -0.3f, 0.7f),
			};

			if (NatureRenderer.Instance != null)
			{
				// ── MultiMesh batching: 8×3 láminas → 2 draw calls, flores → 1 draw call ──
				foreach (var (x, z, s) in tufts)
				{
					for (int i = 0; i < 3; i++)
					{
						BatchMesh(i == 1 ? K_GRASS_LIT : K_GRASS_DARK,
								  _unitBox, i == 1 ? _mGrassLit! : _mGrassDark!,
								  new Vector3(x, h + 0.16f * s, z),
								  new Vector3(0.08f * s, 0.32f * s, 0.04f),
								  -12f + i * 8f, i * 60f);
					}
					if ((x * 7 + z * 11) % 3 == 0)
						BatchMesh(K_GRASS_FLOWER, _unitSphere, _mGrassFlower!,
								  new Vector3(x, h + 0.38f * s, z),
								  new Vector3(0.20f * s, 0.20f * s, 0.20f * s));
				}
				return;
			}

			// ── Fallback: MeshInstance3D individual (sin NatureRenderer) ──────────
			foreach (var (x, z, s) in tufts)
			{
				var g = new Node3D { Position = new Vector3(x, h, z) };
				for (int i = 0; i < 3; i++)
				{
					var blade = MeshAt(new BoxMesh { Size = new Vector3(0.08f*s, 0.32f*s, 0.04f) },
						i == 1 ? _mGrassLit! : _mGrassDark!, new Vector3(0, 0.16f*s, 0));
					blade.RotationDegrees = new Vector3(-12f + i*8f, i*60f, 0);
					g.AddChild(blade);
				}
				if ((x * 7 + z * 11) % 3 == 0)
					g.AddChild(MeshAt(new SphereMesh { Radius=0.10f*s, RadialSegments=5, Rings=3 },
						_mGrassFlower!, new Vector3(0, 0.38f*s, 0)));
				AddChild(g);
			}
		}

		private void SpawnPlainStones(float h)
		{
			_mStoneA ??= SolidMat(new Color(0.55f, 0.52f, 0.46f), roughness: 0.88f);
			_mStoneB ??= SolidMat(new Color(0.68f, 0.64f, 0.56f), roughness: 0.84f);

			(float x, float z, float sx, float sy, float sz, float ry)[] stones =
			{
				(-1.5f,  0.8f, 0.55f, 0.28f, 0.42f, 18f),
				( 1.8f, -0.5f, 0.42f, 0.22f, 0.38f,-15f),
				( 0.3f, -1.6f, 0.62f, 0.32f, 0.48f, 32f),
				(-0.7f,  1.4f, 0.36f, 0.18f, 0.30f,-22f),
				( 2.1f,  0.9f, 0.48f, 0.24f, 0.40f,  8f),
			};

			if (NatureRenderer.Instance != null)
			{
				// 5 piedras → 2 draw calls (stoneA + stoneB), zero MeshInstance3D
				foreach (var (x, z, sx, sy, sz, ry) in stones)
				{
					bool useA = (int)(x + z) % 2 == 0;
					BatchMesh(useA ? K_STONE_A : K_STONE_B,
							  _unitBox, useA ? _mStoneA! : _mStoneB!,
							  new Vector3(x, h + sy * 0.5f, z),
							  new Vector3(sx, sy, sz),
							  0f, ry);
				}
				return;
			}

			// Fallback
			foreach (var (x, z, sx, sy, sz, ry) in stones)
			{
				var mi = MeshAt(new BoxMesh { Size = new Vector3(sx, sy, sz) },
					(int)(x + z) % 2 == 0 ? _mStoneA! : _mStoneB!, new Vector3(x, h + sy*0.5f, z));
				mi.RotationDegrees = new Vector3(0, ry, 0);
				AddChild(mi);
			}
		}

		private void SpawnWaterDetail(float h, bool deep)
		{
			// Anillos concéntricos para simular superficie de agua
			var waveMat = new StandardMaterial3D
			{
				AlbedoColor  = deep
					? new Color(0.12f, 0.38f, 0.74f, 0.55f)
					: new Color(0.22f, 0.56f, 0.84f, 0.45f),
				Roughness    = deep ? 0.06f : 0.12f,
				Metallic     = deep ? 0.45f : 0.28f,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				ShadingMode  = BaseMaterial3D.ShadingModeEnum.PerPixel,
			};

			// Disco brillante sobre el tile (simula superficie de agua)
			AddChild(MeshAt(
				new CylinderMesh { TopRadius = HexSize * 0.86f, BottomRadius = HexSize * 0.86f,
								   Height = 0.04f, RadialSegments = 12 },
				waveMat, new Vector3(0, h + 0.08f, 0)));

			// Anillo exterior más oscuro (borde del tile de agua)
			var edgeMat = new StandardMaterial3D
			{
				AlbedoColor  = deep
					? new Color(0.08f, 0.24f, 0.55f, 0.70f)
					: new Color(0.14f, 0.42f, 0.70f, 0.60f),
				Roughness    = 0.10f,
				Metallic     = 0.50f,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			};
			AddChild(MeshAt(
				new CylinderMesh { TopRadius = HexSize * 0.94f, BottomRadius = HexSize * 0.94f,
								   Height = 0.03f, RadialSegments = 12 },
				edgeMat, new Vector3(0, h + 0.04f, 0)));

			if (!deep)
			{
				// Costa: anillo de espuma continuo en el borde del tile (un solo cilindro hueco)
				var foamMat = new StandardMaterial3D
				{
					AlbedoColor  = new Color(0.92f, 0.96f, 1.00f, 0.55f),
					Roughness    = 0.18f,
					Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				};
				AddChild(MeshAt(
					new CylinderMesh { TopRadius    = HexSize * 0.84f, BottomRadius = HexSize * 0.84f,
									   Height       = 0.03f,            RadialSegments = 12 },
					foamMat, new Vector3(0, h + 0.15f, 0)));
			}
		}

		private void SpawnTundra(float h)
		{
			_mSnow  ??= SolidMat(new Color(0.84f, 0.90f, 0.95f), roughness: 0.60f);
			_mFrost ??= SolidMat(new Color(0.70f, 0.78f, 0.86f), roughness: 0.72f);

			(float x, float z, float s)[] patches =
			{
				(-1.6f,  0.5f, 1.1f), (1.4f, -0.8f, 0.9f),
				( 0.2f, -1.7f, 1.2f), (0.0f,  1.5f, 0.8f),
			};

			if (NatureRenderer.Instance != null)
			{
				// Parches: CylinderMesh aplanado via escala en Transform
				foreach (var (x, z, s) in patches)
					BatchMesh(K_SNOW_PATCH, _unitBox, _mSnow!,
							  new Vector3(x, h + 0.05f, z),
							  new Vector3(1.20f * s, 0.08f, 1.30f * s));
				// Estalactitas de hielo (conos pequeños, batch como cajas estrechas)
				BatchMesh(K_FROST, _unitBox, _mFrost!,
						  new Vector3(-0.4f, h + 0.22f, 0.6f), new Vector3(0.12f, 0.45f, 0.12f));
				BatchMesh(K_FROST, _unitBox, _mFrost!,
						  new Vector3( 0.8f, h + 0.17f,-0.4f), new Vector3(0.10f, 0.35f, 0.10f));
				return;
			}

			foreach (var (x, z, s) in patches)
				AddChild(MeshAt(new CylinderMesh { TopRadius=0.60f*s, BottomRadius=0.65f*s, Height=0.08f, RadialSegments=7 },
					_mSnow!, new Vector3(x, h + 0.05f, z)));
			AddChild(MeshAt(new CylinderMesh { TopRadius=0f, BottomRadius=0.28f, Height=0.45f, RadialSegments=5 },
				_mFrost!, new Vector3(-0.4f, h + 0.22f, 0.6f)));
			AddChild(MeshAt(new CylinderMesh { TopRadius=0f, BottomRadius=0.22f, Height=0.35f, RadialSegments=5 },
				_mFrost!, new Vector3( 0.8f, h + 0.17f, -0.4f)));
		}

		private void SpawnPeaks(float h)
		{
			// Rocas Kenney en los bordes del tile de montaña
			string[] rockGlbs = {
				"res://assets/nature/cliff_top_rock.glb",
				"res://assets/nature/rock_smallA.glb",
				"res://assets/nature/rock_smallB.glb",
			};
			(Vector3 p, float s, float ry)[] rockSpots = {
				(new(-2.0f, h + 0.05f, -1.0f), 0.55f,  20f),
				(new( 2.0f, h + 0.05f,  0.8f), 0.44f, -15f),
				(new(-0.5f, h + 0.05f,  2.0f), 0.50f,  45f),
			};
			int rseed = Q * 13 ^ R * 7;
			for (int ri = 0; ri < rockSpots.Length; ri++)
			{
				var (rp, rs, rry) = rockSpots[ri];
				// TrySpawnNatureAt: no crea Node3D cuando NatureRenderer está activo
				if (!TrySpawnNatureAt(rockGlbs[ri % rockGlbs.Length], rp, rs, rry))
				{
					var rg = new Node3D { Position = rp };
					TrySpawnNature(rockGlbs[ri % rockGlbs.Length], rg, Vector3.Zero, rs, rry);
					AddChild(rg);
				}
			}

			var rock    = SolidMat(new Color(0.42f, 0.40f, 0.45f));
			var darkRk  = SolidMat(new Color(0.28f, 0.26f, 0.30f));
			var snow    = SolidMat(new Color(0.96f, 0.98f, 1.00f));
			var iceBlue = SolidMat(new Color(0.76f, 0.88f, 0.98f), roughness: 0.20f);

			int seed = Q * 97 ^ R * 53;
			(Vector3 p, float s, float tilt)[] peaks = {
				(new( 0.0f, h, -0.4f), 1.05f, (seed & 7) * 2f - 6f),
				(new(-1.8f, h, -0.5f), 0.72f, ((seed>>3) & 7) * 2f - 6f),
				(new( 1.7f, h,  0.8f), 0.65f, ((seed>>6) & 7) * 2f - 6f),
				(new(-0.4f, h,  1.8f), 0.58f, ((seed>>9) & 7) * 2f - 4f),
			};

			foreach (var (p, s, tilt) in peaks)
			{
				float ph = 3.4f * s;
				var g = new Node3D { Position = p };
				g.RotationDegrees = new Vector3(tilt * 0.5f, tilt * 40f, 0f);

				// Base de la montaña (cono ancho y achatado)
				g.AddChild(MeshAt(new CylinderMesh {
					TopRadius=0.15f*s, BottomRadius=1.5f*s, Height=ph*0.65f, RadialSegments=7
				}, darkRk, new Vector3(0, ph*0.32f, 0)));

				// Pico superior (cono estrecho)
				g.AddChild(MeshAt(new CylinderMesh {
					TopRadius=0f, BottomRadius=0.80f*s, Height=ph*0.55f, RadialSegments=6
				}, rock, new Vector3(0, ph*0.62f, 0)));

				// Capa de nieve
				float sh = ph * 0.28f;
				g.AddChild(MeshAt(new CylinderMesh {
					TopRadius=0f, BottomRadius=0.55f*s, Height=sh, RadialSegments=6
				}, snow, new Vector3(0, ph - sh*0.30f, 0)));

				// Pequeño refle azulado de hielo glacial
				g.AddChild(MeshAt(new CylinderMesh {
					TopRadius=0f, BottomRadius=0.28f*s, Height=sh*0.4f, RadialSegments=5
				}, iceBlue, new Vector3(0.18f*s, ph - sh*0.05f, 0.12f*s)));

				AddChild(g);
			}
		}

		private void SpawnHillDome(float h)
		{
			var rockMat  = SolidMat(new Color(0.52f, 0.48f, 0.40f), roughness: 0.90f);
			var rock2Mat = SolidMat(new Color(0.44f, 0.40f, 0.34f), roughness: 0.92f);
			var grassMat = SolidMat(_baseColor.Lightened(0.08f));

			int seed = Q * 13 ^ R * 7;

			// ── Roca principal — cliff_top_rock (dominante) ───────────────
			float mainScale = 0.80f + (seed & 3) * 0.08f;
			float mainRot   = (seed & 7) * 45f;
			if (!TrySpawnNatureAt("res://assets/nature/cliff_top_rock.glb",
								   new Vector3(0f, h, 0f), mainScale, mainRot))
			{
				var gMain = new Node3D { Position = new Vector3(0f, h, 0f) };
				gMain.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.10f, 0.70f, 0.90f) }, MaterialOverride = rockMat,  RotationDegrees = new Vector3(0f, mainRot, 8f) });
				gMain.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.70f, 0.90f, 0.60f) }, MaterialOverride = rock2Mat, Position = new Vector3(0.30f, 0.10f, 0.20f), RotationDegrees = new Vector3(-6f, mainRot + 22f, 0f) });
				AddChild(gMain);
			}

			// ── Rocas secundarias ─────────────────────────────────────────
			float s1 = 0.44f + (seed & 3) * 0.05f;
			if (!TrySpawnNatureAt("res://assets/nature/rock_smallA.glb",
								   new Vector3(-1.20f, h, 0.40f), s1, (seed & 7) * 30f))
			{
				var g1 = new Node3D { Position = new Vector3(-1.20f, h, 0.40f) };
				g1.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.55f, 0.44f, 0.44f) }, MaterialOverride = rockMat, RotationDegrees = new Vector3(0f, (seed & 7) * 22f, 12f) });
				AddChild(g1);
			}

			float s2 = 0.38f + ((seed >> 2) & 3) * 0.04f;
			if (!TrySpawnNatureAt("res://assets/nature/rock_smallB.glb",
								   new Vector3(1.10f, h, -0.50f), s2, ((seed >> 3) & 7) * 35f))
			{
				var g2 = new Node3D { Position = new Vector3(1.10f, h, -0.50f) };
				g2.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.40f, 0.32f, 0.54f) }, MaterialOverride = rock2Mat, RotationDegrees = new Vector3(5f, ((seed >> 3) & 7) * 28f, -8f) });
				AddChild(g2);
			}

			if (!TrySpawnNatureAt("res://assets/nature/rock_smallA.glb",
								   new Vector3(-0.40f, h, -1.00f),
								   0.28f + ((seed >> 4) & 3) * 0.03f, (seed >> 5) * 50f))
			{
				var g3 = new Node3D { Position = new Vector3(-0.40f, h, -1.00f) };
				g3.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.30f, 0.22f, 0.34f) }, MaterialOverride = rockMat, RotationDegrees = new Vector3(-4f, (seed >> 5) * 40f, 6f) });
				AddChild(g3);
			}

			// ── Hierba baja entre las rocas (batch si NatureRenderer disponible) ─
			_mGrassDark ??= SolidMat(new Color(0.14f, 0.52f, 0.16f), roughness: 0.92f);
			(Vector3 pos, float s)[] tufts =
			{
				(new(-0.65f, h + 0.02f, -0.50f), 0.18f),
				(new( 0.55f, h + 0.02f,  0.60f), 0.15f),
				(new(-1.40f, h + 0.02f, -0.30f), 0.13f),
			};
			foreach (var (pos, s) in tufts)
			{
				if (NatureRenderer.Instance != null)
					BatchMesh(K_GRASS_DARK, _unitBox, _mGrassDark!,
							  pos, new Vector3(s * 0.8f, s * 0.6f, s * 0.8f));
				else
					AddChild(MeshAt(new CylinderMesh { TopRadius=s*0.5f, BottomRadius=s*0.8f, Height=s*0.6f, RadialSegments=5 }, grassMat, pos));
			}
		}

		private void SpawnDunes(float h)
		{
			var sand = SolidMat(_baseColor.Lightened(0.18f));
			(Vector3 p, float s)[] dunes = {
				(new(-1.5f, h, 0.5f), 0.7f),
				(new( 1.5f, h, -0.5f), 0.6f),
				(new( 0.0f, h,  1.5f), 0.55f),
			};
			foreach (var (p, s) in dunes)
				AddChild(MeshAt(new SphereMesh { Radius=1.1f*s, Height=0.9f*s, RadialSegments=8, Rings=4 },
								sand, p, new Vector3(1f, 0.35f, 1f)));
		}

		private void SpawnIce(float h)
		{
			_mIce ??= SolidMat(new Color(0.85f, 0.93f, 1.00f), roughness:0.15f, metallic:0.12f);
			(Vector3 p, Vector3 sc, float ry)[] blocks = {
				(new(-1.6f, h + 0.15f,  0.0f), new(1.8f, 0.3f, 1.1f), 15f),
				(new( 1.4f, h + 0.12f,  0.8f), new(1.5f, 0.25f,1.2f), -20f),
				(new( 0.0f, h + 0.10f, -1.5f), new(2.0f, 0.2f, 0.9f), 5f),
			};

			if (NatureRenderer.Instance != null)
			{
				foreach (var (p, sc, ry) in blocks)
					BatchMesh(K_ICE, _unitBox, _mIce!, p, sc, 0f, ry);
				return;
			}

			foreach (var (p, sc, ry) in blocks)
			{
				AddChild(new MeshInstance3D
				{
					Mesh             = _unitBox,
					MaterialOverride = _mIce,
					Position         = p,
					Scale            = sc,
					RotationDegrees  = new Vector3(0f, ry, 0f)
				});
			}
		}

		// ================================================================
		//  HELPERS
		// ================================================================

		/// <summary>
		// ── Caché de PackedScene para assets de naturaleza ──────────────
		private static readonly Dictionary<string, PackedScene> _natureCache = new();

		// ── Meshes unit-size compartidos (una instancia para todo el juego) ──────
		private static readonly BoxMesh    _unitBox    = new BoxMesh    { Size = Vector3.One };
		private static readonly SphereMesh _unitSphere = new SphereMesh { Radius = 0.5f, Height = 1.0f, RadialSegments = 8, Rings = 5 };

		// Claves semánticas para grupos de MultiMesh procedimental
		private const string K_GRASS_DARK   = "prim:grass_dark";
		private const string K_GRASS_LIT    = "prim:grass_lit";
		private const string K_GRASS_FLOWER = "prim:grass_flower";
		private const string K_STONE_A      = "prim:stone_a";
		private const string K_STONE_B      = "prim:stone_b";
		private const string K_ICE          = "prim:ice";
		private const string K_SNOW_PATCH   = "prim:snow_patch";
		private const string K_FROST        = "prim:frost";

		// Materiales compartidos (lazy init)
		private static StandardMaterial3D? _mGrassDark;
		private static StandardMaterial3D? _mGrassLit;
		private static StandardMaterial3D? _mGrassFlower;
		private static StandardMaterial3D? _mStoneA;
		private static StandardMaterial3D? _mStoneB;
		private static StandardMaterial3D? _mIce;
		private static StandardMaterial3D? _mSnow;
		private static StandardMaterial3D? _mFrost;

		/// <summary>
		/// Registra una instancia de naturaleza en NatureRenderer (MultiMesh) si está disponible,
		/// o instancia el GLB como nodo hijo como fallback.
		/// Devuelve true si tuvo éxito (no se necesita fallback procedural).
		/// </summary>
		private bool TrySpawnNature(string assetPath, Node3D parent,
									 Vector3 localPos, float scale, float rotY = 0f)
		{
			// ── Ruta rápida: MultiMesh via NatureRenderer ─────────────────
			if (NatureRenderer.Instance != null)
			{
				// Verificar que el asset existe (usando la caché de PackedScene)
				if (!_natureCache.ContainsKey(assetPath))
				{
					if (!ResourceLoader.Exists(assetPath)) return false;
					// Registramos el path como "conocido" (value null = sólo marcador)
					_natureCache[assetPath] = null!;
				}

				// Construir Transform3D en espacio mundo.
				// GlobalPosition es la posición del tile; parent.Position es el offset local.
				Vector3 worldPos = GlobalPosition + parent.Position + localPos;
				var basis = new Basis(Vector3.Up, Mathf.DegToRad(rotY))
								.Scaled(Vector3.One * scale);
				NatureRenderer.Instance.RegisterForTile(assetPath, Q, R, new Transform3D(basis, worldPos));
				return true;
			}

			// ── Fallback: instanciar como nodo (cuando NatureRenderer no está) ──
			if (!_natureCache.TryGetValue(assetPath, out var scene) || scene == null)
			{
				if (!ResourceLoader.Exists(assetPath)) return false;
				scene = GD.Load<PackedScene>(assetPath);
				if (scene == null) return false;
				_natureCache[assetPath] = scene;
			}
			try
			{
				var node = scene.Instantiate();
				if (node is not Node3D inst3d)
				{
					var wrap = new Node3D();
					wrap.AddChild(node);
					inst3d = wrap;
				}
				inst3d.Scale           = Vector3.One * scale;
				inst3d.Position        = localPos;
				inst3d.RotationDegrees = new Vector3(0f, rotY, 0f);
				parent.AddChild(inst3d);
				ApplyVertexColorFix(inst3d);
				return true;
			}
			catch (Exception ex)
			{
				GD.PrintErr($"[HexTile3D] Error cargando '{assetPath}': {ex.Message}");
				return false;
			}
		}

		/// <summary>
		/// Versión sin-nodo de TrySpawnNature: registra en NatureRenderer usando GlobalPosition+localOffset.
		/// No crea Node3D auxiliar — cero scene-tree overhead cuando NatureRenderer está activo.
		/// Returns false si NatureRenderer no está disponible o el asset no existe.
		/// </summary>
		private bool TrySpawnNatureAt(string assetPath, Vector3 localOffset, float scale, float rotY = 0f)
		{
			if (NatureRenderer.Instance == null) return false;
			if (!_natureCache.ContainsKey(assetPath))
			{
				if (!ResourceLoader.Exists(assetPath)) return false;
				_natureCache[assetPath] = null!;
			}
			var basis    = new Basis(Vector3.Up, Mathf.DegToRad(rotY)).Scaled(Vector3.One * scale);
			var worldPos = GlobalPosition + localOffset;
			NatureRenderer.Instance.RegisterForTile(assetPath, Q, R, new Transform3D(basis, worldPos));
			return true;
		}

		/// <summary>
		/// Registra un mesh procedimental compartido en NatureRenderer (batching).
		/// La escala y rotación van codificadas en el Transform3D → cero overhead adicional.
		/// </summary>
		private void BatchMesh(string key, Mesh mesh, StandardMaterial3D mat,
								Vector3 localPos, Vector3 scale3,
								float rotXdeg = 0f, float rotYdeg = 0f)
		{
			if (NatureRenderer.Instance == null) return;
			var rot      = Basis.FromEuler(new Vector3(Mathf.DegToRad(rotXdeg), Mathf.DegToRad(rotYdeg), 0f));
			var basis    = rot.Scaled(scale3);
			var worldPos = GlobalPosition + localPos;
			NatureRenderer.Instance.RegisterMeshForTile(key, mesh, mat, Q, R, new Transform3D(basis, worldPos));
		}

		/// <summary>
		/// Activa VertexColorUseAsAlbedo en todos los MeshInstance3D descendientes.
		/// Necesario para que los modelos Kenney muestren sus colores de vértice.
		/// </summary>
		private static void ApplyVertexColorFix(Node3D root)
		{
			foreach (var child in root.FindChildren("*", "MeshInstance3D", true, false))
			{
				if (child is not MeshInstance3D mi) continue;
				int surfaces = mi.Mesh?.GetSurfaceCount() ?? 0;
				for (int s = 0; s < surfaces; s++)
				{
					var mat = mi.GetActiveMaterial(s) as StandardMaterial3D;
					if (mat == null || mat.VertexColorUseAsAlbedo) continue;
					var dup = (StandardMaterial3D)mat.Duplicate();
					dup.VertexColorUseAsAlbedo = true;
					mi.SetSurfaceOverrideMaterial(s, dup);
				}
			}
		}

		private static MeshInstance3D MeshAt(Mesh mesh, StandardMaterial3D mat,
											  Vector3 pos, Vector3? scale = null)
		{
			var mi = new MeshInstance3D { Mesh=mesh, MaterialOverride=mat, Position=pos };
			if (scale.HasValue) mi.Scale = scale.Value;
			return mi;
		}

		private static StandardMaterial3D SolidMat(Color color,
													float roughness = 0.82f,
													float metallic  = 0.0f)
		{
			return new StandardMaterial3D
			{
				AlbedoColor = color,
				Roughness   = roughness,
				Metallic    = metallic,
			};
		}

		private static bool   IsWater(TileType t) => t == TileType.Ocean || t == TileType.Coast;
		private static float  GetRoughness(TileType t) => t switch {
			TileType.Ocean => 0.08f, TileType.Coast => 0.15f,
			TileType.Desert => 0.95f, TileType.Mountains => 0.75f,
			_ => 0.82f
		};
		private static float  GetMetallic(TileType t) => t switch {
			TileType.Ocean => 0.38f, TileType.Coast => 0.22f, TileType.Arctic => 0.10f,
			_ => 0.0f
		};

		// ================================================================
		//  COORDENADAS (compatibles con el resto del sistema)
		// ================================================================

		/// <summary>
		/// Convierte (q,r) axial → Vector3 world. Y=0 (base del prisma).
		/// La superficie del tile está en Y = GetHeight(type).
		/// </summary>
		public static Vector3 AxialToWorld(int q, int r)
		{
			float x = HexSize * MathF.Sqrt(3f) * (q + r / 2f);
			float z = HexSize * 1.5f * r;
			return new Vector3(x, 0f, z);
		}

		/// <summary>
		/// Convierte Vector3 world XZ → coordenadas axiales (q,r).
		/// </summary>
		public static (float q, float r) WorldToAxialFrac(float wx, float wz)
		{
			float size   = HexSize;
			float r_frac = wz * 2f / (3f * size);
			float q_frac = wx / (MathF.Sqrt(3f) * size) - r_frac * 0.5f;
			return (q_frac, r_frac);
		}
	}
}
