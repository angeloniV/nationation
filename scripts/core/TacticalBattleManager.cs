using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Natiolation.Map;
using Natiolation.Units;

namespace Natiolation.Core
{
    /// <summary>
    /// Singleton Node3D que gestiona el ciclo completo de un combate táctico.
    ///
    /// Ciclo:
    ///   1. UnitManager detecta ataque → llama StartBattle(...)
    ///   2. Se emite BattleStarted — cámara y UI reaccionan
    ///   3. Turnos alternan entre bandos (jugador vs IA)
    ///   4. Cuando un bando es derrotado → EndBattle() → BattleEnded
    ///   5. UnitManager escucha BattleEnded para limpiar el mapa
    /// </summary>
    public partial class TacticalBattleManager : Node3D
    {
        // ── Singleton ────────────────────────────────────────────────────────
        public static TacticalBattleManager? Instance { get; private set; }

        // ── Eventos estáticos (desacoplan UI, cámara, etc.) ─────────────────
        public static event Action<TacticalBattleManager>? BattleStarted;
        public static event Action<bool>?                  BattleEnded;   // true = atacantes ganaron

        // ── Estado público ───────────────────────────────────────────────────
        public bool IsActive { get; private set; }

        public HexCoord ConflictHex { get; private set; } = null!;

        /// <summary>Hex actualmente seleccionado en el arena (unidad del jugador).</summary>
        public TacticalUnit? SelectedUnit { get; private set; }

        // ── Datos de batalla ─────────────────────────────────────────────────
        private readonly List<TacticalUnit> _attackers = new();
        private readonly List<TacticalUnit> _defenders = new();
        private readonly HashSet<HexCoord>  _arena     = new();

        // Mapa rápido hex → unidad táctica (para ambos bandos)
        private readonly Dictionary<HexCoord, TacticalUnit> _byHex = new();

        // Referencia a los Army originales (para restaurar tras la batalla)
        private Army? _atkArmy;
        private Army? _defArmy;

        // Turno: true = turno de atacantes, false = turno de defensores
        private bool _attackersTurn = true;

        // Nodos 3D instanciados en el arena (para limpiar al terminar)
        private readonly List<Node3D> _arenaNodes = new();

        // Referencia al mapa para alturas
        private MapManager? _map;

        // Flag para evitar doble procesamiento
        private bool _processingAI = false;

        // ── Constantes ───────────────────────────────────────────────────────
        private const float ArenaY      = 0.55f;  // HexTile3D.TokenHover
        private const int   ArenaRadius = 2;       // hexes de radio alrededor del conflicto

        // ================================================================
        //  GODOT
        // ================================================================

        public override void _Ready()
        {
            if (Instance != null && Instance != this)
            {
                QueueFree();
                return;
            }
            Instance = this;
        }

        public override void _ExitTree()
        {
            if (Instance == this) Instance = null;
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            if (!IsActive) return;
            if (_attackersTurn && !_processingAI && @event is InputEventMouseButton mb && mb.Pressed)
            {
                var hex = ScreenToHex(mb.Position);
                if (hex == null) return;

                switch (mb.ButtonIndex)
                {
                    case MouseButton.Left:
                        HandleTacticalLeftClick(hex);
                        GetViewport().SetInputAsHandled();
                        break;
                    case MouseButton.Right:
                        HandleTacticalRightClick(hex);
                        GetViewport().SetInputAsHandled();
                        break;
                }
            }
        }

        // ================================================================
        //  INICIO DE BATALLA
        // ================================================================

