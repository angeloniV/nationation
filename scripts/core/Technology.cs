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
        // Costos calibrados para ~4-8 turnos con 2-3 ciencia/turno (sin Library),
        // y ~2-4 turnos cuando la ciudad tiene Library (+3 ciencia).
        // Referencia: Civ 5/6 — primeras tecnologías en 3-6 turnos, medievales en 8-15.
        private static readonly Dictionary<Technology, TechStats> _stats = new()
        {
            // ── Antigüedad ────────────────────────────────────────────────
            [Technology.Archery] = new(
                "Tiro con Arco",
                "Los arqueros de largo alcance se vuelven el núcleo de tu ejército.\nDesbloquea: Ballestero.",
                10,
                new Technology[] { },
                new[] { UnitType.Longbowman },
                new BuildingType[] { }
            ),
            [Technology.BronzeWorking] = new(
                "Trabajo en Bronce",
                "Las aleaciones de metal permiten forjar espadas resistentes.\nDesbloquea: Espadachín.",
                12,
                new Technology[] { },
                new[] { UnitType.Swordsman },
                new BuildingType[] { }
            ),
            [Technology.Writing] = new(
                "Escritura",
                "El conocimiento se vuelve transmisible entre generaciones.\nDesbloquea: Biblioteca (+3🔬/turno).",
                14,
                new Technology[] { },
                new UnitType[] { },
                new[] { BuildingType.Library }
            ),
            [Technology.Masonry] = new(
                "Cantería",
                "Técnicas avanzadas de corte y ensamble de piedra.\nDesbloquea: Forja (+1⚒).",
                11,
                new Technology[] { },
                new UnitType[] { },
                new[] { BuildingType.Forge }
            ),

            // ── Clásica ───────────────────────────────────────────────────
            [Technology.IronWorking] = new(
                "Trabajo en Hierro",
                "El hierro reemplaza al bronce: armaduras más pesadas, armas más afiladas.\nReq: Trabajo en Bronce.  Desbloquea: Caballero.",
                18,
                new[] { Technology.BronzeWorking },
                new[] { UnitType.Knight },
                new BuildingType[] { }
            ),
            [Technology.Mathematics] = new(
                "Matemáticas",
                "La geometría aplicada permite construir máquinas de asedio complejas.\nReq: Escritura.  Desbloquea: Ballista.",
                17,
                new[] { Technology.Writing },
                new[] { UnitType.Ballista },
                new BuildingType[] { }
            ),
            [Technology.Currency] = new(
                "Moneda",
                "Un sistema monetario estandarizado acelera el comercio entre ciudades.\nReq: Escritura.  Desbloquea: Puerto (+2💰 en costa).",
                16,
                new[] { Technology.Writing },
                new UnitType[] { },
                new[] { BuildingType.Harbor }
            ),
            [Technology.Philosophy] = new(
                "Filosofía",
                "El pensamiento racional y el debate sistemático abren el conocimiento.\nReq: Escritura.  Desbloquea: Universidad (+5🔬).",
                17,
                new[] { Technology.Writing },
                new UnitType[] { },
                new[] { BuildingType.University }
            ),

            // ── Medieval ──────────────────────────────────────────────────
            [Technology.Steel] = new(
                "Acero",
                "La fundición avanzada produce acero templado de alta resistencia.\nReq: Trabajo en Hierro.  Desbloquea: Espadón.",
                24,
                new[] { Technology.IronWorking },
                new[] { UnitType.Longswordsman },
                new BuildingType[] { }
            ),
            [Technology.Gunpowder] = new(
                "Pólvora",
                "La mezcla de nitrato, carbón y azufre cambia la guerra para siempre.\nReq: Acero.  Desbloquea: Mosquetero.",
                28,
                new[] { Technology.Steel },
                new[] { UnitType.Musketman },
                new BuildingType[] { }
            ),
        };

        public static TechStats GetStats(Technology t) => _stats[t];

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
