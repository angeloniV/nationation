using Godot;
using System.Collections.Generic;
using Natiolation.Core;

namespace Natiolation.Map
{
	/// <summary>
	/// Gestiona el grid de HexTile3D.
	///
	/// API pública idéntica a la versión 2D — el resto del sistema no necesita cambios.
	/// </summary>
	public partial class MapManager : Node3D
	{
		[Export] public int MapWidth  = 60;
		[Export] public int MapHeight = 40;
		[Export] public int Seed      = 0;   // 0 = aleatorio al inicio

		/// <summary>Emitida cuando cualquier conjunto de tiles cambia su estado de visibilidad.</summary>
		[Signal] public delegate void TilesRevealedEventHandler();

		private TileType[,]  _tileTypes = null!;
		private HexTile3D[,] _tiles     = null!;

		// ── Ríos ──────────────────────────────────────────────────────────
		private readonly Dictionary<(int q, int r), List<int>> _riverEdgesByTile = new();

		// ── Mejoras de Constructor ────────────────────────────────────────
		private readonly Dictionary<(int q, int r), TileImprovement> _improvements = new();

		// ── Capa de caminos ───────────────────────────────────────────────
		private Node3D _roadLayer = null!;
		private readonly Dictionary<(int q, int r), Node3D> _roadNodes = new();
		private static StandardMaterial3D? _roadMat;

		public override void _Ready()
		{
			// NatureRenderer debe existir antes de que cualquier tile sea revelado
			// (SetVisible → AddDecorations → TrySpawnNature usa NatureRenderer.Instance)
			var nr = new NatureRenderer { Name = "NatureRenderer" };
			AddChild(nr);   // _EnterTree() del NatureRenderer establece Instance inmediatamente

			// Si la seed viene de GameSettings (nuevo juego con semilla específica), usarla
			if (GameSettings.Instance != null && GameSettings.Instance.MapSeed != 0)
				Seed = GameSettings.Instance.MapSeed;
			else if (Seed == 0)
				Seed = (int)(Time.GetTicksMsec() & 0x7FFFFFFF);   // seed aleatoria

			GenerateMap();
			// Sin RevealAllForDebug() — el fog of war empieza activo.
			// Las unidades revelan su entorno al spawnear y al moverse.
		}

		// ================================================================
		//  GENERACIÓN
		// ================================================================

		private void GenerateMap()
		{
			_tileTypes = MapGenerator.Generate(MapWidth, MapHeight, Seed, out var riverEdges);
			_tiles     = new HexTile3D[MapWidth, MapHeight];

			// Indexar bordes de río por tile
			foreach (var (q, r, dir) in riverEdges)
			{
				var key = (q, r);
				if (!_riverEdgesByTile.TryGetValue(key, out var list))
				{
					list = new List<int>();
					_riverEdgesByTile[key] = list;
				}
				if (!list.Contains(dir))
					list.Add(dir);
			}

			for (int q = 0; q < MapWidth; q++)
			{
				for (int r = 0; r < MapHeight; r++)
				{
					var tile = new HexTile3D
					{
						Q    = q,
						R    = r,
						Type = _tileTypes[q, r],
					};

					AddChild(tile);
					tile.Position = HexTile3D.AxialToWorld(q, r);

					// Pasar direcciones de río antes de Init (para decoraciones)
					if (_riverEdgesByTile.TryGetValue((q, r), out var dirs))
						tile.SetRiverEdges(dirs);

					tile.Init();   // crea mesh, colisión, material, decoraciones (lazy)

					_tiles[q, r] = tile;
				}
			}

			// TerrainRenderer maneja visualmente el terreno; slopes y water plane
			// ya no son necesarios (el shader los integra con blending suave).
			// BuildTerrainSlopes();  — suprimido
			// BuildWaterPlane();     — suprimido

			// Capa de ríos visible siempre (no sujeta al fog of war)
			BuildRiverLayer(riverEdges);

			// Capa de caminos (vacía al inicio — se construye cuando el jugador pone caminos)
			_roadLayer = new Node3D { Name = "RoadLayer" };
			AddChild(_roadLayer);

			// Terrain renderer unificado (reemplaza visualmente los prismas hex individuales)
			var terrainRenderer = new TerrainRenderer { Name = "TerrainRenderer" };
			AddChild(terrainRenderer);
			terrainRenderer.Init(_tileTypes, MapWidth, MapHeight);

			GD.Print($"[MapManager] Mapa 3D generado: {MapWidth}×{MapHeight}, seed={Seed}, " +
					 $"ríos: {riverEdges.Count / 2} bordes");
		}