        /// <summary>
        /// Inicia un combate táctico.
        /// Llamado por UnitManager cuando se detecta un ataque.
        /// </summary>
        public void StartBattle(
            List<Unit>  attackers,
            List<Unit>  defenders,
            HexCoord    conflictHex,
            MapManager  map,
            Army?       atkArmy = null,
            Army?       defArmy = null)
        {
            if (IsActive) return;
            if (attackers.Count == 0 || defenders.Count == 0) return;

            IsActive     = true;
            ConflictHex  = conflictHex;
            _map         = map;
            _atkArmy     = atkArmy;
            _defArmy     = defArmy;
            _attackersTurn = true;

            _attackers.Clear();
            _defenders.Clear();
            _arena.Clear();
            _byHex.Clear();
            _arenaNodes.Clear();
            SelectedUnit = null;
            _processingAI = false;

            // ── Construir arena ──────────────────────────────────────────
            BuildArena(conflictHex, map);

            // ── Ocultar banners de ejércitos ─────────────────────────────
            if (atkArmy != null) atkArmy.Visible = false;
            if (defArmy != null) defArmy.Visible = false;

            // ── Desplegar unidades en el arena ───────────────────────────
            DeployUnits(attackers, defenders);

            // ── Notificar sistemas externos ──────────────────────────────
            BattleStarted?.Invoke(this);
        }

        private void BuildArena(HexCoord center, MapManager map)
        {
            // Ring de radio ArenaRadius alrededor del hex de conflicto
            for (int dq = -ArenaRadius; dq <= ArenaRadius; dq++)
            {
                for (int dr = -ArenaRadius; dr <= ArenaRadius; dr++)
                {
                    int ds = -dq - dr;
                    if (Mathf.Abs(ds) > ArenaRadius) continue;
                    int q = center.Q + dq;
                    int r = center.R + dr;
                    var tileType = map.GetTileType(q, r);
                    if (tileType == null) continue;
                    if (!IsArenaPassable(tileType.Value)) continue;
                    _arena.Add(new HexCoord(q, r));
                }
            }
        }

        private static bool IsArenaPassable(TileType type) => type switch
        {
            TileType.Ocean    => false,
            TileType.Mountains => false,
            _                  => true,
        };

        private void DeployUnits(List<Unit> attackers, List<Unit> defenders)
        {
            // Separar hexes del arena en lado atacante (q < conflicto) y defensor (q >= conflicto)
            var arenaList = _arena.OrderBy(h => HexDist(h, ConflictHex)).ToList();

            // Atacantes: hexes con q menor (lado izquierdo del conflicto)
            var atkHexes = arenaList
                .Where(h => h != ConflictHex)
                .OrderBy(h => h.Q)
                .ThenBy(h => Math.Abs(h.R - ConflictHex.R))
                .ToList();

            // Defensores: hexes con q mayor o igual (lado derecho, incluyendo conflicto)
            var defHexes = arenaList
                .Where(h => h != ConflictHex)
                .OrderByDescending(h => h.Q)
                .ThenBy(h => Math.Abs(h.R - ConflictHex.R))
                .ToList();

            // El hex de conflicto va al primer defensor
            defHexes.Insert(0, ConflictHex);

            for (int i = 0; i < attackers.Count && i < atkHexes.Count; i++)
            {
                var tu = new TacticalUnit(attackers[i], atkHexes[i], isAttacker: true);
                _attackers.Add(tu);
                _byHex[atkHexes[i]] = tu;
                SpawnTacticalMarker(tu, new Color(0.25f, 0.55f, 1.0f));  // azul = atacante
            }

            for (int i = 0; i < defenders.Count && i < defHexes.Count; i++)
            {
                var tu = new TacticalUnit(defenders[i], defHexes[i], isAttacker: false);
                _defenders.Add(tu);
                _byHex[defHexes[i]] = tu;
                SpawnTacticalMarker(tu, new Color(1.0f, 0.30f, 0.25f));  // rojo = defensor
            }
        }

