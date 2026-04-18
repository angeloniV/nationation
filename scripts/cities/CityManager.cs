using Godot;
using System.Collections.Generic;
using Natiolation.Map;
using Natiolation.Units;

namespace Natiolation.Cities
{
    /// <summary>
    /// Gestiona todas las ciudades del juego.
    ///
    /// Responsabilidades:
    ///   • Validar y ejecutar la fundación (mínimo 3 tiles entre ciudades).
    ///   • Proveer observadores de fog of war por civilización.
    ///   • Procesar el turno de cada ciudad: comida, producción, crecimiento.
    ///   • Emitir UnitProductionComplete cuando una ciudad termina una unidad.
    ///   • Mantener y dibujar el overlay de territorio cultural.
    /// </summary>
    public partial class CityManager : Node3D
    {
        // ── Señales ──────────────────────────────────────────────────────
        [Signal] public delegate void CityFoundedEventHandler(string cityName, Color civColor);

        /// <summary>Eventos narrativos para el HUD (toast): fundación, crecimiento, producción.</summary>
        [Signal] public delegate void CityEventEventHandler(string message);

        /// <summary>
        /// Se emite cuando una ciudad completa una unidad.
        /// civIndex, civColor: dueño; unitTypeInt: cast de UnitType; q, r: posición de la ciudad.
        /// </summary>
        [Signal] public delegate void UnitProductionCompleteEventHandler(
            int q, int r, int civIndex, Color civColor, int unitTypeInt);

        // ── Referencias ──────────────────────────────────────────────────
        private MapManager _map     = null!;
        private MapOverlay _overlay = null!;

        // ── Datos ────────────────────────────────────────────────────────
        private readonly List<City> _cities = new();

        public IReadOnlyList<City> AllCities => _cities;

        private const int MinCityDistance  = 4;  // mínimo 3 tiles vacíos entre ciudades

        // ── Colores de territorio por civ (α = 0.22) ────────────────────
        private static readonly Color[] TerritoryColor =
        {
            new(0.18f, 0.42f, 0.95f, 0.22f),   // civ 0 — azul
            new(0.90f, 0.22f, 0.18f, 0.22f),   // civ 1 — rojo
        };

        // ── Nombres ──────────────────────────────────────────────────────
        private static readonly string[][] CityNamePool =
        {
            new[] { "Roma",      "Atenas",    "Paris",      "Londres",    "Madrid",
                    "Lisboa",    "Viena",     "Berlin",     "Venecia",    "Florencia",
                    "Sevilla",   "Lyon",      "Amsterdam",  "Bruselas",   "Praga"      },
            new[] { "Beijing",   "Kyoto",     "Samarkanda", "Bagdad",     "Delhi",
                    "Osaka",     "Carthago",  "Memphis",    "Tebas",      "Babilonia",
                    "Persepolis","Nankín",    "Mohenjo-daro","Ur",        "Ninevé"     },
        };
        private readonly int[] _nameCursors = { 0, 0 };

        // ================================================================
        //  GODOT
        // ================================================================

        public override void _Ready()
        {
            _map     = GetNode<MapManager>("/root/Main/MapManager");
            _overlay = GetNode<MapOverlay>("/root/Main/MapOverlay");
        }

        // ================================================================
        //  FUNDACIÓN
        // ================================================================

        public bool TryFoundCity(Unit settler)
        {
            int q = settler.Q, r = settler.R;
            if (!CanFoundAt(q, r)) return false;

            string name  = GenerateName(settler.CivIndex);
            float  tileH = _map.GetTileHeight(q, r);

            var city = new City();
            AddChild(city);
            city.Init(name, q, r, settler.CivIndex, settler.CivColor, tileH);
            city.CalculateYields(_map);
            _cities.Add(city);

            RefreshTerritory();

            GD.Print($"[CityManager] '{name}' fundada en ({q},{r}) civ={settler.CivIndex}. " +
                     $"Rendimiento: +{city.FoodPerTurn}🌾 +{city.ProdPerTurn}⚒/turno");
            EmitSignal(SignalName.CityFounded, name, settler.CivColor);
            if (settler.CivIndex == 0)
                EmitSignal(SignalName.CityEvent, $"⚑  {name} ha sido fundada");
            return true;
        }

        public bool CanFoundAt(int q, int r)
        {
            var t = _map.GetTileType(q, r);
            if (t == null || !Pathfinder.IsPassable(t.Value)) return false;
            foreach (var c in _cities)
                if (HexDistance(q, r, c.Q, c.R) < MinCityDistance) return false;
            return true;
        }

        // ================================================================
        //  CONSULTAS
        // ================================================================

        /// <summary>Devuelve la ciudad en (q, r) o null si no hay ninguna.</summary>
        public City? GetCityAt(int q, int r)
        {
            foreach (var c in _cities)
                if (c.Q == q && c.R == r) return c;
            return null;
        }

        /// <summary>Cambia la cola de producción (unidad) de una ciudad y resetea progreso.</summary>
        public void SetProductionQueue(City city, UnitType? type)
        {
            city.SetProductionQueue(type);
            GD.Print($"[CityManager] {city.CityName}: unidad → {(type.HasValue ? type.Value.ToString() : "ninguna")}");
        }

        /// <summary>Cambia la cola de producción (edificio) de una ciudad y resetea progreso.</summary>
        public void SetProductionQueue(City city, BuildingType? type)
        {
            city.SetProductionQueue(type);
            GD.Print($"[CityManager] {city.CityName}: edificio → {(type.HasValue ? type.Value.ToString() : "ninguno")}");
        }

