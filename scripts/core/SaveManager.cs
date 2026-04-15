using Godot;
using System.Collections.Generic;

namespace Natiolation.Core
{
    /// <summary>
    /// Gestiona guardado y carga de partida en JSON.
    /// Guardar: Ctrl+S en cualquier momento.
    /// Archivo: user://save.json
    /// </summary>
    public partial class SaveManager : Node
    {
        private const string SavePath = "user://save.json";

        public static SaveManager? Instance { get; private set; }

        public override void _Ready()
        {
            Instance = this;
        }

        public override void _Input(InputEvent @event)
        {
            if (@event is InputEventKey key && key.Pressed && !key.Echo)
            {
                if (key.Keycode == Key.S && key.CtrlPressed)
                    Save();
            }
        }

        // ================================================================
        //  GUARDADO
        // ================================================================

        /// <summary>Serializa el estado de la partida a JSON y lo escribe en disco.</summary>
        public bool Save()
        {
            var gm  = GameManager.Instance;
            var map = GetNode<Map.MapManager>("/root/Main/MapManager");
            var um  = GetNode<Units.UnitManager>("/root/Main/UnitManager");
            var cm  = GetNode<Cities.CityManager>("/root/Main/CityManager");

            if (gm == null || map == null) return false;

            var data = new Godot.Collections.Dictionary
            {
                ["seed"]   = map.Seed,
                ["turn"]   = gm.CurrentTurn,
                ["gold"]   = gm.Gold,
                ["science"]= gm.ScienceStored,
            };

            // Tecnologías investigadas
            var techs = new Godot.Collections.Array<int>();
            foreach (Technology tech in System.Enum.GetValues<Technology>())
                if (gm.HasTech(tech)) techs.Add((int)tech);
            data["techs"] = techs;

            if (gm.CurrentResearch.HasValue)
                data["current_research"] = (int)gm.CurrentResearch.Value;
            else
                data["current_research"] = -1;

            // Unidades
            var units = new Godot.Collections.Array<Godot.Collections.Dictionary>();
            foreach (var unit in um.AllUnits)
            {
                units.Add(new Godot.Collections.Dictionary
                {
                    ["type"]      = (int)unit.UnitType,
                    ["civ"]       = unit.CivIndex,
                    ["q"]         = unit.Q,
                    ["r"]         = unit.R,
                    ["moves"]     = unit.RemainingMovement,
                    ["veteran"]   = unit.IsVeteran,
                    ["fortified"] = unit.IsFortified,
                });
            }
            data["units"] = units;

            // Ciudades
            var cities = new Godot.Collections.Array<Godot.Collections.Dictionary>();
            foreach (var city in cm.AllCities)
            {
                cities.Add(new Godot.Collections.Dictionary
                {
                    ["name"]  = city.CityName,
                    ["civ"]   = city.CivIndex,
                    ["q"]     = city.Q,
                    ["r"]     = city.R,
                    ["pop"]   = city.Population,
                    ["food"]  = city.FoodStored,
                    ["prod"]  = city.ProdStored,
                });
            }
            data["cities"] = cities;

            using var file = Godot.FileAccess.Open(SavePath, Godot.FileAccess.ModeFlags.Write);
            if (file == null)
            {
                GD.PrintErr("[SaveManager] No se pudo abrir save.json para escritura");
                return false;
            }
            file.StoreString(Json.Stringify(data, indent: "  "));
            GD.Print("[SaveManager] Partida guardada ✓");

            // Notificar al HUD
            var hud = GetNodeOrNull<UI.GameHUD>("/root/Main/GameHUD");
            hud?.ShowToast("💾  Partida guardada");

            return true;
        }

        // ================================================================
        //  CARGA (futuro — placeholder)
        // ================================================================

        /// <summary>
        /// Carga el estado guardado. Devuelve true si tuvo éxito.
        /// Llamar antes de cambiar a la escena principal.
        /// </summary>
        public static bool Load()
        {
            if (!Godot.FileAccess.FileExists(SavePath)) return false;
            // TODO: deserializar JSON y pasar los datos a MapManager/UnitManager/CityManager
            // Por ahora solo lee la semilla para que el mapa se genere con la misma semilla
            using var file = Godot.FileAccess.Open(SavePath, Godot.FileAccess.ModeFlags.Read);
            if (file == null) return false;
            var json = new Json();
            if (json.Parse(file.GetAsText()) != Error.Ok) return false;
            var data = json.Data.AsGodotDictionary();
            if (data.ContainsKey("seed") && GameSettings.Instance != null)
                GameSettings.Instance.MapSeed = data["seed"].AsInt32();
            return true;
        }
    }
}
