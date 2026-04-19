using Godot;
using System.Collections.Generic;
using System.Linq;
using Natiolation.Map;

namespace Natiolation.Core
{
    /// <summary>
    /// Singleton global. Coordina todos los sistemas del juego.
    /// </summary>
    public partial class GameManager : Node
    {
        public static GameManager Instance { get; private set; } = null!;

        public int CurrentTurn { get; private set; } = 1;
        public int CurrentCivilizationIndex { get; private set; } = 0;

        // ── Economía global ──────────────────────────────────────────────
        public int Gold          { get; private set; } = 50;
        public int GoldLastDelta { get; private set; } = 0;

        /// <summary>Emitida cuando el oro cambia (al final de turno o al cargar partida).</summary>
        [Signal] public delegate void GoldChangedEventHandler(int amount, int delta);

        /// <summary>Aplica un cambio de oro (income - upkeep) y registra el delta para el HUD.</summary>
        public void ApplyGoldDelta(int delta)
        {
            GoldLastDelta = delta;
            Gold          = Mathf.Max(0, Gold + delta);
            EmitSignal(SignalName.GoldChanged, Gold, GoldLastDelta);
        }

        // ── Diplomacia ───────────────────────────────────────────────────
        private const int MaxCivs = 4;
        private readonly bool[,] _atWar = new bool[MaxCivs, MaxCivs];

        [Signal] public delegate void WarDeclaredEventHandler(int civA, int civB);

        public bool IsAtWar(int a, int b)
        {
            if (a == b || a < 0 || b < 0 || a >= MaxCivs || b >= MaxCivs) return false;
            return _atWar[a, b];
        }

        public void DeclareWar(int a, int b)
        {
            if (a == b || a < 0 || b < 0 || a >= MaxCivs || b >= MaxCivs) return;
            if (_atWar[a, b]) return;   // ya en guerra
            _atWar[a, b] = true;
            _atWar[b, a] = true;
            GD.Print($"[GameManager] ¡Guerra declarada! civ{a} vs civ{b}");
            EmitSignal(SignalName.WarDeclared, a, b);
        }

        public void MakePeace(int a, int b)
        {
            if (a == b || a < 0 || b < 0 || a >= MaxCivs || b >= MaxCivs) return;
            _atWar[a, b] = false;
            _atWar[b, a] = false;
        }

        // ── Investigación ─────────────────────────────────────────────────
        public int            ScienceStored    { get; private set; } = 0;
        public int            ScienceLastDelta { get; private set; } = 0;
        public Technology?    CurrentResearch  { get; private set; }

        /// <summary>Emitida cuando la ciencia acumulada cambia.</summary>
        [Signal] public delegate void ScienceChangedEventHandler(int amount, int delta);

        private readonly HashSet<Technology> _researched = new();
        public IReadOnlySet<Technology> ResearchedTechs => _researched;

        [Signal] public delegate void TechResearchedEventHandler(int techInt);

        public bool HasTech(Technology t) => _researched.Contains(t);

        public bool CanResearch(Technology t)
            => !_researched.Contains(t)
            && TechnologyData.GetStats(t).Prerequisites.All(HasTech);

        public System.Collections.Generic.IEnumerable<Technology> GetAvailableTechs()
            => System.Enum.GetValues<Technology>().Where(CanResearch);

        public void AccumulateScience(int amount)
        {
            ScienceLastDelta = amount;
            ScienceStored   += amount;
            EmitSignal(SignalName.ScienceChanged, ScienceStored, ScienceLastDelta);
        }

        public void SetResearch(Technology tech)
        {
            if (CanResearch(tech)) CurrentResearch = tech;
        }

        /// <summary>Devuelve true si se completó una tecnología este turno.</summary>
        public bool ProcessResearch()
        {
            if (CurrentResearch == null) return false;
            int cost = TechnologyData.GetStats(CurrentResearch.Value).ResearchCost;
            if (ScienceStored < cost) return false;
            ScienceStored -= cost;
            _researched.Add(CurrentResearch.Value);
            var finished = CurrentResearch.Value;
            CurrentResearch = null;
            EmitSignal(SignalName.TechResearched, (int)finished);
            return true;
        }

        // Se populan desde Main al iniciar
        public MapManager Map { get; set; } = null!;

        // ================================================================
        //  CARGA DESDE GUARDADO
        // ================================================================

        /// <summary>
        /// Restaura el estado global de juego desde datos de guardado.
        /// Debe llamarse ANTES de que UnitManager y CityManager restauren sus entidades,
        /// para que las señales de cambio de recursos estén correctamente conectadas al HUD.
        /// </summary>
        public void LoadFrom(GameSaveData data)
        {
            CurrentTurn   = data.Turn;
            Gold          = data.Gold;
            GoldLastDelta = 0;
            ScienceStored  = data.Science;
            ScienceLastDelta = 0;

            _researched.Clear();
            foreach (int t in data.ResearchedTechs)
                _researched.Add((Technology)t);

            CurrentResearch = data.CurrentResearch >= 0
                ? (Technology?)data.CurrentResearch
                : null;

            // Notificar al HUD de los valores iniciales
            EmitSignal(SignalName.TurnChanged,     CurrentTurn);
            EmitSignal(SignalName.GoldChanged,     Gold,         0);
            EmitSignal(SignalName.ScienceChanged,  ScienceStored, 0);
        }

        public override void _Ready()
        {
            Instance = this;
            // Forzar foco al iniciar para que WASD y clicks funcionen de inmediato
            GetWindow().GrabFocus();
        }

        public void EndTurn()
        {
            CurrentCivilizationIndex++;
            // TODO: cuando haya N civilizaciones, ciclar correctamente
            CurrentTurn++;
            GD.Print($"Turno {CurrentTurn}");
            EmitSignal(SignalName.TurnChanged, CurrentTurn);
        }

        [Signal]
        public delegate void TurnChangedEventHandler(int turn);
    }
}
