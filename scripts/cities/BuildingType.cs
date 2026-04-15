using System.Collections.Generic;

namespace Natiolation.Cities
{
    public enum BuildingType
    {
        Granary,
        Market,
        Workshop,
        Barracks,
        CityWalls,
        Temple,
        Library,
        Forge,
        Harbor,
        University,
    }

    public record BuildingStats(
        string DisplayName,
        string Description,
        int    ProductionCost,
        int    MaintenanceCost
    );

    public static class BuildingTypeData
    {
        private static readonly Dictionary<BuildingType, BuildingStats> _stats = new()
        {
            [BuildingType.Granary]    = new("Granero",     "+50% comida al crecer",         40,  1),
            [BuildingType.Market]     = new("Mercado",     "+50% oro / turno",              60,  1),
            [BuildingType.Workshop]   = new("Taller",      "+2 producción / turno",         60,  1),
            [BuildingType.Barracks]   = new("Cuartel",     "Unidades con +1 ataque",        40,  1),
            [BuildingType.CityWalls]  = new("Murallas",    "×2 defensa ciudad",             60,  2),
            [BuildingType.Temple]     = new("Templo",      "+1 oro / turno",                40,  1),
            [BuildingType.Library]    = new("Biblioteca",  "+3 ciencia / turno",            60,  1),
            [BuildingType.Forge]      = new("Forja",       "+1 producción / turno",         50,  1),
            [BuildingType.Harbor]     = new("Puerto",      "+2 oro (ciudad costera)",       60,  1),
            [BuildingType.University] = new("Universidad", "+5 ciencia / turno (req. Bib.)",100, 2),
        };

        public static BuildingStats GetStats(BuildingType type) => _stats[type];
    }
}
