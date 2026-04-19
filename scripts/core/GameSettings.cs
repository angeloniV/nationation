using Godot;

namespace Natiolation.Core
{
    /// <summary>
    /// Singleton autoload — pasa configuración de partida entre escenas (menú → mapa).
    /// Registrar en Project → Autoloads como "GameSettings".
    /// </summary>
    public partial class GameSettings : Node
    {
        public static GameSettings? Instance { get; private set; }

        /// <summary>Seed del mapa. 0 = generar aleatoriamente.</summary>
        public int MapSeed { get; set; } = 0;

        /// <summary>True si existe un archivo de guardado.</summary>
        public bool HasSave => Godot.FileAccess.FileExists("user://save.json");

        /// <summary>
        /// Datos de partida pendientes de aplicar al cargar la escena de juego.
        /// SaveManager.Load() los escribe aquí antes de cambiar de escena.
        /// SaveManager.ApplyPendingLoad() los consume y limpia este campo.
        /// </summary>
        public GameSaveData? PendingLoad { get; set; }

        public override void _Ready()
        {
            Instance = this;
            // Sobrevive cambios de escena
            if (GetParent() == GetTree().Root)
                ProcessMode = ProcessModeEnum.Always;
        }
    }
}