		// ================================================================
		//  PENDIENTES DE TERRENO
		// ================================================================

		/// <summary>
		/// Construye un único mesh con cuadriláteros que rellenan los "escalones"
		/// entre tiles adyacentes a diferentes alturas, creando transiciones suaves.
		/// </summary>
		private void BuildTerrainSlopes()
		{
			// Para la dirección d, los índices de los dos vértices del anillo
			// de un tile que forman la arista compartida con su vecino.
			// Derivado de la geometría del hex apuntado (offset 30°).
			int[] eVa = { 5, 2, 0, 3, 4, 1 };   // primer vértice de arista
			int[] eVb = { 0, 3, 1, 4, 5, 2 };   // segundo vértice de arista

			int[] dq = {  1, -1, 0,  0,  1, -1 };
			int[] dr = {  0,  0, 1, -1, -1,  1 };

			var st = new SurfaceTool();
			st.Begin(Mesh.PrimitiveType.Triangles);
			bool anyAdded = false;

			// Solo d=0,2,4 para procesar cada arista una sola vez
			for (int q = 0; q < MapWidth; q++)
			{
				for (int r = 0; r < MapHeight; r++)
				{
					float hA   = GetTileHeight(q, r);
					var   posA = HexTile3D.AxialToWorld(q, r);
					Color cA   = _tileTypes[q, r].MapColor();

					for (int di = 0; di < 3; di++)
					{
						int d  = di * 2;   // 0, 2, 4
						int nq = q + dq[d], nr = r + dr[d];
						if (nq < 0 || nq >= MapWidth || nr < 0 || nr >= MapHeight) continue;

						float hB = GetTileHeight(nq, nr);
						if (MathF.Abs(hA - hB) < 0.01f) continue;

						Color cB   = _tileTypes[nq, nr].MapColor();
						Color cHi  = hA > hB ? cA.Darkened(0.12f) : cB.Darkened(0.12f);
						Color cLo  = hA > hB ? cB.Darkened(0.06f) : cA.Darkened(0.06f);

						// Puntos en la arista del tile A a su altura hA
						var ringA = HexTile3D.HexRing(hA);
						Vector3 hiA = posA + ringA[eVa[d]];
						Vector3 hiB = posA + ringA[eVb[d]];

						// Los mismos XZ pero a la altura del vecino hB
						Vector3 loA = new(hiA.X, hB, hiA.Z);
						Vector3 loB = new(hiB.X, hB, hiB.Z);

						// Si el vecino es más alto, intercambiar hi↔lo
						if (hA < hB)
						{
							(hiA, loA) = (loA, hiA);
							(hiB, loB) = (loB, hiB);
							(cHi, cLo) = (cLo, cHi);
						}

						// Normal: perpendicular a la superficie del quad, apuntando hacia arriba-afuera
						var faceNorm = (hiB - hiA).Cross(loA - hiA).Normalized();
						if (faceNorm.Y < 0f) faceNorm = -faceNorm;

						// Desplazar ligeramente el quad hacia afuera para evitar Z-fighting
						// con las caras laterales de los prismas hexagonales
						var edgeMid = (hiA + hiB) * 0.5f;
						var outDir  = new Vector3(edgeMid.X - posA.X, 0f, edgeMid.Z - posA.Z).Normalized();
						const float eps = 0.004f;
						hiA += outDir * eps; hiB += outDir * eps;
						loA += outDir * eps; loB += outDir * eps;

						// Triángulo 1: hiA → hiB → loB
						st.SetNormal(faceNorm); st.SetColor(cHi); st.AddVertex(hiA);
						st.SetNormal(faceNorm); st.SetColor(cHi); st.AddVertex(hiB);
						st.SetNormal(faceNorm); st.SetColor(cLo); st.AddVertex(loB);

						// Triángulo 2: hiA → loB → loA
						st.SetNormal(faceNorm); st.SetColor(cHi); st.AddVertex(hiA);
						st.SetNormal(faceNorm); st.SetColor(cLo); st.AddVertex(loB);
						st.SetNormal(faceNorm); st.SetColor(cLo); st.AddVertex(loA);

						anyAdded = true;
					}
				}
			}

			if (!anyAdded) return;

			var mesh = st.Commit();
			var mi = new MeshInstance3D
			{
				Name = "TerrainSlopes",
				Mesh = mesh,
				MaterialOverride = new StandardMaterial3D
				{
					VertexColorUseAsAlbedo = true,
					Roughness   = 0.90f,
					// Double-sided: visible tanto desde arriba como desde el lado
					CullMode    = BaseMaterial3D.CullModeEnum.Disabled,
				},
			};
			AddChild(mi);
			GD.Print($"[MapManager] TerrainSlopes: {MapWidth * MapHeight * 3 / 2} aristas revisadas");
		}