        private void SpawnTacticalMarker(TacticalUnit tu, Color bandColor)
        {
            if (_map == null) return;
            var hex = tu.Pos;
            float h = _map.GetTileHeight(hex.Q, hex.R);
            var worldPos = HexTile3D.AxialToWorld(hex.Q, hex.R)
                         + new Vector3(0f, h + ArenaY, 0f);

            // Usar el modelo real de la unidad (visible) en posición táctica
            tu.Unit.Visible = true;
            tu.Unit.Position = worldPos;

            // Banda de color encima (pequeño disco flotante)
            var bandMat = new StandardMaterial3D
            {
                AlbedoColor  = bandColor,
                ShadingMode  = BaseMaterial3D.ShadingModeEnum.Unshaded,
            };
            var band = new MeshInstance3D
            {
                Mesh             = new CylinderMesh { TopRadius = 0.44f, BottomRadius = 0.44f,
                                                      Height = 0.08f, RadialSegments = 12 },
                MaterialOverride = bandMat,
                Position         = worldPos + new Vector3(0f, 0.80f, 0f),
            };
            AddChild(band);
            _arenaNodes.Add(band);
            tu.BandNode = band;
        }

        // ================================================================
        //  INPUT TÁCTICO
        // ================================================================

        private void HandleTacticalLeftClick(HexCoord hex)
        {
            // ¿Hay una unidad atacante (bando del jugador) en este hex?
            if (_byHex.TryGetValue(hex, out var tu) && tu.IsAttacker && !tu.IsDone)
            {
                SelectTacticalUnit(tu);
                return;
            }

            // ¿Hay una unidad defensora y tenemos seleccionada una atacante?
            if (SelectedUnit != null && _byHex.TryGetValue(hex, out var enemy) && !enemy.IsAttacker)
            {
                var attackable = GetAttackableHexes(SelectedUnit);
                if (attackable.Contains(hex))
                {
                    _ = ExecuteAttackAsync(SelectedUnit, enemy);
                }
                return;
            }

            // ¿Hex vacío en el area de movimiento?
            if (SelectedUnit != null && !_byHex.ContainsKey(hex))
            {
                var reachable = GetReachableHexes(SelectedUnit);
                if (reachable.Contains(hex))
                {
                    _ = ExecuteMoveAsync(SelectedUnit, hex);
                }
            }
        }

        private void HandleTacticalRightClick(HexCoord hex)
        {
            // Deseleccionar
            SelectTacticalUnit(null);
        }

        private void SelectTacticalUnit(TacticalUnit? tu)
        {
            SelectedUnit = tu;
            UnitSelected?.Invoke(tu);
        }

        // ── Evento interno para que TacticalHUD actualice la UI ──────────────
        public event Action<TacticalUnit?>? UnitSelected;
        public event Action?                TurnChanged;
        public event Action<TacticalUnit, int>? UnitDamaged;  // unidad, daño recibido

        // ================================================================
        //  MOVIMIENTO TÁCTICO
        // ================================================================

        private async Task ExecuteMoveAsync(TacticalUnit tu, HexCoord dest)
        {
            if (tu.MovRemaining <= 0) return;
            if (_map == null) return;

            var occupied = GetOccupiedHexes(excludeUnit: tu);
            var path = TacticalPathfinder.FindPath(tu.Pos, dest, _arena, occupied);
            if (path == null || path.Count < 2) return;

            // Calcular pasos consumidos
            int steps = path.Count - 1;
            if (steps > tu.MovRemaining) return;

            // Animar movimiento hex a hex
            _byHex.Remove(tu.Pos);
            for (int i = 1; i < path.Count; i++)
            {
                tu.Pos = path[i];
                float h = _map.GetTileHeight(tu.Pos.Q, tu.Pos.R);
                var dest3d = HexTile3D.AxialToWorld(tu.Pos.Q, tu.Pos.R)
                           + new Vector3(0f, h + ArenaY, 0f);

                var tween = CreateTween();
                tween.SetEase(Tween.EaseType.InOut);
                tween.SetTrans(Tween.TransitionType.Sine);
                tween.TweenProperty(tu.Unit, "position", dest3d, 0.20f);
                if (tu.BandNode != null)
                    tween.Parallel().TweenProperty(tu.BandNode, "position",
                        dest3d + new Vector3(0f, 0.80f, 0f), 0.20f);
                await ToSignal(tween, Tween.SignalName.Finished);
            }
            _byHex[tu.Pos] = tu;
            tu.MovRemaining -= steps;

            // Si no le quedan movimientos ni ataques, marcar como terminada
            if (tu.MovRemaining <= 0 && tu.HasAttacked) tu.IsDone = true;
            SelectTacticalUnit(SelectedUnit);  // refrescar UI
        }

