namespace Natiolation.Map
{
    /// <summary>Mejoras que un Constructor puede edificar sobre un tile.</summary>
    public enum TileImprovement
    {
        None,
        Irrigation,   // +1 comida; tierra no fértil
        Farm,         // +2 comida; solo Llanura/Pastizal
        Road,         // costo 1/3 mov al entrar
        Mine          // +1 producción; solo colinas/montañas
    }

    public enum TileType
    {
        Ocean,
        Coast,
        Plains,
        Grassland,
        Hills,
        Forest,
        Mountains,
        Desert,
        Tundra,
        Arctic
    }

    public static class TileTypeExtensions
    {
        /// <summary>
        /// Costo de movimiento base para entrar a este tile.
        /// </summary>
        public static int MovementCost(this TileType type) => type switch
        {
            TileType.Ocean      => 0,   // no caminable (unidades navales)
            TileType.Coast      => 0,
            TileType.Plains     => 1,
            TileType.Grassland  => 1,
            TileType.Desert     => 1,
            TileType.Tundra     => 1,
            TileType.Hills      => 2,
            TileType.Forest     => 2,
            TileType.Mountains  => 3,
            TileType.Arctic     => 3,
            _                   => 1
        };

        /// <summary>
        /// Produccion de comida base del tile.
        /// </summary>
        public static int FoodYield(this TileType type) => type switch
        {
            TileType.Grassland  => 2,
            TileType.Plains     => 1,
            TileType.Hills      => 1,
            TileType.Forest     => 1,
            TileType.Tundra     => 0,
            TileType.Desert     => 0,
            TileType.Arctic     => 0,
            TileType.Mountains  => 0,
            _                   => 0
        };

        /// <summary>
        /// Produccion de produccion base del tile.
        /// </summary>
        public static int ProductionYield(this TileType type) => type switch
        {
            TileType.Hills      => 2,
            TileType.Mountains  => 1,
            TileType.Forest     => 2,
            TileType.Plains     => 1,
            TileType.Grassland  => 0,
            _                   => 0
        };

        /// <summary>
        /// Nombre localizado del tipo de tile.
        /// </summary>
        public static string TileName(this TileType type) => type switch
        {
            TileType.Ocean     => "Oceano",
            TileType.Coast     => "Costa",
            TileType.Plains    => "Llanura",
            TileType.Grassland => "Pastizal",
            TileType.Hills     => "Colinas",
            TileType.Forest    => "Bosque",
            TileType.Mountains => "Montanas",
            TileType.Desert    => "Desierto",
            TileType.Tundra    => "Tundra",
            TileType.Arctic    => "Artico",
            _                  => "Desconocido"
        };

        /// <summary>
        /// Produccion de oro/comercio base del tile.
        /// </summary>
        public static int GoldYield(this TileType type) => type switch
        {
            TileType.Ocean  => 2,
            TileType.Coast  => 1,
            _               => 0
        };

        /// <summary>
        /// Color de representacion en el mapa (placeholder hasta tener sprites).
        /// </summary>
        public static Godot.Color MapColor(this TileType type) => type switch
        {
            TileType.Ocean      => new Godot.Color(0.10f, 0.30f, 0.70f),
            TileType.Coast      => new Godot.Color(0.20f, 0.50f, 0.85f),
            TileType.Plains     => new Godot.Color(0.76f, 0.80f, 0.40f),
            TileType.Grassland  => new Godot.Color(0.30f, 0.70f, 0.25f),
            TileType.Hills      => new Godot.Color(0.60f, 0.55f, 0.30f),
            TileType.Forest     => new Godot.Color(0.10f, 0.45f, 0.15f),
            TileType.Mountains  => new Godot.Color(0.55f, 0.55f, 0.55f),
            TileType.Desert     => new Godot.Color(0.90f, 0.85f, 0.45f),
            TileType.Tundra     => new Godot.Color(0.70f, 0.78f, 0.78f),
            TileType.Arctic     => new Godot.Color(0.92f, 0.95f, 1.00f),
            _                   => Godot.Colors.Magenta
        };
    }
}