		// ================================================================
		//  PLANO DE AGUA UNIFICADO
		// ================================================================

		/// <summary>
		/// Crea un plano de agua grande y plano bajo todos los tiles marinos.
		/// Esto da la sensación de un océano continuo en lugar de discos por tile.
		/// </summary>
		private void BuildWaterPlane()
		{
			// Calcular bounding box de tiles de agua
			float minX = float.MaxValue, maxX = float.MinValue;
			float minZ = float.MaxValue, maxZ = float.MinValue;
			bool hasWater = false;

			for (int q = 0; q < MapWidth; q++)
				for (int r = 0; r < MapHeight; r++)
				{
					var t = _tileTypes[q, r];
					if (t != TileType.Ocean && t != TileType.Coast) continue;
					var pos = HexTile3D.AxialToWorld(q, r);
					minX = MathF.Min(minX, pos.X - HexTile3D.HexSize);
					maxX = MathF.Max(maxX, pos.X + HexTile3D.HexSize);
					minZ = MathF.Min(minZ, pos.Z - HexTile3D.HexSize);
					maxZ = MathF.Max(maxZ, pos.Z + HexTile3D.HexSize);
					hasWater = true;
				}

			if (!hasWater) return;

			float waterY = 0.30f;  // entre ocean (0.20) y coast (0.40)
			float cx = (minX + maxX) * 0.5f;
			float cz = (minZ + maxZ) * 0.5f;
			float wx = maxX - minX + HexTile3D.HexSize * 2f;
			float wz = maxZ - minZ + HexTile3D.HexSize * 2f;

			var planeMesh = new PlaneMesh
			{
				Size            = new Vector2(wx, wz),
				SubdivideWidth  = 0,
				SubdivideDepth  = 0,
			};

			var waterMat = new StandardMaterial3D
			{
				AlbedoColor  = new Color(0.08f, 0.28f, 0.60f, 0.82f),
				Roughness    = 0.04f,
				Metallic     = 0.55f,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				RenderPriority = -1,   // se dibuja antes que los tiles (bajo ellos)
			};

			var waterPlane = new MeshInstance3D
			{
				Name             = "WaterPlane",
				Mesh             = planeMesh,
				MaterialOverride = waterMat,
				Position         = new Vector3(cx, waterY, cz),
			};
			AddChild(waterPlane);
		}

		private void BuildRiverLayer(HashSet<(int q, int r, int dir)> riverEdges)
		{
			if (riverEdges.Count == 0) return;

			var layer = new Node3D { Name = "RiverLayer" };
			AddChild(layer);

			var mat = new StandardMaterial3D
			{
				AlbedoColor  = new Color(0.18f, 0.52f, 0.86f, 0.90f),
				Roughness    = 0.06f,
				Metallic     = 0.28f,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			};

			int[] dq = {  1, -1,  0,  0,  1, -1 };
			int[] dr = {  0,  0,  1, -1, -1,  1 };

			// Evitar duplicados: solo dibujamos desde el lado con menor (q,r,dir)
			var created = new HashSet<(int, int, int)>();
			foreach (var (q, r, dir) in riverEdges)
			{
				int opp = dir ^ 1;
				int nq  = q + dq[dir], nr = r + dr[dir];
				if (created.Contains((nq, nr, opp))) continue;
				created.Add((q, r, dir));

				float tileH    = GetTileHeight(q, r);
				var   tilePos  = HexTile3D.AxialToWorld(q, r);
				var   verts    = HexTile3D.HexRing(tileH + 0.05f);
				var   mid      = (verts[dir] + verts[(dir + 1) % 6]) * 0.5f;

				var strip = new MeshInstance3D
				{
					Mesh             = new BoxMesh { Size = new Vector3(HexTile3D.HexSize * 0.92f, 0.09f, 0.55f) },
					MaterialOverride = mat,
					Position         = tilePos + mid,
				};
				strip.RotationDegrees = new Vector3(0f, -(150f + 60f * dir), 0f);
				layer.AddChild(strip);
			}
			GD.Print($"[MapManager] RiverLayer: {created.Count} bordes visibles");
		}