        // ================================================================
        //  ATAQUE TÁCTICO
        // ================================================================

        private async Task ExecuteAttackAsync(TacticalUnit attacker, TacticalUnit defender)
        {
            if (attacker.HasAttacked) return;

            var aStats = UnitTypeData.GetStats(attacker.Unit.UnitType);
            var dStats = UnitTypeData.GetStats(defender.Unit.UnitType);

            float atkPow  = Mathf.Max(1f, aStats.CombatStrength + (attacker.Unit.IsVeteran ? 2 : 0));
            float defPow  = Mathf.Max(1f, dStats.CombatStrength);

            float atkRoll = (float)GD.RandRange(0.8, 1.2) * atkPow;
            float defRoll = (float)GD.RandRange(0.8, 1.2) * defPow;

            int defDamage = Mathf.Clamp((int)(30f * atkRoll / defRoll), 5, 90);
            int atkDamage = Mathf.Clamp((int)(18f * defRoll / atkRoll), 2, 60);

            // Animación: sacudida del atacante hacia el defensor
            if (_map != null)
            {
                float h = _map.GetTileHeight(attacker.Pos.Q, attacker.Pos.R);
                var origin = HexTile3D.AxialToWorld(attacker.Pos.Q, attacker.Pos.R)
                           + new Vector3(0f, h + ArenaY, 0f);
                float hd = _map.GetTileHeight(defender.Pos.Q, defender.Pos.R);
                var target3d = HexTile3D.AxialToWorld(defender.Pos.Q, defender.Pos.R)
                             + new Vector3(0f, hd + ArenaY, 0f);
                var lunge = origin.Lerp(target3d, 0.4f);

                var tw = CreateTween();
                tw.SetEase(Tween.EaseType.Out);
                tw.SetTrans(Tween.TransitionType.Sine);
                tw.TweenProperty(attacker.Unit, "position", lunge, 0.10f);
                await ToSignal(tw, Tween.SignalName.Finished);
                var tw2 = CreateTween();
                tw2.TweenProperty(attacker.Unit, "position", origin, 0.12f);
                await ToSignal(tw2, Tween.SignalName.Finished);
            }

            // Aplicar daño
            defender.Unit.TakeDamage(defDamage);
            UnitDamaged?.Invoke(defender, defDamage);

            attacker.Unit.TakeDamage(atkDamage);
            UnitDamaged?.Invoke(attacker, atkDamage);

            attacker.HasAttacked  = true;
            attacker.MovRemaining = 0;
            attacker.IsDone       = true;

            SelectTacticalUnit(null);

            // Verificar muertes
            if (defender.Unit.CurrentHP <= 0) RemoveTacticalUnit(defender);
            if (attacker.Unit.CurrentHP <= 0) RemoveTacticalUnit(attacker);

            // ¿Terminó la batalla?
            CheckBattleEnd();
        }

        private void RemoveTacticalUnit(TacticalUnit tu)
        {
            _byHex.Remove(tu.Pos);
            if (tu.IsAttacker) _attackers.Remove(tu);
            else               _defenders.Remove(tu);

            // Ocultar el nodo de la unidad en el arena (no destruirlo — lo gestiona UnitManager)
            if (IsInstanceValid(tu.Unit)) tu.Unit.Visible = false;
            if (tu.BandNode != null && IsInstanceValid(tu.BandNode)) tu.BandNode.QueueFree();
        }

