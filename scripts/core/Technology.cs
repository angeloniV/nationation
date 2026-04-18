using System.Collections.Generic;
using Natiolation.Cities;
using Natiolation.Units;

namespace Natiolation.Core
{
    public enum Technology
    {
        // ── Antigüedad ───────────────────────────────────────────────────
        Archery,
        BronzeWorking,
        Writing,
        Masonry,
        // ── Clásica ──────────────────────────────────────────────────────
        IronWorking,
        Mathematics,
        Currency,
        Philosophy,
        // ── Medieval ─────────────────────────────────────────────────────
        Steel,
        Gunpowder,
    }

    public record TechStats(
        string         DisplayName,
        string         Description,
        int            ResearchCost,
        Technology[]   Prerequisites,
        UnitType[]     UnlocksUnits,
        BuildingType[] UnlocksBuildings
    );

    public static class TechnologyData
    {
        // Costos calibrados para 8-12 turnos al inicio (1 ciencia/turno = 1 ciudad sin Library).
        // Antigüedad: 8-12 turnos  → costos 8-12
        // Clásica:    ~20-25 turnos → costos 20-25 (con varias ciudades + Library)
        // Medieval:   ~30-40 turnos → costos 30-40 (economía de ciencia ya desarrollada)
        // Referencia: Civ 5/6 — primeras tecnologías en 8-10 turnos al inicio.
        private static readonly Dictionary<Technology, TechStats> _stats = new()
        {
            // ── Antigüedad ────────────────────────────────────────────────
            [Technology.Archery] = new(
                "Tiro con Arco",
                "Los arqueros de largo alcance se vuelven el núcleo de tu ejército.\nDesbloquea: Ballestero.",
                9,
                new Technology[] { },
                new[] { UnitType.Longbowman },
                new BuildingType[] { }
            ),
            [Technology.BronzeWorking] = new(
                "Trabajo en Bronce",
                "Las aleaciones de metal permiten forjar espadas resistentes.\nDesbloquea: Espadachín.",
                10,
                new Technology[] { },
                new[] { UnitType.Swordsman },
                new BuildingType[] { }
            ),
            [Technology.Writing] = new(
                "Escritura",
                "El conocimiento se vuelve transmisible entre generaciones.\nDesbloquea: Biblioteca (+3🔬/turno).",
                12,
                new Technology[] { },
                new UnitType[] { },
                new[] { BuildingType.Library }
            ),
            [Technology.Masonry] = new(
                "Cantería",
                "Técnicas avanzadas de corte y ensamble de piedra.\nDesbloquea: Forja (+1⚒).",
                10,
                new Technology[] { },
                new UnitType[] { },
                new[] { BuildingType.Forge }
            ),

            // ── Clásica ───────────────────────────────────────────────────
            [Technology.IronWorking] = new(
                "Trabajo en Hierro",
                "El hierro reemplaza al bronce: armaduras más pesadas, armas más afiladas.\nReq: Trabajo en Bronce.  Desbloquea: Caballero.",
                22,
                new[] { Technology.BronzeWorking },
                new[] { UnitType.Knight },
                new BuildingType[] { }
            ),
            [Technology.Mathematics] = new(
                "Matemáticas",
                "La geometría aplicada permite construir máquinas de asedio complejas.\nReq: Escritura.  Desbloquea: Ballista.",
                22,
                new[] { Technology.Writing },
                new[] { UnitType.Ballista },
                new BuildingType[] { }
            ),
            [Technology.Currency] = new(
                "Moneda",
                "Un sistema monetario estandarizado acelera el comercio entre ciudades.\nReq: Escritura.  Desbloquea: Puerto (+2💰 en costa).",
                20,
                new[] { Technology.Writing },
                new UnitType[] { },
                new[] { BuildingType.Harbor }
            ),
            [Technology.Philosophy] = new(
                "Filosofía",
                "El pensamiento racional y el debate sistemático abren el conocimiento.\nReq: Escritura.  Desbloquea: Universidad (+5🔬).",
                22,
                new[] { Technology.Writing },
                new UnitType[] { },
                new[] { BuildingType.University }
            ),

            // ── Medieval ──────────────────────────────────────────────────
            [Technology.Steel] = new(
                "Acero",
                "La fundición avanzada produce acero templado de alta resistencia.\nReq: Trabajo en Hierro.  Desbloquea: Espadón.",
                35,
                new[] { Technology.IronWorking },
                new[] { UnitType.Longswordsman },
                new BuildingType[] { }
            ),
            [Technology.Gunpowder] = new(
                "Pólvora",
                "La mezcla de nitrato, carbón y azufre cambia la guerra para siempre.\nReq: Acero.  Desbloquea: Mosquetero.",
                40,
                new[] { Technology.Steel },
                new[] { UnitType.Musketman },
                new BuildingType[] { }
            ),
        };

        /// <summary>
        /// Devuelve los datos de la tecnología.
        /// Prioridad: archivo .tres en res://resources/data/techs/{t}.tres
        /// Fallback: diccionario C# hardcodeado en este archivo.
        /// </summary>
        public static TechStats GetStats(Technology t)
        {
            string path = $"res://resources/data/techs/{t}.tres";
            if (Godot.ResourceLoader.Exists(path))
            {
                var res = Godot.GD.Load<TechResource>(path);
                if (res != null)
                {
                    return new TechStats(
                        res.DisplayName,
                        res.Description,
                        res.ResearchCost,
                        System.Array.ConvertAll(res.Prerequisites,    p => (Technology)p),
                        System.Array.ConvertAll(res.UnlocksUnits,     u => (Units.UnitType)u),
                        System.Array.ConvertAll(res.UnlocksBuildings, b => (Cities.BuildingType)b)
                    );
                }
            }
            return _stats[t];
        }

        /// <summary>Tecnología requerida para producir una unidad (null = libre desde el inicio).</summary>
        public static Technology? RequiredTechForUnit(UnitType u)
        {
            foreach (var kv in _stats)
                foreach (var ut in kv.Value.UnlocksUnits)
                    if (ut == u) return kv.Key;
            return null;
        }

        /// <summary>Tecnología requerida para construir un edificio (null = libre desde el inicio).</summary>
        public static Technology? RequiredTechForBuilding(BuildingType b)
        {
            foreach (var kv in _stats)
                foreach (var bt in kv.Value.UnlocksBuildings)
                    if (bt == b) return kv.Key;
            return null;
        }
    }
}
