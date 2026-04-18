using Natiolation.Core;

namespace Natiolation.Units
{
    public enum UnitType
    {
        // Disponibles desde el inicio
        Settler,
        Warrior,
        Worker,
        Archer,
        Scout,
        // Requieren tecnología
        Longbowman,    // req: Tiro con Arco
        Swordsman,     // req: Trabajo en Bronce
        Knight,        // req: Trabajo en Hierro
        Ballista,      // req: Matemáticas
        Longswordsman, // req: Acero
        Musketman,     // req: Pólvora
    }

    public record UnitStats(
        int    MaxMovement,
        int    CombatStrength,
        int    RangedStrength,
        bool   CanFoundCity,
        bool   CanBuildImprovements,
        int    SightRange,
        int    ProductionCost,
        string Symbol,
        string DisplayName
    );

    public static class UnitTypeData
    {
        /// <summary>
        /// Devuelve las estadísticas de la unidad.
        /// Prioridad: archivo .tres en res://resources/data/units/{type}.tres
        /// Fallback: valores hardcodeados en C#.
        /// </summary>
        public static UnitStats GetStats(UnitType type)
        {
            string path = $"res://resources/data/units/{type}.tres";
            if (Godot.ResourceLoader.Exists(path))
            {
                var res = Godot.GD.Load<UnitStatsResource>(path);
                if (res != null)
                {
                    return new UnitStats(
                        res.MaxMovement,
                        res.CombatStrength,
                        res.RangedStrength,
                        res.CanFoundCity,
                        res.CanBuildImprovements,
                        res.SightRange,
                        res.ProductionCost,
                        res.Symbol,
                        res.DisplayName
                    );
                }
            }
            return GetHardcoded(type);
        }

        // ── Valores C# de referencia (fallback cuando no hay .tres) ─────
        private static UnitStats GetHardcoded(UnitType type) => type switch
        {
            //                                  mov  cs   rs  city   build  sight  cost   sym  name
            UnitType.Settler      => new( 2,   0,   0,  true,  false, 2,   40, "S", "Colono"),
            UnitType.Warrior      => new( 2,   8,   0,  false, false, 2,   20, "W", "Guerrero"),
            UnitType.Worker       => new( 2,   0,   0,  false, true,  1,   30, "B", "Constructor"),
            UnitType.Archer       => new( 2,   5,   8,  false, false, 2,   24, "A", "Arquero"),
            UnitType.Scout        => new( 3,   3,   0,  false, false, 4,   15, "E", "Explorador"),
            UnitType.Longbowman   => new( 2,   6,  10,  false, false, 2,   28, "L", "Ballestero"),
            UnitType.Swordsman    => new( 2,  12,   0,  false, false, 2,   35, "X", "Espadachín"),
            UnitType.Knight       => new( 3,  16,   0,  false, false, 3,   60, "K", "Caballero"),
            UnitType.Ballista     => new( 1,   8,  14,  false, false, 2,   55, "T", "Ballista"),
            UnitType.Longswordsman=> new( 2,  18,   0,  false, false, 2,   70, "Z", "Espadón"),
            UnitType.Musketman    => new( 2,  24,   0,  false, false, 2,   80, "M", "Mosquetero"),
            _                     => new( 1,   0,   0,  false, false, 1,   10, "?", "Desconocido")
        };
    }
}