		// ================================================================
		//  API PÚBLICA
		// ================================================================

		public HexTile3D? GetTile(int q, int r)
		{
			if (q < 0 || q >= MapWidth || r < 0 || r >= MapHeight) return null;
			return _tiles[q, r];
		}

		public TileType? GetTileType(int q, int r)
		{
			if (q < 0 || q >= MapWidth || r < 0 || r >= MapHeight) return null;
			return _tileTypes[q, r];
		}

		public float GetTileHeight(int q, int r)
		{
			var t = GetTileType(q, r);
			return t.HasValue ? HexTile3D.GetHeight(t.Value) : 0.75f;
		}

		public List<HexTile3D> GetNeighbors(int q, int r)
		{
			var  neighbors = new List<HexTile3D>();
			int[] dq = {  1, -1,  0,  0,  1, -1 };
			int[] dr = {  0,  0,  1, -1, -1,  1 };

			for (int i = 0; i < 6; i++)
			{
				var tile = GetTile(q + dq[i], r + dr[i]);
				if (tile != null) neighbors.Add(tile);
			}
			return neighbors;
		}

		/// <summary>Verdadero si el tile (q,r) tiene al menos un borde de río.</summary>
		public bool HasRiverEdge(int q, int r)
			=> _riverEdgesByTile.ContainsKey((q, r));

		/// <summary>Direcciones de borde de río del tile (q,r). Vacío si ninguno.</summary>
		public IEnumerable<int> GetRiverDirs(int q, int r)
			=> _riverEdgesByTile.TryGetValue((q, r), out var d)
			   ? (IEnumerable<int>)d
			   : System.Array.Empty<int>();

		// ── Mejoras ──────────────────────────────────────────────────────

		public TileImprovement GetImprovement(int q, int r)
			=> _improvements.TryGetValue((q, r), out var imp) ? imp : TileImprovement.None;

		/// <summary>
		/// Costo de movimiento efectivo para entrar a (q,r).
		/// Los tiles con Camino cuestan 1/3 de movimiento (≈0.34 para que sea significativo).
		/// </summary>
		public float GetEffectiveCost(int q, int r)
		{
			if (GetImprovement(q, r) == TileImprovement.Road) return 0.25f;
			var t = GetTileType(q, r);
			return t.HasValue ? t.Value.MovementCost() : 1f;
		}

		public void SetImprovement(int q, int r, TileImprovement improvement)
		{
			_improvements[(q, r)] = improvement;
			var tile = GetTile(q, r);
			tile?.AddImprovementVisual(improvement);

			if (improvement == TileImprovement.Road)
			{
				UpdateRoadVisuals(q, r);
				int[] dq = {  1, -1,  0,  0,  1, -1 };
				int[] dr = {  0,  0,  1, -1, -1,  1 };
				for (int i = 0; i < 6; i++)
				{
					int nq = q + dq[i], nr = r + dr[i];
					if (GetImprovement(nq, nr) == TileImprovement.Road)
						UpdateRoadVisuals(nq, nr);
				}
			}
		}

		public void RevealAllForDebug()
		{
			for (int q = 0; q < MapWidth; q++)
				for (int r = 0; r < MapHeight; r++)
					_tiles[q, r].SetVisible(true, true);
		}

		/// <summary>
		/// Encuentra el tile no explorado más cercano accesible desde (startQ, startR)
		/// usando BFS por tiles transitables. Retorna null si todo el mapa está explorado.
		/// </summary>
		public HexTile3D? FindNearestUnexplored(int startQ, int startR)
		{
			int[] dq = {  1, -1,  0,  0,  1, -1 };
			int[] dr = {  0,  0,  1, -1, -1,  1 };

			var visited = new HashSet<(int, int)>();
			var queue   = new Queue<(int q, int r)>();
			queue.Enqueue((startQ, startR));
			visited.Add((startQ, startR));

			while (queue.Count > 0)
			{
				var (q, r) = queue.Dequeue();
				var tile = GetTile(q, r);
				if (tile == null) continue;

				if (!tile.WasExplored)
					return tile;

				for (int i = 0; i < 6; i++)
				{
					int nq = q + dq[i], nr = r + dr[i];
					if (visited.Contains((nq, nr))) continue;
					var nt = GetTileType(nq, nr);
					if (nt == null || !Pathfinder.IsPassable(nt.Value)) continue;
					visited.Add((nq, nr));
					queue.Enqueue((nq, nr));
				}
			}

			return null;
		}