        /// <summary>Ciencia total por turno de una civilización.</summary>
        public int GetTotalSciencePerTurn(int civIndex)
        {
            int total = 0;
            foreach (var c in _cities)
                if (c.CivIndex == civIndex)
                    total += c.SciencePerTurn;
            return total;
        }

        /// <summary>Oro de mantenimiento de edificios de una civilización (por turno).</summary>
        public int GetBuildingMaintenanceCost(int civIndex)
        {
            int total = 0;
            foreach (var c in _cities)
                if (c.CivIndex == civIndex)
                    total += c.MaintenanceCost;
            return total;
        }

        // ================================================================
        //  FOG OF WAR
        // ================================================================

        public IEnumerable<(int q, int r, int sight)> GetObservers(int civIndex)
        {
            foreach (var c in _cities)
                if (c.CivIndex == civIndex)
                    yield return (c.Q, c.R, c.SightRange);
        }

        /// <summary>
        /// Actualiza la visibilidad de las ciudades enemigas según el fog of war.
        /// Ciudades del jugador (civ 0) siempre visibles; ciudades enemigas solo si el tile es visible.
        /// </summary>
        public void RefreshFogVisibility(MapManager map)
        {
            foreach (var city in _cities)
                city.Visible = city.CivIndex == 0 || (map.GetTile(city.Q, city.R)?.TileVisible ?? false);
        }

        /// <summary>Suma del oro por turno de todas las ciudades de una civilización.</summary>
        public int GetTotalGoldPerTurn(int civIndex)
        {
            int total = 0;
            foreach (var c in _cities)
                if (c.CivIndex == civIndex)
                    total += c.GoldPerTurn;
            return total;
        }

        // ================================================================
        //  TERRITORIO
        // ================================================================

        /// <summary>
        /// Recalcula y dibuja el overlay de territorio para todas las civilizaciones.
        /// Radio de territorio = 1 + floor(population / 2), máximo 3.
        /// </summary>
        public void RefreshTerritory()
        {
            var tilesByCiv = new Dictionary<int, HashSet<HexCoord>>();

            foreach (var city in _cities)
            {
                if (!tilesByCiv.TryGetValue(city.CivIndex, out var set))
                {
                    set = new HashSet<HexCoord>();
                    tilesByCiv[city.CivIndex] = set;
                }

                int radius = Mathf.Min(1 + city.Population / 2, 3);
                foreach (var hex in HexesInRadius(city.Q, city.R, radius))
                {
                    // Solo tiles que existen en el mapa
                    if (_map.GetTileType(hex.Q, hex.R) != null)
                        set.Add(hex);
                }
            }

            // Pasar a MapOverlay
            for (int civ = 0; civ < TerritoryColor.Length; civ++)
            {
                tilesByCiv.TryGetValue(civ, out var tiles);
                _overlay.SetTerritoryTiles(civ, tiles ?? new HashSet<HexCoord>());
            }
        }

        // ================================================================
        //  TURNO
        // ================================================================

        public void ProcessTurn()
        {
            bool territoryChanged = false;

            foreach (var city in _cities)
            {
                int popBefore = city.Population;
                var result = city.ProcessTurn(_map);

                if (city.Population != popBefore)
                {
                    city.TryRebuildVisuals();
                    territoryChanged = true;
                    if (city.CivIndex == 0)
                        EmitSignal(SignalName.CityEvent,
                            $"📈  {city.CityName} creció a {city.Population} habitantes");
                }

                if (result.HasValue)
                {
                    if (result.Value.IsUnit)
                    {
                        EmitSignal(SignalName.UnitProductionComplete,
                                   city.Q, city.R, city.CivIndex, city.CivColor, result.Value.TypeInt);
                        if (city.CivIndex == 0)
                            EmitSignal(SignalName.CityEvent,
                                $"⚒  {city.CityName} completó: {UnitTypeData.GetStats((UnitType)result.Value.TypeInt).DisplayName}");
                    }
                    else
                    {
                        var bt = (BuildingType)result.Value.TypeInt;
                        city.AddBuildingVisual(bt);
                        if (city.CivIndex == 0)
                        {
                            var bStats = BuildingTypeData.GetStats(bt);
                            EmitSignal(SignalName.CityEvent,
                                $"🏛  {city.CityName} construyó: {bStats.DisplayName}");
                        }
                    }
                }
            }

            if (territoryChanged) RefreshTerritory();
        }

        // ================================================================
        //  HELPERS
        // ================================================================

        private string GenerateName(int civIndex)
        {
            int   safe   = civIndex < CityNamePool.Length ? civIndex : 0;
            var   pool   = CityNamePool[safe];
            int   cursor = _nameCursors[safe]++;
            return cursor < pool.Length ? pool[cursor] : $"Ciudad {cursor + 1}";
        }

        private static int HexDistance(int q1, int r1, int q2, int r2)
        {
            int s1 = -q1 - r1, s2 = -q2 - r2;
            return (Mathf.Abs(q1 - q2) + Mathf.Abs(r1 - r2) + Mathf.Abs(s1 - s2)) / 2;
        }

        /// <summary>Todos los hexes dentro de un radio (algoritmo axial estándar).</summary>
        private static IEnumerable<HexCoord> HexesInRadius(int q, int r, int radius)
        {
            for (int dq = -radius; dq <= radius; dq++)
            {
                int drMin = Mathf.Max(-radius, -dq - radius);
                int drMax = Mathf.Min( radius, -dq + radius);
                for (int dr = drMin; dr <= drMax; dr++)
                    yield return new HexCoord(q + dq, r + dr);
            }
        }
    }
}
