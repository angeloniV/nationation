using Godot;

namespace Natiolation.Core
{
    /// <summary>
    /// Resource nativo de Godot que representa los datos de una tecnología.
    ///
    /// Al heredar de Resource y usar [GlobalClass], el tipo aparece en el Editor
    /// de Godot y puede ser editado y guardado como archivo .tres sin recompilar.
    ///
    /// Para sobreescribir una tecnología, crea un archivo .tres en:
    ///   res://resources/data/techs/Archery.tres  (nombre = enum Technology)
    ///
    /// Los arrays de prereqs/desbloqueos usan int (índice del enum correspondiente)
    /// porque Godot no puede exportar arrays de enums personalizados directamente.
    /// </summary>
    [GlobalClass]
    public partial class TechResource : Resource
    {
        [Export] public string DisplayName  { get; set; } = "";
        [Export(PropertyHint.MultilineText)]
        public string Description           { get; set; } = "";
        [Export] public int    ResearchCost { get; set; } = 10;

        /// Índices de Technology enum (ej. 0=Archery, 1=BronzeWorking…)
        [Export] public int[] Prerequisites   { get; set; } = System.Array.Empty<int>();
        /// Índices de UnitType enum
        [Export] public int[] UnlocksUnits    { get; set; } = System.Array.Empty<int>();
        /// Índices de BuildingType enum
        [Export] public int[] UnlocksBuildings{ get; set; } = System.Array.Empty<int>();
    }
}