        private void CheckBattleEnd()
        {
            bool attackersAlive = _attackers.Count > 0;
            bool defendersAlive = _defenders.Count > 0;

            if (!attackersAlive || !defendersAlive)
                EndBattle(attackersWon: !defendersAlive);
        }

        // ================================================================
        //  FIN DE TURNO TÁCTICO
        // ================================================================

        /// <summary>Llamado por TacticalHUD cuando el jugador pulsa "Fin de Turno Táctico".</summary>
        public void EndPlayerTurn()
        {
            if (!IsActive || !_attackersTurn) return;
            // Resetear movimiento/ataque de atacantes para el próximo turno
            foreach (var tu in _attackers) { tu.IsDone = false; tu.MovRemaining = GetUnitMovement(tu); tu.HasAttacked = false; }
            _attackersTurn = false;
            TurnChanged?.Invoke();
            _ = ProcessAITurnAsync();
        }

        private int GetUnitMovement(TacticalUnit tu)
            => UnitTypeData.GetStats(tu.Unit.UnitType).MaxMovement;

        private async Task ProcessAITurnAsync()
        {
            _processingAI = true;
            await Task.Delay(600);  // pausa visual para que el jugador vea el turno IA

            foreach (var defender in _defenders.ToList())
            {
                if (!IsActive) break;
                if (!IsInstanceValid(defender.Unit)) continue;

                // IA simple: intentar atacar si hay atacante adyacente, sino avanzar
                var enemyHexes = new HashSet<HexCoord>(_attackers.Select(a => a.Pos));
                var attackable = TacticalPathfinder.GetAttackable(defender.Pos, _arena, enemyHexes);

                if (attackable.Count > 0)
                {
                    // Atacar al más débil
                    var target = attackable
                        .Select(h => _byHex.TryGetValue(h, out var t) ? t : null)
                        .Where(t => t != null)
                        .OrderBy(t => t!.Unit.CurrentHP)
                        .FirstOrDefault();

                    if (target != null)
                    {
                        await Task.Delay(300);
                        await ExecuteAttackAsync(defender, target);
                        if (!IsActive) break;
                        continue;
                    }
                }

                // Avanzar hacia el atacante más cercano
                if (_attackers.Count > 0)
                {
                    var occupied = GetOccupiedHexes(excludeUnit: defender);
                    var reachable = TacticalPathfinder.GetReachable(
                        defender.Pos, GetUnitMovement(defender), _arena, occupied);
                    var closestEnemy = _attackers.OrderBy(a => HexDist(a.Pos, defender.Pos)).First();
                    var best = reachable
                        .OrderBy(h => HexDist(h, closestEnemy.Pos))
                        .FirstOrDefault();
                    if (best != default)
                    {
                        await Task.Delay(250);
                        await ExecuteMoveAsync(defender, best);
                    }
                }
            }

            if (!IsActive) return;

            // Resetear defensores para próximo turno
            foreach (var tu in _defenders) { tu.IsDone = false; tu.MovRemaining = GetUnitMovement(tu); tu.HasAttacked = false; }

            _attackersTurn = true;
            // Resetear atacantes (jugador)
            foreach (var tu in _attackers) { tu.IsDone = false; tu.MovRemaining = GetUnitMovement(tu); tu.HasAttacked = false; }

            _processingAI = false;
            TurnChanged?.Invoke();
            SelectTacticalUnit(null);
        }

        // ================================================================
        //  FIN DE BATALLA
        // ================================================================

