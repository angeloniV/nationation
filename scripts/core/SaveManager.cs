using Godot;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Natiolation.Core
{
    /// <summary>
    /// Gestiona el guardado y la carga de partida.
    ///
    /// Guardado (Ctrl+S): extrae el estado puro de cada manager a clases POCO
    ///   (GameSaveData / UnitSaveData / CitySaveData / ImprovementSaveData),
    ///   luego serializa con System.Text.Json. Ningún Node ni tipo de Godot
    ///   toca el archivo JSON.
    ///
    /// Carga (Load + ApplyPendingLoad):
    ///   1. Load() (estático) deserializa el JSON y guarda el GameSaveData en
    ///      GameSettings.PendingLoad antes de cambiar de escena.
    ///   2. _Ready() llama a ApplyPendingLoad() via CallDeferred, asegurando
    ///      que TODOS los _Ready() de managers ya se ejecutaron.
    ///   3. ApplyPendingLoad() reparte los datos a cada manager y luego limpia
    ///      GameSettings.PendingLoad.
    /// </summary>
    public partial class SaveManager : Node
    {
        private const string SavePath = "user://save.json";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented              = true,
            PropertyNameCaseInsensitive= true,
            DefaultIgnoreCondition     = JsonIgnoreCondition.WhenWritingNull,
        };

        public static SaveManager? Instance { get; private set; }

        public override void _Ready()
        {
            Instance = this;

            // Si hay datos pendientes de carga, aplicarlos en el próximo frame
            // (garantiza que todos los _Ready() de los managers ya corrieron).
            if (GameSettings.Instance?.PendingLoad != null)
                CallDeferred(MethodName.ApplyPendingLoad);
        }

        public override void _Input(InputEvent @event)
        {
            if (@event is InputEventKey key && key.Pressed && !key.Echo
                && key.Keycode == Key.S && key.CtrlPressed)
                Save();
        }

        // ================================================================
        //  GUARDADO
        // ================================================================

        /// <summary>
        /// Serializa el estado de la partida a JSON y lo escribe en disco.
        /// No serializa ningún Node — solo extrae estado puro a POCOs.
        /// </summary>
        public bool Save()
        {
            var gm  = GameManager.Instance;
            var map = GetNodeOrNull<Map.MapManager>("/root/Main/MapManager");
            var um  = GetNodeOrNull<Units.UnitManager>("/root/Main/UnitManager");
            var cm  = GetNodeOrNull<Cities.CityManager>("/root/Main/CityManager");

            if (gm == null || map == null || um == null || cm == null)
            {
                GD.PrintErr("[SaveManager] No se puede guardar: algún manager no está disponible.");
                return false;
            }

            var data = ExtractSaveData(gm, map, um, cm);

            string json;
            try   { json = JsonSerializer.Serialize(data, JsonOpts); }
            catch (System.Exception ex)
            {
                GD.PrintErr($"[SaveManager] Error al serializar: {ex.Message}");
                return false;
            }

            using var file = Godot.FileAccess.Open(SavePath, Godot.FileAccess.ModeFlags.Write);
            if (file == null)
            {
                GD.PrintErr("[SaveManager] No se pudo abrir save.json para escritura.");
                return false;
            }
            file.StoreString(json);

            GD.Print($"[SaveManager] Partida guardada ✓  " +
                     $"({data.Units.Length} unidades, {data.Cities.Length} ciudades, " +
                     $"{data.Improvements.Length} mejoras)");

            GetNodeOrNull<UI.GameHUD>("/root/Main/GameHUD")?.ShowToast("💾  Partida guardada");
            return true;
        }

        // ── Extracción de estado a POCOs (sin Godot types) ──────────────────

        private static GameSaveData ExtractSaveData(
            GameManager        gm,
            Map.MapManager     map,
            Units.UnitManager  um,
            Cities.CityManager cm)
        {
            // ── Tecnologías ──────────────────────────────────────────────────
            var researchedList = new List<int>();
            foreach (Technology t in System.Enum.GetValues<Technology>())
                if (gm.HasTech(t)) researchedList.Add((int)t);

            // ── Unidades ─────────────────────────────────────────────────────
            var units = new List<UnitSaveData>(um.AllUnits.Count);
            foreach (var unit in um.AllUnits)
            {
                units.Add(new UnitSaveData
                {
                    UnitType    = (int)unit.UnitType,
                    CivIndex    = unit.CivIndex,
                    Q           = unit.Q,
                    R           = unit.R,
                    MovesLeft   = unit.RemainingMovement,
                    CurrentHP   = unit.CurrentHP,
                    IsVeteran   = unit.IsVeteran,
                    IsFortified = unit.IsFortified,
                    CivR        = unit.CivColor.R,
                    CivG        = unit.CivColor.G,
                    CivB        = unit.CivColor.B,
                });
            }

            // ── Ciudades ─────────────────────────────────────────────────────
            var cities = new List<CitySaveData>(cm.AllCities.Count);
            foreach (var city in cm.AllCities)
            {
                var buildingsArr = new List<int>(city.Buildings.Count);
                foreach (var b in city.Buildings) buildingsArr.Add((int)b);

                cities.Add(new CitySaveData
                {
                    Name             = city.CityName,
                    CivIndex         = city.CivIndex,
                    Q                = city.Q,
                    R                = city.R,
                    Population       = city.Population,
                    FoodStored       = city.FoodStored,
                    ProdStored       = city.ProdStored,
                    Buildings        = buildingsArr.ToArray(),
                    BuildingUnit     = city.BuildingUnit.HasValue     ? (int)city.BuildingUnit.Value     : -1,
                    BuildingBuilding = city.BuildingBuilding.HasValue ? (int)city.BuildingBuilding.Value : -1,
                    CivR             = city.CivColor.R,
                    CivG             = city.CivColor.G,
                    CivB             = city.CivColor.B,
                });
            }

            // ── Mejoras de terreno ────────────────────────────────────────────
            var improvements = new List<ImprovementSaveData>();
            foreach (var (q, r, imp) in map.GetAllImprovements())
                improvements.Add(new ImprovementSaveData { Q = q, R = r, Improvement = (int)imp });

            return new GameSaveData
            {
                Seed            = map.Seed,
                Turn            = gm.CurrentTurn,
                Gold            = gm.Gold,
                Science         = gm.ScienceStored,
                ResearchedTechs = researchedList.ToArray(),
                CurrentResearch = gm.CurrentResearch.HasValue ? (int)gm.CurrentResearch.Value : -1,
                Units           = units.ToArray(),
                Cities          = cities.ToArray(),
                Improvements    = improvements.ToArray(),
            };
        }

        // ================================================================
        //  CARGA — PASO 1 (estático, antes del cambio de escena)
        // ================================================================

        /// <summary>
        /// Lee el JSON del disco y almacena los datos en GameSettings.PendingLoad.
        /// Devuelve true si el archivo existe y el JSON es válido.
        /// Llamar desde el menú principal antes de cambiar a la escena de juego.
        /// </summary>
        public static bool Load()
        {
            if (!Godot.FileAccess.FileExists(SavePath)) return false;

            string json;
            using (var file = Godot.FileAccess.Open(SavePath, Godot.FileAccess.ModeFlags.Read))
            {
                if (file == null) return false;
                json = file.GetAsText();
            }

            GameSaveData? data;
            try   { data = JsonSerializer.Deserialize<GameSaveData>(json, JsonOpts); }
            catch (System.Exception ex)
            {
                GD.PrintErr($"[SaveManager] JSON inválido: {ex.Message}");
                return false;
            }

            if (data == null) return false;

            // Propagar la seed para que MapManager genere el mismo mapa
            if (GameSettings.Instance != null)
            {
                GameSettings.Instance.MapSeed    = data.Seed;
                GameSettings.Instance.PendingLoad = data;
            }

            GD.Print($"[SaveManager] Datos de guardado listos — seed={data.Seed}, " +
                     $"turno={data.Turn}, {data.Units.Length} unidades.");
            return true;
        }

        // ================================================================
        //  CARGA — PASO 2 (instancia, tras el _Ready() de todos los managers)
        // ================================================================

        /// <summary>
        /// Aplica los datos de GameSettings.PendingLoad a los managers de juego.
        /// Llamado por _Ready() via CallDeferred, garantizando que todos los
        /// _Ready() de managers ya se ejecutaron antes de intentar restaurar.
        /// </summary>
        private void ApplyPendingLoad()
        {
            var settings = GameSettings.Instance;
            if (settings?.PendingLoad == null) return;

            var data = settings.PendingLoad;
            settings.PendingLoad = null;   // consumir para no re-aplicar

            var gm  = GameManager.Instance;
            var map = GetNodeOrNull<Map.MapManager>    ("/root/Main/MapManager");
            var um  = GetNodeOrNull<Units.UnitManager>  ("/root/Main/UnitManager");
            var cm  = GetNodeOrNull<Cities.CityManager> ("/root/Main/CityManager");

            if (gm == null || map == null || um == null || cm == null)
            {
                GD.PrintErr("[SaveManager] ApplyPendingLoad: manager(s) no disponibles.");
                return;
            }

            // Orden: GameManager primero (señales de gold/science conectadas al HUD),
            // luego mapa (mejoras), luego unidades, luego ciudades.
            gm.LoadFrom(data);
            map.RestoreImprovements(data.Improvements);
            um.LoadFromSave(data.Units);
            cm.LoadFromSave(data.Cities, map);

            // Refrescar fog of war con las unidades y ciudades restauradas
            um.RefreshFog();

            GD.Print($"[SaveManager] Partida restaurada ✓  " +
                     $"turno={data.Turn}, {data.Units.Length} unidades, {data.Cities.Length} ciudades.");
        }
    }
}
