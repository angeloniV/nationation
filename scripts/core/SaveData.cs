namespace Natiolation.Core
{
    // =========================================================================
    //  CLASES POCO PARA SERIALIZACIÓN DE PARTIDA
    //
    //  Reglas de oro:
    //    • Cero tipos de Godot (no Node, no Color, no Vector2/3).
    //    • Todos los campos son tipos primitivos C# o arrays de primitivos.
    //    • System.Text.Json puede serializar/deserializar sin configuración extra.
    //    • Inmutables conceptualmente: se crean en Save() y se consumen en Load();
    //      nunca se pasan referencias mutable a código externo.
    // =========================================================================

    /// <summary>
    /// Raíz del archivo de guardado. Contiene todo el estado necesario para
    /// restaurar una partida exactamente como estaba al guardar.
    /// </summary>
    public sealed class GameSaveData
    {
        // ── Metadatos de mapa ────────────────────────────────────────────────
        /// <summary>Seed usada para generar el mapa. Garantiza el mismo terreno al cargar.</summary>
        public int Seed { get; set; }

        // ── Estado global de juego ────────────────────────────────────────────
        public int Turn            { get; set; }
        public int Gold            { get; set; }
        public int Science         { get; set; }

        /// <summary>Cast de Technology enum. Vacío si ninguna investigada.</summary>
        public int[] ResearchedTechs { get; set; } = System.Array.Empty<int>();

        /// <summary>Cast de Technology enum. -1 si no hay investigación activa.</summary>
        public int   CurrentResearch  { get; set; } = -1;

        // ── Entidades ────────────────────────────────────────────────────────
        public UnitSaveData[]        Units        { get; set; } = System.Array.Empty<UnitSaveData>();
        public CitySaveData[]        Cities       { get; set; } = System.Array.Empty<CitySaveData>();
        public ImprovementSaveData[] Improvements { get; set; } = System.Array.Empty<ImprovementSaveData>();
    }

    /// <summary>Estado puro de una unidad. Sin referencia al Node3D.</summary>
    public sealed class UnitSaveData
    {
        /// <summary>Cast de UnitType enum.</summary>
        public int   UnitType    { get; set; }
        public int   CivIndex    { get; set; }
        public int   Q           { get; set; }
        public int   R           { get; set; }
        public float MovesLeft   { get; set; }
        public int   CurrentHP   { get; set; }
        public bool  IsVeteran   { get; set; }
        public bool  IsFortified { get; set; }
        // Color de civilización — canales RGB en [0,1]
        public float CivR        { get; set; }
        public float CivG        { get; set; }
        public float CivB        { get; set; }
    }

    /// <summary>Estado puro de una ciudad. Sin referencia al Node3D.</summary>
    public sealed class CitySaveData
    {
        public string Name            { get; set; } = "";
        public int    CivIndex        { get; set; }
        public int    Q               { get; set; }
        public int    R               { get; set; }
        public int    Population      { get; set; } = 1;
        public int    FoodStored      { get; set; }
        public int    ProdStored      { get; set; }

        /// <summary>Array de casts de BuildingType enum.</summary>
        public int[]  Buildings        { get; set; } = System.Array.Empty<int>();

        /// <summary>Cast de UnitType en producción. -1 = ninguna.</summary>
        public int    BuildingUnit     { get; set; } = -1;

        /// <summary>Cast de BuildingType en producción. -1 = ninguno.</summary>
        public int    BuildingBuilding { get; set; } = -1;

        // Color de civilización
        public float  CivR             { get; set; }
        public float  CivG             { get; set; }
        public float  CivB             { get; set; }
    }

    /// <summary>Mejora de terreno en un tile específico.</summary>
    public sealed class ImprovementSaveData
    {
        public int Q           { get; set; }
        public int R           { get; set; }
        /// <summary>Cast de TileImprovement enum.</summary>
        public int Improvement { get; set; }
    }
}