        private void EndBattle(bool attackersWon)
        {
            if (!IsActive) return;
            IsActive = false;

            // Restaurar banners de ejércitos
            if (_atkArmy != null && IsInstanceValid(_atkArmy)) _atkArmy.Visible = true;
            if (_defArmy != null && IsInstanceValid(_defArmy)) _defArmy.Visible = true;

            // Limpiar bandas de arena
            foreach (var node in _arenaNodes)
                if (IsInstanceValid(node)) node.QueueFree();
            _arenaNodes.Clear();

            // Restaurar posición lógica de sobrevivientes
            foreach (var tu in _attackers)
                if (IsInstanceValid(tu.Unit)) { tu.Unit.Visible = false; }  // vuelve al ejército
            foreach (var tu in _defenders)
                if (IsInstanceValid(tu.Unit)) { tu.Unit.Visible = false; }  // vuelve al ejército

            _attackers.Clear();
            _defenders.Clear();
            _byHex.Clear();
            _arena.Clear();
            SelectedUnit = null;

            BattleEnded?.Invoke(attackersWon);
        }

        // ================================================================
        //  HELPERS PÚBLICOS (para TacticalHUD)
        // ================================================================

        public IReadOnlyList<TacticalUnit> Attackers => _attackers;
        public IReadOnlyList<TacticalUnit> Defenders => _defenders;
        public bool IsAttackersTurn => _attackersTurn;

        public HashSet<HexCoord> GetReachableHexes(TacticalUnit tu)
        {
            if (tu.MovRemaining <= 0) return new HashSet<HexCoord>();
            var occupied = GetOccupiedHexes(excludeUnit: tu);
            return TacticalPathfinder.GetReachable(tu.Pos, tu.MovRemaining, _arena, occupied);
        }

        public HashSet<HexCoord> GetAttackableHexes(TacticalUnit tu)
        {
            var enemyHexes = new HashSet<HexCoord>(
                (tu.IsAttacker ? _defenders : _attackers).Select(e => e.Pos));
            return TacticalPathfinder.GetAttackable(tu.Pos, _arena, enemyHexes);
        }

        // ================================================================
        //  HELPERS INTERNOS
        // ================================================================

        private HashSet<HexCoord> GetOccupiedHexes(TacticalUnit? excludeUnit = null)
        {
            var result = new HashSet<HexCoord>();
            foreach (var kv in _byHex)
                if (kv.Value != excludeUnit) result.Add(kv.Key);
            return result;
        }

        private static int HexDist(HexCoord a, HexCoord b)
        {
            int dq = a.Q - b.Q, dr = a.R - b.R, ds = -dq - dr;
            return (Mathf.Abs(dq) + Mathf.Abs(dr) + Mathf.Abs(ds)) / 2;
        }

        private HexCoord? ScreenToHex(Vector2 screenPos)
        {
            var cam = GetViewport().GetCamera3D();
            if (cam == null) return null;
            var spaceState = GetWorld3D().DirectSpaceState;
            var from = cam.ProjectRayOrigin(screenPos);
            var to   = from + cam.ProjectRayNormal(screenPos) * 600f;
            var query  = PhysicsRayQueryParameters3D.Create(from, to);
            var result = spaceState.IntersectRay(query);
            if (result.Count == 0) return null;

            var collider = result["collider"].As<Node>();
            // Buscar HexTile3D en el árbol del collider
            while (collider != null)
            {
                if (collider is HexTile3D tile) return new HexCoord(tile.Q, tile.R);
                collider = collider.GetParent() as Node;
            }
            return null;
        }
    }

    // ====================================================================
    //  TacticalUnit — wrapper de unidad en el arena
    // ====================================================================

    public class TacticalUnit
    {
        public Unit       Unit        { get; }
        public HexCoord   Pos         { get; set; }
        public int        MovRemaining{ get; set; }
        public bool       HasAttacked { get; set; }
        public bool       IsAttacker  { get; }
        public bool       IsDone      { get; set; }

        /// <summary>Nodo 3D de la banda de color (disco de identificación).</summary>
        public Node3D?    BandNode    { get; set; }

        public TacticalUnit(Unit unit, HexCoord pos, bool isAttacker)
        {
            Unit         = unit;
            Pos          = pos;
            IsAttacker   = isAttacker;
            MovRemaining = UnitTypeData.GetStats(unit.UnitType).MaxMovement;
            HasAttacked  = false;
            IsDone       = false;
        }
    }
}
