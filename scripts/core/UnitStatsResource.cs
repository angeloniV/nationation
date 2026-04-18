using Godot;

namespace Natiolation.Core
{
    /// <summary>
    /// Resource nativo de Godot que representa las estadísticas de un tipo de unidad.
    ///
    /// Para sobreescribir una unidad, crea un archivo .tres en:
    ///   res://resources/data/units/Warrior.tres  (nombre = enum UnitType)
    ///
    /// Los campos no sobreescritos toman el valor de las constantes C# en UnitTypeData.
    /// </summary>
    [GlobalClass]
    public partial class UnitStatsResource : Resource
    {
        [Export] public string DisplayName        { get; set; } = "";
        [Export] public string Symbol             { get; set; } = "?";
        [Export] public int    MaxMovement        { get; set; } = 2;
        [Export] public int    CombatStrength     { get; set; } = 0;
        [Export] public int    RangedStrength     { get; set; } = 0;
        [Export] public bool   CanFoundCity       { get; set; } = false;
        [Export] public bool   CanBuildImprovements{ get; set; } = false;
        [Export] public int    SightRange         { get; set; } = 2;
        [Export] public int    ProductionCost     { get; set; } = 20;
    }
}