		/// <summary>
		/// Recalcula la visibilidad de todo el mapa basado en el conjunto de observadores
		/// provistos. Cada observer es (q, r, radius).
		///
		/// • Tiles dentro del radio de cualquier observador → visible + explorado.
		/// • Tiles explorados pero fuera de todos los radios → explorado (niebla).
		/// • Tiles nunca vistos → inexplorado (oscuro).
		///
		/// Emite TilesRevealed al finalizar.
		/// </summary>
		public void RefreshVisibility(System.Collections.Generic.IEnumerable<(int q, int r, int sight)> observers)
		{
			// Paso 1: marcar todos los tiles visibles como "en niebla" (mantienen explorado)
			for (int q = 0; q < MapWidth; q++)
				for (int r = 0; r < MapHeight; r++)
					if (_tiles[q, r].TileVisible)
						_tiles[q, r].SetVisible(false, true);

			// Paso 2: revelar según todos los observadores
			foreach (var (oq, or_, sight) in observers)
				for (int q = 0; q < MapWidth; q++)
					for (int r = 0; r < MapHeight; r++)
						if (HexDistance(q, r, oq, or_) <= sight)
							_tiles[q, r].SetVisible(true, true);

			EmitSignal(SignalName.TilesRevealed);
		}

		/// <summary>Mantener para compatibilidad — revela desde un único punto.</summary>
		public void RevealRadius(int centerQ, int centerR, int radius)
			=> RefreshVisibility(new[] { (centerQ, centerR, radius) });

		private static int HexDistance(int q1, int r1, int q2, int r2)
		{
			int s1 = -q1 - r1, s2 = -q2 - r2;
			return (Mathf.Abs(q1 - q2) + Mathf.Abs(r1 - r2) + Mathf.Abs(s1 - s2)) / 2;
		}

		// ================================================================
		//  CAMINOS — VISUALES
		// ================================================================

		private void UpdateRoadVisuals(int q, int r)
		{
			// Eliminar nodo anterior si existe
			if (_roadNodes.TryGetValue((q, r), out var old))
			{
				old.QueueFree();
				_roadNodes.Remove((q, r));
			}

			float roadY  = GetTileHeight(q, r) + 0.05f;
			var   tilePos = HexTile3D.AxialToWorld(q, r);

			// Direcciones para calcular vecinos con camino
			int[] dq  = {  1, -1,  0,  0,  1, -1 };
			int[] dr  = {  0,  0,  1, -1, -1,  1 };

			// Vértices del hexágono para calcular la dirección de cada arista
			var verts = HexTile3D.HexRing(0f);

			_roadMat ??= new StandardMaterial3D
			{
				AlbedoColor = new Color(0.54f, 0.44f, 0.26f),
				Roughness   = 0.92f,
			};

			var node = new Node3D { Name = $"Road_{q}_{r}" };
			_roadLayer.AddChild(node);

			// Punto central (siempre presente)
			var dot = new MeshInstance3D
			{
				Mesh = new CylinderMesh { TopRadius = 0.28f, BottomRadius = 0.28f,
										  Height = 0.06f, RadialSegments = 8 },
				MaterialOverride = _roadMat,
				Position         = tilePos + new Vector3(0f, roadY, 0f),
			};
			node.AddChild(dot);

			// Medios radios hacia vecinos con camino
			for (int dir = 0; dir < 6; dir++)
			{
				int nq = q + dq[dir], nr = r + dr[dir];
				if (GetImprovement(nq, nr) != TileImprovement.Road) continue;

				// Punto medio de la arista dir (en coordenadas locales al tile, Y=0)
				var mid     = (verts[dir] + verts[(dir + 1) % 6]) * 0.5f;
				float spokLen = mid.Length();   // apothem ≈ 3.46 para HexSize=4

				var spoke = new MeshInstance3D
				{
					Mesh = new BoxMesh { Size = new Vector3(0.24f, 0.06f, spokLen) },
					MaterialOverride = _roadMat,
					Position         = tilePos + new Vector3(mid.X * 0.5f, roadY, mid.Z * 0.5f),
				};
				spoke.RotationDegrees = new Vector3(0f, 30f - 60f * dir, 0f);
				node.AddChild(spoke);
			}

			_roadNodes[(q, r)] = node;
		}
	}
}
