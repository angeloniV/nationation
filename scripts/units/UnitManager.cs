using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Natiolation.Cities;
using Natiolation.Core;
using Natiolation.Map;

namespace Natiolation.Units
{
    /// <summary>
    /// Gestiona todas las unidades del jugador.
    ///
    /// Cambios vs versión 2D:
    ///   • ScreenToHex usa PhysicsRaycast 3D (StaticBody3D en cada tile).
    ///   • PlaceAt pasa la altura del tile para posicionamiento 3D correcto.
    ///   • La cámara se centra en el spawn inicial del jugador.
    /// </summary>
    public partial class UnitManager : Node3D
    {
        [Signal] public delegate void CombatEventEventHandler(string message);
        [Signal] public delegate void ResearchRequiredEventHandler();
        [Signal] public delegate void OpenTechPickerEventHandler();
        [Signal] public delegate void UnitSelectedEventHandler(string name, Color civColor, int remaining, int max, int unitTypeInt, int q, int r, bool isFortified);
        [Signal] public delegate void UnitDeselectedEventHandler();
        [Signal] public delegate void TileHoveredEventHandler(string tileName, int food, int prod, int moveCost);
        [Signal] public delegate void CitySelectedEventHandler(int q, int r);
        [Signal] public delegate void CityDeselectedEventHandler();

        // ── C# Events para Army (evitan acoplamiento fuerte con la UI) ──────
        public event System.Action<Army>? ArmySelectedEvent;
        public event System.Action?       ArmyDeselectedEvent;

        private MapManager  _map         = null!;
        private MapOverlay  _overlay     = null!;
        private MapCamera   _camera      = null!;
        private CityManager _cityManager = null!;

        private readonly List<Unit>                 _units      = new();
        private readonly Dictionary<HexCoord, Unit> _byHex      = new();

        // ── Ejércitos ────────────────────────────────────────────────────────
        private readonly List<Army>                  _armies     = new();
        private readonly Dictionary<HexCoord, Army>  _armiesByHex = new();

        /// <summary>Acceso de solo lectura a todas las unidades (usado por el minimapa).</summary>
        public System.Collections.Generic.IReadOnlyList<Unit>  AllUnits  => _units;
        public System.Collections.Generic.IReadOnlyList<Army>  AllArmies => _armies;

        private Unit?             _selected;
        private Army?             _selectedArmy;
        private HexCoord?         _hovered;
        private HashSet<HexCoord> _reachable = new();

        // Evita que clics rápidos lancen múltiples pathfindings en paralelo
        private bool _pathfindingInProgress;

        // ── Estado de fusión / despliegue (Fase 4) ───────────────────────────
        private ConfirmationDialog _mergeDialog     = null!;
        private Unit?  _pendingMergeA    = null;   // primera unidad a fusionar
        private Unit?  _pendingMergeB    = null;   // segunda unidad a fusionar (null = join to army)
        private Army?  _pendingJoinArmy  = null;   // ejército al que se une _pendingMergeA
        private Unit?  _pendingDeployUnit = null;  // unidad seleccionada para desplegarse del ejército

        // ================================================================

        public override void _Ready()
        {
            _map         = GetNode<MapManager>  ("/root/Main/MapManager");
            _overlay     = GetNode<MapOverlay>  ("/root/Main/MapOverlay");
            _camera      = GetNode<MapCamera>   ("/root/Main/MapCamera");
            _cityManager = GetNode<CityManager> ("/root/Main/CityManager");

            _cityManager.UnitProductionComplete += OnUnitProductionComplete;
            GameManager.Instance.WarDeclared    += OnWarDeclared;
            TacticalBattleManager.BattleEnded   += OnTacticalBattleEnded;

            // ── ConfirmationDialog para fusionar unidades ─────────────────
            var cl = new CanvasLayer { Layer = 25 };
            AddChild(cl);
            _mergeDialog = new ConfirmationDialog
            {
                Title           = "Fusionar en Ejército",
                DialogText      = "¿Deseas fusionar estas unidades en un Ejército?",
                OkButtonText    = "Fusionar",
                CancelButtonText= "Cancelar",
                InitialPosition = Window.WindowInitialPosition.CenterMainWindowScreen,
            };
            cl.AddChild(_mergeDialog);
            _mergeDialog.Confirmed += OnMergeConfirmed;
            _mergeDialog.Canceled  += OnMergeCanceled;

            // Si hay datos de guardado pendientes, SaveManager.ApplyPendingLoad()
            // llamará a LoadFromSave() en el siguiente frame (vía CallDeferred).
            if (Core.GameSettings.Instance?.PendingLoad == null)
                SpawnStartingUnits();
        }

        // ================================================================
        //  SPAWN
        // ================================================================

        private void SpawnStartingUnits()
        {
            var p1 = FindStartPos(14, 18);
            Spawn(UnitType.Settler, p1.Q,     p1.R,     new Color(0.18f, 0.42f, 0.95f), 0);
            Spawn(UnitType.Warrior, p1.Q + 1, p1.R,     new Color(0.18f, 0.42f, 0.95f), 0);
            Spawn(UnitType.Scout,   p1.Q,     p1.R + 1, new Color(0.18f, 0.42f, 0.95f), 0);

            var p2 = FindStartPos(42, 18);
            Spawn(UnitType.Settler, p2.Q,     p2.R,     new Color(0.90f, 0.22f, 0.18f), 1);
            Spawn(UnitType.Warrior, p2.Q + 1, p2.R,     new Color(0.90f, 0.22f, 0.18f), 1);

            // Centrar la cámara en el spawn del jugador 1
            _camera.FocusOn(HexTile3D.AxialToWorld(p1.Q, p1.R));
        }

        private HexCoord FindStartPos(int hintQ, int hintR)
        {
            for (int radius = 0; radius <= 12; radius++)
                for (int dq = -radius; dq <= radius; dq++)
                    for (int dr = -radius; dr <= radius; dr++)
                    {
                        int q = hintQ + dq, r = hintR + dr;
                        var t = _map.GetTileType(q, r);
                        if (t != null && Pathfinder.IsPassable(t.Value)
                                      && !_byHex.ContainsKey(new HexCoord(q, r)))
                            return new HexCoord(q, r);
                    }
            return new HexCoord(hintQ, hintR);
        }

        // ================================================================
        //  CARGA DESDE GUARDADO
        // ================================================================

        /// <summary>
        /// Restaura todas las unidades desde datos de guardado.
        /// Llamado por SaveManager.ApplyPendingLoad() tras todos los _Ready().
        /// </summary>
        public void LoadFromSave(Core.UnitSaveData[] savedUnits)
        {
            foreach (var data in savedUnits)
                SpawnFromSave(data);
        }

        private void SpawnFromSave(Core.UnitSaveData data)
        {
            var t = _map.GetTileType(data.Q, data.R);
            if (t == null || !Pathfinder.IsPassable(t.Value))
            {
                GD.PrintErr($"[UnitManager] No se pudo restaurar unidad en ({data.Q},{data.R}): tile inválido.");
                return;
            }

            var civColor = new Color(data.CivR, data.CivG, data.CivB);
            var unit = new Unit
            {
                UnitType = (UnitType)data.UnitType,
                CivColor  = civColor,
                CivIndex  = data.CivIndex,
            };
            AddChild(unit);
            ApplyMaterialOverride(unit, civColor);
            unit.PlaceAt(data.Q, data.R, _map.GetTileHeight(data.Q, data.R));
            unit.RestoreFromSave(data.MovesLeft, data.CurrentHP, data.IsVeteran, data.IsFortified);
            _units.Add(unit);
            _byHex[new HexCoord(data.Q, data.R)] = unit;
        }

        // ================================================================
        //  SPAWN (nueva partida)
        // ================================================================

        // ================================================================
        //  MATERIAL OVERRIDE — GLB vertex colors
        // ================================================================

        /// <summary>
        /// Recorre recursivamente todos los MeshInstance3D hijos de <paramref name="instance"/>
        /// y les aplica un StandardMaterial3D con VertexColorUseAsAlbedo = true como MaterialOverride,
        /// de modo que los colores de vértice del .glb se usen como albedo base.
        /// </summary>
        private static void ApplyMaterialOverride(Node3D instance, Color color)
        {
            foreach (var child in instance.GetChildren())
            {
                if (child is MeshInstance3D mi)
                {
                    mi.MaterialOverride = new StandardMaterial3D
                    {
                        VertexColorUseAsAlbedo = true,
                        AlbedoColor            = color,
                    };
                }
                if (child is Node3D n3d)
                    ApplyMaterialOverride(n3d, color);
            }
        }

        private void Spawn(UnitType type, int q, int r, Color civColor, int civIndex)
        {
            var t = _map.GetTileType(q, r);
            if (t == null || !Pathfinder.IsPassable(t.Value)) return;

            var unit = new Unit { UnitType=type, CivColor=civColor, CivIndex=civIndex };
            AddChild(unit);
            ApplyMaterialOverride(unit, civColor);
            unit.PlaceAt(q, r, _map.GetTileHeight(q, r));
            _units.Add(unit);
            _byHex[new HexCoord(q, r)] = unit;

            // Recalcular visibilidad considerando todas las unidades civ 0
            if (civIndex == 0)
                RefreshFog();
        }

        // ================================================================
        //  INPUT
        // ================================================================

        public override void _UnhandledInput(InputEvent @event)
        {
            // Durante combate táctico, el TacticalBattleManager maneja el input
            if (TacticalBattleManager.Instance?.IsActive == true) return;

            if (@event is InputEventMouseMotion mm)
            {
                var newHex = ScreenToHex(mm.Position);
                if (newHex != _hovered)
                {
                    _hovered = newHex;
                    _overlay.SetHovered(newHex);
                    RefreshPathPreview();
                    EmitTileHovered(newHex);
                }
            }

            if (@event is InputEventMouseButton mb && mb.Pressed)
            {
                // Recalcular hex al momento exacto del click
                _hovered = ScreenToHex(mb.Position);
                _overlay.SetHovered(_hovered);
                EmitTileHovered(_hovered);

                switch (mb.ButtonIndex)
                {
                    case MouseButton.Left:  HandleLeftClick();  break;
                    case MouseButton.Right: HandleRightClick(); break;
                }
            }

            if (@event is InputEventKey key && key.Pressed && !key.Echo)
            {
                if (key.Keycode == Key.Enter)
                    EndTurn();
                if (key.Keycode == Key.Space)
                    CycleToNextUnit();
                if (key.Keycode == Key.B)
                    TryFoundCity();
                if (key.Keycode == Key.F)
                    TryFortify();
                if (key.Keycode == Key.I)
                    TryBuildImprovement(TileImprovement.Irrigation);
                if (key.Keycode == Key.R)
                    TryBuildImprovement(TileImprovement.Road);
                if (key.Keycode == Key.M)
                    TryBuildImprovement(TileImprovement.Mine);
                if (key.Keycode == Key.G)
                    TryBuildImprovement(TileImprovement.Farm);
                if (key.Keycode == Key.S)
                    TrySkipUnit();
                if (key.Keycode == Key.T)
                    EmitSignal(SignalName.OpenTechPicker);
                if (key.Keycode == Key.A)
                    TryAutoExplore();
            }
        }

        // ================================================================
        //  SELECCIÓN Y MOVIMIENTO
        // ================================================================

        private async void HandleLeftClick()
        {
            var hex = _hovered;
            if (hex == null) return;

            // 1a. Click en ejército propio
            if (_armiesByHex.TryGetValue(hex, out var clickedArmy) && clickedArmy.CivIndex == 0)
            {
                if (clickedArmy == _selectedArmy)
                {
                    DeselectArmy();
                }
                else
                {
                    Deselect();
                    SelectArmy(clickedArmy);
                }
                return;
            }

            // 1b. Click en unidad propia
            if (_byHex.TryGetValue(hex, out var clicked) && clicked.CivIndex == 0)
            {
                if (clicked == _selected)
                {
                    // Unidad ya seleccionada — si hay ciudad en el mismo tile, cambiar a ciudad
                    var cityHere = _cityManager.GetCityAt(hex.Q, hex.R);
                    if (cityHere != null && cityHere.CivIndex == 0)
                    {
                        Deselect();
                        EmitSignal(SignalName.CitySelected, hex.Q, hex.R);
                    }
                    else
                    {
                        Deselect();
                    }
                }
                else if (_selected != null
                         && _selected.CivIndex == 0
                         && HexDist(_selected.Q, _selected.R, clicked.Q, clicked.R) == 1
                         && IsMilitary(_selected.UnitType)
                         && IsMilitary(clicked.UnitType))
                {
                    // Unidad seleccionada + click en unidad aliada adyacente → fusionar
                    ShowMergeDialog(_selected, clicked);
                }
                else
                {
                    EmitSignal(SignalName.CityDeselected);
                    SelectUnit(clicked);
                }
                return;
            }

            // 2a. Mover o atacar con ejército seleccionado
            if (_selectedArmy != null && _reachable.Contains(hex))
            {
                // Unidad enemiga → combate táctico (ejército vs unidad)
                if (_byHex.TryGetValue(hex, out var enemyUnit) && enemyUnit.CivIndex != _selectedArmy.CivIndex)
                {
                    var atkUnits = _selectedArmy.Units.ToList();
                    var defUnits = new System.Collections.Generic.List<Unit> { enemyUnit };
                    var conflictHex = new HexCoord(hex.Q, hex.R);
                    TacticalBattleManager.Instance?.StartBattle(
                        atkUnits, defUnits, conflictHex, _map, _selectedArmy, null);
                    return;
                }
                // Ejército enemigo → combate táctico (ejército vs ejército)
                if (_armiesByHex.TryGetValue(hex, out var enemyArmy) && enemyArmy.CivIndex != _selectedArmy.CivIndex)
                {
                    var atkUnits = _selectedArmy.Units.ToList();
                    var defUnits = enemyArmy.Units.ToList();
                    var conflictHex = new HexCoord(hex.Q, hex.R);
                    TacticalBattleManager.Instance?.StartBattle(
                        atkUnits, defUnits, conflictHex, _map, _selectedArmy, enemyArmy);
                    return;
                }
                // Unidad aliada → ofrecer unirse al ejército
                if (_byHex.TryGetValue(hex, out var ally) && ally.CivIndex == _selectedArmy.CivIndex)
                {
                    ShowJoinArmyDialog(ally, _selectedArmy);
                    return;
                }
                // Hex vacío → mover ejército (pathfinding asíncrono)
                if (_pathfindingInProgress) return;
                _pathfindingInProgress = true;
                try
                {
                    var army = _selectedArmy;   // capturar referencia antes del await
                    var armyPath = await Pathfinder.FindPathAsync(
                        _map, army.Q, army.R,
                        hex.Q, hex.R, army.RemainingMovement,
                        GetArmyBlockedHexes(army));
                    if (armyPath != null) await IssueArmyMove(army, armyPath);
                }
                finally { _pathfindingInProgress = false; }
                return;
            }

            // 2b. Mover o atacar con unidad seleccionada
            if (_selected != null && _reachable.Contains(hex))
            {
                // Unidad enemiga → combate táctico (unidad vs unidad)
                if (_byHex.TryGetValue(hex, out var occupant) && occupant.CivIndex != _selected.CivIndex)
                {
                    if (UnitTypeData.GetStats(_selected.UnitType).CombatStrength > 0)
                    {
                        var atkUnits = new System.Collections.Generic.List<Unit> { _selected };
                        var defUnits = new System.Collections.Generic.List<Unit> { occupant };
                        var conflictHex = new HexCoord(hex.Q, hex.R);
                        TacticalBattleManager.Instance?.StartBattle(
                            atkUnits, defUnits, conflictHex, _map, null, null);
                    }
                    return;
                }
                // Ejército enemigo → combate táctico (unidad vs ejército)
                if (_armiesByHex.TryGetValue(hex, out var defArmy) && defArmy.CivIndex != _selected.CivIndex)
                {
                    var atkUnits = new System.Collections.Generic.List<Unit> { _selected };
                    var defUnits = defArmy.Units.ToList();
                    var conflictHex = new HexCoord(hex.Q, hex.R);
                    TacticalBattleManager.Instance?.StartBattle(
                        atkUnits, defUnits, conflictHex, _map, null, defArmy);
                    return;
                }
                // Ejército aliado → ofrecer unirse
                if (_armiesByHex.TryGetValue(hex, out var allyArmy) && allyArmy.CivIndex == _selected.CivIndex)
                {
                    ShowJoinArmyDialog(_selected, allyArmy);
                    return;
                }

                // Mover unidad (pathfinding asíncrono)
                if (_pathfindingInProgress) return;
                _pathfindingInProgress = true;
                try
                {
                    var unit = _selected;   // capturar referencia antes del await
                    var blocked1upt = IsMilitary(unit.UnitType)
                        ? GetFriendlyMilitaryHexes(unit)
                        : null;
                    var path = await Pathfinder.FindPathAsync(
                        _map, unit.Q, unit.R,
                        hex.Q, hex.R, unit.RemainingMovement, blocked1upt);
                    if (path != null) await IssueMove(unit, path);
                }
                finally { _pathfindingInProgress = false; }
                return;
            }

            // 3. Click en ciudad propia
            var city = _cityManager.GetCityAt(hex.Q, hex.R);
            if (city != null && city.CivIndex == 0)
            {
                if (_selected != null) Deselect();
                if (_selectedArmy != null) DeselectArmy();
                EmitSignal(SignalName.CitySelected, hex.Q, hex.R);
                return;
            }

            // 4. Click en vacío — deseleccionar todo
            Deselect();
            DeselectArmy();
        }

        private void HandleRightClick()
        {
            var hex = _hovered;

            // Ejército seleccionado + unidad de despliegue pendiente + hex adyacente vacío → desplegar
            if (_selectedArmy != null && _pendingDeployUnit != null && hex != null)
            {
                if (IsAdjacent(_selectedArmy.Q, _selectedArmy.R, hex.Q, hex.R))
                {
                    var t = _map.GetTileType(hex.Q, hex.R);
                    if (t != null && Pathfinder.IsPassable(t.Value)
                                  && !_byHex.ContainsKey(hex)
                                  && !_armiesByHex.ContainsKey(hex))
                    {
                        DeployUnitFromArmy(_selectedArmy, _pendingDeployUnit, hex.Q, hex.R);
                        _pendingDeployUnit = null;
                        return;
                    }
                }
                // Click fuera del rango → cancelar despliegue
                _pendingDeployUnit = null;
                EmitSignal(SignalName.CombatEvent, "✗  Despliegue cancelado");
                return;
            }

            // Unidad seleccionada + hex válido → fijar destino (waypoint)
            if (_selected != null && hex != null)
            {
                var t = _map.GetTileType(hex.Q, hex.R);
                if (t != null && Pathfinder.IsPassable(t.Value)
                    && !(hex.Q == _selected.Q && hex.R == _selected.R))
                {
                    _selected.SetWaypoint(hex.Q, hex.R);
                    var previewPath = Pathfinder.FindPath(
                        _map, _selected.Q, _selected.R,
                        hex.Q, hex.R, float.MaxValue);
                    _overlay.SetPath(previewPath ?? new List<HexCoord>());
                    EmitUnitSelected(_selected);
                    EmitSignal(SignalName.CombatEvent, $"→  Destino fijado en ({hex.Q},{hex.R})");
                    return;
                }
            }

            // Sin selección → deseleccionar todo
            Deselect();
            DeselectArmy();
        }

        private void SelectUnit(Unit unit)
        {
            GD.Print($"[UnitManager] SelectUnit: {unit.UnitType} civ={unit.CivIndex}");
            if (_selected != null) _selected.Select(false);
            _selected = unit;
            unit.Select(true);

            // 1 UPT: calcular tiles bloqueados para unidades militares
            var blocked = IsMilitary(unit.UnitType)
                ? GetFriendlyMilitaryHexes(unit)
                : null;

            _reachable = Pathfinder.GetReachable(_map, unit.Q, unit.R, unit.RemainingMovement, blocked);
            _overlay.SetReachable(_reachable);
            _overlay.SetHovered(_hovered);
            _overlay.SetSelectedUnit(new HexCoord(unit.Q, unit.R));
            RefreshPathPreview();
            EmitUnitSelected(unit);
        }

        private void Deselect()
        {
            // Siempre limpiar la ciudad seleccionada (el HUD comprueba si estaba visible)
            EmitSignal(SignalName.CityDeselected);

            if (_selected == null) return;
            _selected.Select(false);
            _selected = null;
            _reachable.Clear();
            _overlay.SetReachable(_reachable);
            _overlay.SetPath(new List<HexCoord>());
            _overlay.SetSelectedUnit(null);
            EmitSignal(SignalName.UnitDeselected);
        }

        private void RefreshPathPreview()
        {
            if (_selected == null || _hovered == null || !_reachable.Contains(_hovered))
            {
                _overlay.SetPath(new List<HexCoord>());
                return;
            }
            var path = Pathfinder.FindPath(
                _map, _selected.Q, _selected.R,
                _hovered.Q, _hovered.R, _selected.RemainingMovement);
            _overlay.SetPath(path ?? new List<HexCoord>());
        }

        private async Task IssueMove(Unit unit, List<HexCoord> path)
        {
            _byHex.Remove(new HexCoord(unit.Q, unit.R));
            _overlay.ClearAll();
            _overlay.SetHovered(_hovered);

            await unit.MoveTo(path, _map);

            _byHex[new HexCoord(unit.Q, unit.R)] = unit;

            // Revelar entorno tras el movimiento (todas las unidades civ 0 contribuyen)
            if (unit.CivIndex == 0)
                RefreshFog();

            if (_selected == unit)
            {
                var blocked = IsMilitary(unit.UnitType) ? GetFriendlyMilitaryHexes(unit) : null;
                _reachable = Pathfinder.GetReachable(_map, unit.Q, unit.R, unit.RemainingMovement, blocked);
                _overlay.SetReachable(_reachable);
                _overlay.SetHovered(_hovered);
                _overlay.MoveSelectedUnit(new HexCoord(unit.Q, unit.R));
                RefreshPathPreview();
                EmitUnitSelected(unit);
            }
        }

        // ================================================================
        //  FOG OF WAR
        // ================================================================

        /// <summary>
        /// Recalcula el fog of war acumulando la visión de todas las unidades del jugador (civ 0).
        /// Public para que SaveManager pueda llamarlo tras restaurar todas las entidades.
        /// </summary>
        public void RefreshFog()
        {
            var observers = new List<(int q, int r, int sight)>();

            // Unidades del jugador
            foreach (var unit in _units)
                if (unit.CivIndex == 0)
                    observers.Add((unit.Q, unit.R, UnitTypeData.GetStats(unit.UnitType).SightRange));

            // Ciudades del jugador (visión permanente aunque no haya unidades)
            foreach (var obs in _cityManager.GetObservers(0))
                observers.Add(obs);

            _map.RefreshVisibility(observers);

            // Ocultar unidades y ejércitos enemigos que estén en la niebla
            foreach (var unit in _units)
                unit.Visible = unit.CivIndex == 0 || (_map.GetTile(unit.Q, unit.R)?.TileVisible ?? false);

            foreach (var army in _armies)
                army.Visible = army.CivIndex == 0 || (_map.GetTile(army.Q, army.R)?.TileVisible ?? false);

            // Ocultar ciudades enemigas que estén en la niebla
            _cityManager.RefreshFogVisibility(_map);
        }

        // ================================================================
        //  TURNOS
        // ================================================================

        public void EndTurn()
        {
            // ── 1. Verificar unidades sin órdenes (incluyendo ejércitos) ──
            var pending = _units
                .Where(u => u.CivIndex == 0 && !u.IsReadyForTurn)
                .ToList();
            var pendingArmies = _armies
                .Where(a => a.CivIndex == 0 && !a.IsReadyForTurn)
                .ToList();
            if (pending.Count > 0)
            {
                SelectUnit(pending[0]);
                _camera.FocusOn(HexTile3D.AxialToWorld(pending[0].Q, pending[0].R));
                EmitSignal(SignalName.CombatEvent,
                    $"⚠  {pending.Count} unidad(es) sin órdenes — usa [S] para saltear");
                return;
            }

            // ── 2. Verificar investigación activa ─────────────────────────
            var gm = GameManager.Instance;
            if (gm.CurrentResearch == null && gm.GetAvailableTechs().Any())
            {
                EmitSignal(SignalName.ResearchRequired);
                EmitSignal(SignalName.CombatEvent, "🔬  Elige una tecnología para investigar");
                return;
            }

            // ── 3. Verificar ciudades sin producción ───────────────────────
            foreach (var city in _cityManager.AllCities)
            {
                if (city.CivIndex != 0) continue;
                if (!city.BuildingUnit.HasValue && !city.BuildingBuilding.HasValue)
                {
                    Deselect();
                    _camera.FocusOn(HexTile3D.AxialToWorld(city.Q, city.R));
                    EmitSignal(SignalName.CitySelected, city.Q, city.R);
                    EmitSignal(SignalName.CombatEvent, $"⚒  {city.CityName} necesita producir algo");
                    return;
                }
            }

            Deselect();
            DeselectArmy();
            foreach (var unit in _units)
                unit.ResetMovement();
            foreach (var army in _armies)
                army.ResetMovement();

            // ── Mover unidades del jugador con waypoint ───────────────────
            ProcessWaypointMovements();

            // ── Economía: ingresos − mantenimiento de unidades y edificios ──
            int income   = _cityManager.GetTotalGoldPerTurn(0);
            int upkeep   = 0;
            foreach (var unit in _units)
                if (unit.CivIndex == 0) upkeep++;
            int bldMaint = _cityManager.GetBuildingMaintenanceCost(0);
            gm.ApplyGoldDelta(income - upkeep - bldMaint);

            // ── Ciencia ───────────────────────────────────────────────────
            int sci = _cityManager.GetTotalSciencePerTurn(0);
            gm.AccumulateScience(sci);
            gm.ProcessResearch();   // señal TechResearched emitida si completa

            _cityManager.ProcessTurn();
            gm.EndTurn();

            // Turno de las civilizaciones IA
            ProcessAITurn();
        }

        /// <summary>
        /// Mueve automáticamente las unidades del jugador que tienen un waypoint fijado.
        /// Se ejecuta al inicio de cada turno, antes del procesamiento de economía.
        /// </summary>
        private void ProcessWaypointMovements()
        {
            foreach (var unit in _units.ToList())
            {
                if (unit.CivIndex != 0) continue;
                if (!IsInstanceValid(unit)) continue;

                // Auto-exploración: procesar antes que el waypoint
                if (unit.IsAutoExploring && !unit.HasWaypoint)
                {
                    ProcessAutoExploreUnit(unit);
                    continue;
                }

                if (!unit.HasWaypoint) continue;

                int wq = unit.WaypointQ!.Value;
                int wr = unit.WaypointR!.Value;

                // Comprobar si ya llegó
                if (unit.Q == wq && unit.R == wr)
                {
                    unit.ClearWaypoint();
                    continue;
                }

                // 1 UPT: calcular tiles bloqueados para unidades militares
                var waypointBlocked = IsMilitary(unit.UnitType)
                    ? GetFriendlyMilitaryHexes(unit)
                    : null;

                // Encontrar el mejor camino con el movimiento disponible
                var path = Pathfinder.FindPath(
                    _map, unit.Q, unit.R, wq, wr, unit.RemainingMovement, waypointBlocked);

                if (path == null || path.Count < 2)
                {
                    // No hay camino alcanzable este turno: avanzar lo máximo posible
                    // usando camino sin límite de movimiento y tomando los pasos que quepan
                    path = Pathfinder.FindPath(_map, unit.Q, unit.R, wq, wr, float.MaxValue, waypointBlocked);
                    if (path == null || path.Count < 2) { unit.ClearWaypoint(); continue; }
                }

                // Mover paso a paso (instante, sin animación)
                // 1 UPT: detener si el próximo paso está ocupado por unidad militar amiga
                _byHex.Remove(new HexCoord(unit.Q, unit.R));
                float movLeft = unit.RemainingMovement;
                for (int i = 1; i < path.Count; i++)
                {
                    var   step = path[i];
                    float cost = _map.GetEffectiveCost(step.Q, step.R);
                    if (movLeft < cost) break;

                    // 1 UPT: verificar ocupación en tiempo real (otro waypoint pudo mover antes)
                    if (waypointBlocked != null && _byHex.ContainsKey(step)) break;

                    movLeft -= cost;
                    unit.ConsumeMovement(cost);
                    unit.PlaceAt(step.Q, step.R, _map.GetTileHeight(step.Q, step.R));
                }
                _byHex[new HexCoord(unit.Q, unit.R)] = unit;

                // Llegó al destino?
                if (unit.Q == wq && unit.R == wr)
                    unit.ClearWaypoint();

                if (unit.CivIndex == 0) RefreshFog();
            }
        }

        /// <summary>
        /// Funda una ciudad con el Colono actualmente seleccionado (tecla B).
        /// El Colono es consumido y se crea una ciudad en su tile.
        /// </summary>
        private void TryFoundCity()
        {
            if (_selected == null) return;
            if (!UnitTypeData.GetStats(_selected.UnitType).CanFoundCity) return;
            if (!_cityManager.CanFoundAt(_selected.Q, _selected.R)) return;

            var settler = _selected;
            Deselect();   // limpia _selected, overlay, señales

            if (_cityManager.TryFoundCity(settler))
            {
                _byHex.Remove(new HexCoord(settler.Q, settler.R));
                _units.Remove(settler);
                settler.QueueFree();
            }

            RefreshFog();
        }

        // ================================================================
        //  MEJORAS DEL CONSTRUCTOR
        // ================================================================

        private void TryBuildImprovement(TileImprovement improvement)
        {
            if (_selected == null) return;
            if (!UnitTypeData.GetStats(_selected.UnitType).CanBuildImprovements) return;

            int q = _selected.Q, r = _selected.R;
            var t = _map.GetTileType(q, r);
            if (t == null || t == TileType.Ocean || t == TileType.Coast) return;

            // Minas solo en colinas/montañas
            if (improvement == TileImprovement.Mine &&
                t != TileType.Hills && t != TileType.Mountains) return;

            // Riego no en bosques ni montañas
            if (improvement == TileImprovement.Irrigation &&
                (t == TileType.Mountains || t == TileType.Forest)) return;

            // Granjas solo en Llanura o Pastizal
            if (improvement == TileImprovement.Farm &&
                t != TileType.Plains && t != TileType.Grassland) return;

            // Evitar construir lo mismo dos veces (granjas y riegos se excluyen mutuamente)
            var existing = _map.GetImprovement(q, r);
            if (existing == improvement) return;
            if (improvement == TileImprovement.Farm       && existing == TileImprovement.Irrigation) return;
            if (improvement == TileImprovement.Irrigation && existing == TileImprovement.Farm) return;

            _map.SetImprovement(q, r, improvement);
            _selected.ConsumeAllMovement();

            // Refrescar overlay (sin movimiento restante, el área alcanzable es vacía)
            _reachable = Pathfinder.GetReachable(_map, _selected.Q, _selected.R, _selected.RemainingMovement);
            _overlay.SetReachable(_reachable);
            _overlay.SetPath(new List<HexCoord>());
            EmitUnitSelected(_selected);

            GD.Print($"[UnitManager] Constructor construyó {improvement} en ({q},{r})");
        }

        // ================================================================
        //  CICLO Y FORTIFICACIÓN
        // ================================================================

        /// <summary>
        /// Selecciona la siguiente unidad propia que aún tiene movimiento y no está fortificada.
        /// Si no hay ninguna, no hace nada (el jugador decide cuándo terminar el turno).
        /// </summary>
        private void CycleToNextUnit()
        {
            var active = _units
                .Where(u => u.CivIndex == 0 && u.RemainingMovement > 0 && !u.IsFortified)
                .ToList();

            if (active.Count == 0) return;

            int startIdx = _selected != null ? active.IndexOf(_selected) : -1;
            int nextIdx  = (startIdx + 1) % active.Count;
            var next     = active[nextIdx];
            if (next == _selected) return;   // única unidad activa — ya está seleccionada

            EmitSignal(SignalName.CityDeselected);
            SelectUnit(next);
            _camera.FocusOn(HexTile3D.AxialToWorld(next.Q, next.R));
        }

        /// <summary>
        /// Fortifica la unidad seleccionada (sólo unidades de combate).
        /// La unidad obtiene el visual de escudo y el juego cicla a la siguiente activa.
        /// </summary>
        private void TryFortify()
        {
            if (_selected == null) return;
            var stats = UnitTypeData.GetStats(_selected.UnitType);
            if (stats.CanFoundCity || stats.CanBuildImprovements) return;  // no combate

            _selected.Fortify();
            EmitUnitSelected(_selected);   // actualizar HUD
            CycleToNextUnit();             // saltar a la siguiente unidad activa
        }

        /// <summary>
        /// Saltea la unidad seleccionada: renuncia al movimiento sin hacer nada.
        /// Marca la unidad como lista para el turno y cicla a la siguiente.
        /// </summary>
        public void TrySkipUnit()
        {
            if (_selected == null) return;
            _selected.SkipTurn();
            EmitUnitSelected(_selected);
            CycleToNextUnit();
        }

        /// <summary>
        /// Activa el modo auto-exploración en la unidad seleccionada (tecla A).
        /// La unidad se moverá automáticamente hacia tiles sin explorar cada turno.
        /// </summary>
        private void TryAutoExplore()
        {
            if (_selected == null) return;
            _selected.SetAutoExplore(true);
            EmitUnitSelected(_selected);
            CycleToNextUnit();
        }

        /// <summary>
        /// Mueve un paso hacia el tile no explorado más cercano.
        /// Si no hay tiles sin explorar, desactiva el modo y salta el turno.
        /// </summary>
        private void ProcessAutoExploreUnit(Unit unit)
        {
            var target = _map.FindNearestUnexplored(unit.Q, unit.R);
            if (target == null)
            {
                unit.SetAutoExplore(false);
                unit.SkipTurn();
                return;
            }

            var path = Pathfinder.FindPath(_map, unit.Q, unit.R,
                                           target.Q, target.R, float.MaxValue);
            if (path == null || path.Count < 2)
            {
                unit.SkipTurn();
                return;
            }

            _byHex.Remove(new HexCoord(unit.Q, unit.R));
            float movLeft = unit.RemainingMovement;
            for (int i = 1; i < path.Count; i++)
            {
                var   step = path[i];
                float cost = _map.GetEffectiveCost(step.Q, step.R);
                if (movLeft < cost) break;
                movLeft -= cost;
                unit.ConsumeMovement(cost);
                unit.PlaceAt(step.Q, step.R, _map.GetTileHeight(step.Q, step.R));
            }
            _byHex[new HexCoord(unit.Q, unit.R)] = unit;

            if (unit.CivIndex == 0) RefreshFog();
        }

        // ================================================================
        //  IA — CIVILIZACIÓN ROJA
        // ================================================================

        /// <summary>Ejecuta el turno completo de todas las civilizaciones IA (civIndex != 0).</summary>
        private void ProcessAITurn()
        {
            // 1. Restaurar movimiento
            foreach (var unit in _units)
                if (unit.CivIndex != 0) unit.ResetMovement();

            // 2. Gestionar colas de producción de ciudades IA
            foreach (var city in _cityManager.AllCities)
            {
                if (city.CivIndex == 0) continue;
                if (city.BuildingUnit.HasValue || city.BuildingBuilding.HasValue) continue;

                int aiSettlers = _units.Count(u => u.CivIndex == city.CivIndex && u.UnitType == UnitType.Settler);
                int aiCities   = _cityManager.AllCities.Count(c => c.CivIndex == city.CivIndex);

                if (!city.Buildings.Contains(Natiolation.Cities.BuildingType.Granary))
                    _cityManager.SetProductionQueue(city, Natiolation.Cities.BuildingType.Granary);
                else if (aiSettlers == 0 && aiCities < 4)
                    _cityManager.SetProductionQueue(city, UnitType.Settler);
                else if (!city.Buildings.Contains(Natiolation.Cities.BuildingType.Barracks))
                    _cityManager.SetProductionQueue(city, Natiolation.Cities.BuildingType.Barracks);
                else
                    _cityManager.SetProductionQueue(city, UnitType.Warrior);
            }

            // 3. Mover unidades IA
            foreach (var unit in _units.ToList())
                if (unit.CivIndex != 0) AIProcessUnit(unit);
        }

        private void AIProcessUnit(Unit unit)
        {
            if (unit.RemainingMovement <= 0) return;
            var stats = UnitTypeData.GetStats(unit.UnitType);

            if      (stats.CanFoundCity)        AISettlerBehavior(unit);
            else if (stats.CanBuildImprovements) AIWorkerBehavior(unit);
            else                                 AIMilitaryBehavior(unit);
        }

        // ── IA Colono ─────────────────────────────────────────────────────

        private void AISettlerBehavior(Unit unit)
        {
            if (_cityManager.CanFoundAt(unit.Q, unit.R))
            {
                AIFoundCity(unit);
                return;
            }
            var spot = FindBestFoundingSpot(unit.Q, unit.R);
            if (spot.HasValue) AIMoveToward(unit, spot.Value.q, spot.Value.r);
            else               unit.ConsumeAllMovement();
        }

        private (int q, int r)? FindBestFoundingSpot(int fromQ, int fromR)
        {
            for (int radius = 1; radius <= 22; radius++)
            for (int dq = -radius; dq <= radius; dq++)
            for (int dr = -radius; dr <= radius; dr++)
            {
                int q = fromQ + dq, r = fromR + dr;
                if (_cityManager.CanFoundAt(q, r)) return (q, r);
            }
            return null;
        }

        private void AIFoundCity(Unit settler)
        {
            if (!_cityManager.TryFoundCity(settler)) return;
            _byHex.Remove(new HexCoord(settler.Q, settler.R));
            _units.Remove(settler);
            settler.QueueFree();
            RefreshFog();
        }

        // ── IA Militar ────────────────────────────────────────────────────

        private void AIMilitaryBehavior(Unit unit)
        {
            // Si estamos en guerra con el jugador (civ 0), buscar y atacar
            if (GameManager.Instance.IsAtWar(unit.CivIndex, 0))
            {
                Unit? target = null; int bestDist = int.MaxValue;
                foreach (var u in _units)
                {
                    if (u.CivIndex == unit.CivIndex) continue;
                    int d = HexDist(unit.Q, unit.R, u.Q, u.R);
                    if (d < bestDist) { bestDist = d; target = u; }
                }
                if (target != null) { AIMoveToward(unit, target.Q, target.R); return; }
            }

            // Si no hay guerra: explorar el mapa aleatoriamente
            AIExplore(unit);
        }

        private void AIExplore(Unit unit)
        {
            var reachable = Pathfinder.GetReachable(_map, unit.Q, unit.R, unit.RemainingMovement)
                .Where(h => !_byHex.ContainsKey(h))
                .ToList();
            if (reachable.Count == 0) { unit.ConsumeAllMovement(); return; }

            var pick = reachable[GD.RandRange(0, reachable.Count - 1)];
            _byHex.Remove(new HexCoord(unit.Q, unit.R));
            unit.PlaceAt(pick.Q, pick.R, _map.GetTileHeight(pick.Q, pick.R));
            unit.ConsumeAllMovement();
            _byHex[new HexCoord(unit.Q, unit.R)] = unit;
        }

        // ── IA Constructor ────────────────────────────────────────────────

        private void AIWorkerBehavior(Unit unit)
        {
            int q = unit.Q, r = unit.R;
            var tileType = _map.GetTileType(q, r);
            var existing = _map.GetImprovement(q, r);

            if (tileType.HasValue && tileType != TileType.Ocean && tileType != TileType.Coast)
            {
                if (tileType == TileType.Hills || tileType == TileType.Mountains)
                {
                    if (existing != TileImprovement.Mine)
                    { _map.SetImprovement(q, r, TileImprovement.Mine); unit.ConsumeAllMovement(); return; }
                }
                else if (tileType != TileType.Mountains && tileType != TileType.Forest)
                {
                    if (existing == TileImprovement.None)
                    { _map.SetImprovement(q, r, TileImprovement.Irrigation); unit.ConsumeAllMovement(); return; }
                }
            }

            // Moverse hacia la ciudad IA más cercana
            City? nearestCity = null; int nd = int.MaxValue;
            foreach (var c in _cityManager.AllCities)
            {
                if (c.CivIndex != unit.CivIndex) continue;
                int d = HexDist(unit.Q, unit.R, c.Q, c.R);
                if (d < nd) { nd = d; nearestCity = c; }
            }
            if (nearestCity != null) AIMoveToward(unit, nearestCity.Q + 1, nearestCity.R);
            else                     unit.ConsumeAllMovement();
        }

        // ── Movimiento IA (teleport instantáneo) ─────────────────────────

        private void AIMoveToward(Unit unit, int targetQ, int targetR)
        {
            var reachable = Pathfinder.GetReachable(_map, unit.Q, unit.R, unit.RemainingMovement);

            // Si estamos en guerra y hay un enemigo adyacente alcanzable → combate táctico
            if (GameManager.Instance.IsAtWar(unit.CivIndex, 0)
                && UnitTypeData.GetStats(unit.UnitType).CombatStrength > 0)
            {
                foreach (var hex in reachable)
                {
                    if (_byHex.TryGetValue(hex, out var occ) && occ.CivIndex != unit.CivIndex)
                    {
                        // IA inicia combate táctico — solo si no hay ya una batalla activa
                        if (TacticalBattleManager.Instance?.IsActive != true)
                        {
                            var atkUnits = new System.Collections.Generic.List<Unit> { unit };
                            var defUnits = new System.Collections.Generic.List<Unit> { occ };
                            var conflictHex = new HexCoord(hex.Q, hex.R);
                            TacticalBattleManager.Instance?.StartBattle(
                                atkUnits, defUnits, conflictHex, _map, null, null);
                        }
                        return;
                    }
                }
            }

            // Movimiento normal: acercarse al objetivo
            HexCoord? best     = null;
            int       bestDist = HexDist(unit.Q, unit.R, targetQ, targetR);

            foreach (var hex in reachable)
            {
                if (_byHex.ContainsKey(hex)) continue;
                int d = HexDist(hex.Q, hex.R, targetQ, targetR);
                if (d < bestDist) { bestDist = d; best = hex; }
            }

            if (best == null) { unit.ConsumeAllMovement(); return; }

            _byHex.Remove(new HexCoord(unit.Q, unit.R));
            unit.PlaceAt(best.Q, best.R, _map.GetTileHeight(best.Q, best.R));
            unit.ConsumeAllMovement();
            _byHex[new HexCoord(unit.Q, unit.R)] = unit;
        }

        private static int HexDist(int q1, int r1, int q2, int r2)
        {
            int s1 = -q1 - r1, s2 = -q2 - r2;
            return (Mathf.Abs(q1 - q2) + Mathf.Abs(r1 - r2) + Mathf.Abs(s1 - s2)) / 2;
        }

        // ================================================================
        //  COMBATE
        // ================================================================

        private void TryAttack(Unit attacker, Unit defender)
        {
            var aStats = UnitTypeData.GetStats(attacker.UnitType);
            var dStats = UnitTypeData.GetStats(defender.UnitType);

            // Unidades no-combate no pueden atacar
            if (aStats.CombatStrength == 0) return;

            // Auto-declarar guerra si no estamos en guerra
            var gm = GameManager.Instance;
            if (!gm.IsAtWar(attacker.CivIndex, defender.CivIndex))
                gm.DeclareWar(attacker.CivIndex, defender.CivIndex);

            // ── Calcular fuerzas ─────────────────────────────────────────
            float terrainMult = GetTerrainDefenseMultiplier(defender.Q, defender.R);
            if (defender.IsFortified)                                    terrainMult *= 1.5f;
            if (_cityManager.GetCityAt(defender.Q, defender.R) != null) terrainMult *= 2.0f;

            float atkPow  = Mathf.Max(1f, aStats.CombatStrength + (attacker.IsVeteran ? 2 : 0));
            float defPow  = Mathf.Max(1f, dStats.CombatStrength * terrainMult);

            float atkRoll = (float)GD.RandRange(0.8, 1.2) * atkPow;
            float defRoll = (float)GD.RandRange(0.8, 1.2) * defPow;

            // ── Calcular daño proporcional (basado en Civ 5) ────────────
            // Daño al defensor: mayor si atacante es más fuerte
            int defDamage = Mathf.Clamp((int)(30f * atkRoll / defRoll), 5, 90);
            // Contra-daño al atacante: menor que el daño principal
            int atkDamage = Mathf.Clamp((int)(18f * defRoll / atkRoll), 2, 60);

            int defQ = defender.Q, defR = defender.R;

            // ── Aplicar daño ─────────────────────────────────────────────
            defender.TakeDamage(defDamage);
            if (IsInstanceValid(attacker)) attacker.TakeDamage(atkDamage);

            bool defenderDied = defender.CurrentHP <= 0;
            bool attackerDied = IsInstanceValid(attacker) && attacker.CurrentHP <= 0;

            // ── Mensaje de combate ───────────────────────────────────────
            string atkName = aStats.DisplayName;
            string defName = dStats.DisplayName;
            string msg;
            if (defenderDied && attackerDied)
                msg = $"⚔  {atkName} y {defName} se destruyen mutuamente";
            else if (defenderDied)
                msg = $"⚔  {atkName} venció a {defName} ({defDamage} daño)";
            else if (attackerDied)
                msg = $"☠  {atkName} fue derrotado por {defName}";
            else
                msg = $"⚔  {atkName}→{defDamage}dmg | {defName}→{atkDamage}dmg";

            if (attacker.CivIndex == 0 || defender.CivIndex == 0)
                EmitSignal(SignalName.CombatEvent, msg);

            // ── Destruir unidades muertas ────────────────────────────────
            if (defenderDied) DestroyUnit(defender);
            if (attackerDied && IsInstanceValid(attacker)) DestroyUnit(attacker);

            // ── Atacante avanza si ganó sin morir ────────────────────────
            if (!attackerDied && defenderDied && IsInstanceValid(attacker))
            {
                var movePath = new List<HexCoord>
                {
                    new(attacker.Q, attacker.R),
                    new(defQ,       defR),
                };
                _ = IssueMove(attacker, movePath);
            }

            if (!attackerDied && IsInstanceValid(attacker))
                attacker.ConsumeAllMovement();
        }

        private void DestroyUnit(Unit unit)
        {
            bool wasSelected = _selected == unit;
            if (wasSelected) Deselect();
            _byHex.Remove(new HexCoord(unit.Q, unit.R));
            _units.Remove(unit);
            unit.QueueFree();
            if (unit.CivIndex == 0) RefreshFog();
        }

        private float GetTerrainDefenseMultiplier(int q, int r)
        {
            var t = _map.GetTileType(q, r);
            if (t == null) return 1f;
            return t.Value switch
            {
                TileType.Forest    => 1.5f,
                TileType.Hills     => 1.5f,
                TileType.Mountains => 2.0f,
                _                  => 1f,
            };
        }

        public override void _ExitTree()
        {
            // ── CityManager signal ───────────────────────────────────────────
            if (_cityManager != null)
                _cityManager.UnitProductionComplete -= OnUnitProductionComplete;

            // ── GameManager event ────────────────────────────────────────────
            if (GameManager.Instance != null)
                GameManager.Instance.WarDeclared -= OnWarDeclared;

            // ── TacticalBattleManager static event ──────────────────────────
            TacticalBattleManager.BattleEnded -= OnTacticalBattleEnded;

            // ── ConfirmationDialog (hijo, pero explicitamos para claridad) ───
            if (_mergeDialog != null)
            {
                _mergeDialog.Confirmed -= OnMergeConfirmed;
                _mergeDialog.Canceled  -= OnMergeCanceled;
            }
        }

        private void OnWarDeclared(int a, int b)
        {
            if (a == 0 || b == 0)
                EmitSignal(SignalName.CombatEvent, "⚔  ¡GUERRA DECLARADA!");
        }

        private void OnTacticalBattleEnded(bool attackersWon)
        {
            // Limpiar unidades muertas (HP <= 0) que el combate táctico pudo haber dejado
            var deadUnits = _units.Where(u => !IsInstanceValid(u) || u.CurrentHP <= 0).ToList();
            foreach (var dead in deadUnits)
            {
                _byHex.Remove(new HexCoord(dead.Q, dead.R));
                _units.Remove(dead);
                if (IsInstanceValid(dead)) dead.QueueFree();
            }

            // Limpiar ejércitos sin unidades vivas
            var deadArmies = _armies.Where(a => !IsInstanceValid(a) || a.Count == 0).ToList();
            foreach (var da in deadArmies)
            {
                _armiesByHex.Remove(new HexCoord(da.Q, da.R));
                _armies.Remove(da);
                if (IsInstanceValid(da)) da.QueueFree();
            }

            // Restaurar visibilidad de unidades sobrevivientes en ejércitos
            // (el manager de ejércitos las tiene con Visible=false para el modelo del banner)
            foreach (var army in _armies)
                foreach (var unit in army.Units)
                    if (IsInstanceValid(unit)) unit.Visible = false;

            // Refrescar niebla de guerra
            RefreshFog();

            string msg = attackersWon ? "⚔  Victoria táctica — atacantes ganaron" : "☠  Derrota táctica — defensores resistieron";
            EmitSignal(SignalName.CombatEvent, msg);
        }

        // ================================================================
        //  PRODUCCIÓN DE CIUDADES
        // ================================================================

        private void OnUnitProductionComplete(
            int q, int r, int civIndex, Color civColor, int unitTypeInt)
        {
            var unitType = (UnitType)unitTypeInt;
            var spawn    = FindSpawnPos(q, r);
            Spawn(unitType, spawn.Q, spawn.R, civColor, civIndex);

            // Unidades de combate producidas en ciudad con Cuartel → veteranas
            var city    = _cityManager.GetCityAt(q, r);
            var uStats  = UnitTypeData.GetStats(unitType);
            if (city != null
                && city.Buildings.Contains(BuildingType.Barracks)
                && uStats.CombatStrength > 0)
            {
                // La última unidad añadida es la recién spawnada
                var newUnit = _byHex.TryGetValue(new HexCoord(spawn.Q, spawn.R), out var u) ? u : null;
                newUnit?.MakeVeteran();
            }

            RefreshFog();
            GD.Print($"[UnitManager] Unidad {unitType} spawnada en ({spawn.Q},{spawn.R}).");
        }

        private HexCoord FindSpawnPos(int q, int r)
        {
            var center = new HexCoord(q, r);
            if (!_byHex.ContainsKey(center)) return center;

            int[] dq = {  1, -1,  0,  0,  1, -1 };
            int[] dr = {  0,  0,  1, -1, -1,  1 };
            for (int i = 0; i < 6; i++)
            {
                int aq = q + dq[i], ar = r + dr[i];
                var t = _map.GetTileType(aq, ar);
                if (t != null && Pathfinder.IsPassable(t.Value)
                              && !_byHex.ContainsKey(new HexCoord(aq, ar)))
                    return new HexCoord(aq, ar);
            }
            return center;
        }

        // ================================================================
        //  SIGNALS
        // ================================================================

        private void EmitUnitSelected(Unit unit)
        {
            var stats = UnitTypeData.GetStats(unit.UnitType);
            GD.Print($"[UnitManager] EmitUnitSelected: {stats.DisplayName} mov={unit.RemainingMovement}/{unit.MaxMovement}");
            EmitSignal(SignalName.UnitSelected,
                stats.DisplayName, unit.CivColor,
                unit.RemainingMovement, unit.MaxMovement,
                (int)unit.UnitType, unit.Q, unit.R, unit.IsFortified);
        }

        // ================================================================
        //  1 UPT — HELPERS DE APILAMIENTO
        // ================================================================

        /// <summary>
        /// Una unidad es militar si no puede fundar ciudades ni construir mejoras.
        /// Settler y Worker pueden coexistir con militares; militares no pueden apilarse.
        /// </summary>
        private static bool IsMilitary(UnitType type)
        {
            var s = UnitTypeData.GetStats(type);
            return !s.CanFoundCity && !s.CanBuildImprovements;
        }

        /// <summary>
        /// Retorna el conjunto de hexes ocupados por unidades militares del mismo bando
        /// que la unidad dada, excluyendo su propia posición.
        /// Se pasa al Pathfinder como <c>blockedHexes</c> para aplicar la regla 1 UPT.
        /// </summary>
        private HashSet<HexCoord> GetFriendlyMilitaryHexes(Unit unit)
        {
            var blocked = new HashSet<HexCoord>();
            var self    = new HexCoord(unit.Q, unit.R);
            foreach (var kv in _byHex)
            {
                if (kv.Value == unit) continue;                    // excluir a sí misma
                if (kv.Value.CivIndex != unit.CivIndex) continue;  // solo aliadas
                if (!IsMilitary(kv.Value.UnitType)) continue;      // solo militares
                blocked.Add(kv.Key);
            }
            return blocked;
        }

        private void EmitTileHovered(HexCoord? hex)
        {
            if (hex == null) { EmitSignal(SignalName.TileHovered, "—", 0, 0, 0); return; }
            var t = _map.GetTileType(hex.Q, hex.R);
            if (t == null) return;
            EmitSignal(SignalName.TileHovered,
                t.Value.TileName(), t.Value.FoodYield(),
                t.Value.ProductionYield(), t.Value.MovementCost());
        }

        // ================================================================
        //  EJÉRCITOS — SELECCIÓN Y MOVIMIENTO
        // ================================================================

        private void SelectArmy(Army army)
        {
            if (_selectedArmy != null) _selectedArmy.Select(false);
            _selectedArmy = army;
            army.Select(true);
            _reachable = Pathfinder.GetReachable(
                _map, army.Q, army.R, army.RemainingMovement,
                GetArmyBlockedHexes(army));
            _overlay.SetReachable(_reachable);
            _overlay.SetSelectedUnit(new HexCoord(army.Q, army.R));
            _overlay.SetHovered(_hovered);
            ArmySelectedEvent?.Invoke(army);
        }

        private void DeselectArmy()
        {
            if (_selectedArmy == null) return;
            _selectedArmy.Select(false);
            _selectedArmy = null;
            _pendingDeployUnit = null;
            _reachable.Clear();
            _overlay.SetReachable(_reachable);
            _overlay.SetPath(new List<HexCoord>());
            _overlay.SetSelectedUnit(null);
            ArmyDeselectedEvent?.Invoke();
        }

        private async Task IssueArmyMove(Army army, List<HexCoord> path)
        {
            _armiesByHex.Remove(new HexCoord(army.Q, army.R));
            _overlay.ClearAll();
            _overlay.SetHovered(_hovered);

            await army.MoveTo(path, _map);

            _armiesByHex[new HexCoord(army.Q, army.R)] = army;

            if (army.CivIndex == 0) RefreshFog();

            if (_selectedArmy == army)
            {
                _reachable = Pathfinder.GetReachable(
                    _map, army.Q, army.R, army.RemainingMovement,
                    GetArmyBlockedHexes(army));
                _overlay.SetReachable(_reachable);
                _overlay.SetHovered(_hovered);
                _overlay.MoveSelectedUnit(new HexCoord(army.Q, army.R));
            }
        }

        /// <summary>Hexes bloqueados para que un ejército no se superponga a otro ejército o unidad aliada.</summary>
        private HashSet<HexCoord> GetArmyBlockedHexes(Army army)
        {
            var blocked = new HashSet<HexCoord>();
            foreach (var kv in _byHex)
            {
                if (kv.Value.CivIndex != army.CivIndex) continue;
                blocked.Add(kv.Key);
            }
            foreach (var kv in _armiesByHex)
            {
                if (kv.Value == army) continue;
                if (kv.Value.CivIndex != army.CivIndex) continue;
                blocked.Add(kv.Key);
            }
            return blocked;
        }

        // ================================================================
        //  EJÉRCITOS — FUSIÓN Y DESPLIEGUE
        // ================================================================

        private void ShowMergeDialog(Unit a, Unit b)
        {
            _pendingMergeA   = a;
            _pendingMergeB   = b;
            _pendingJoinArmy = null;
            var nameA = UnitTypeData.GetStats(a.UnitType).DisplayName;
            var nameB = UnitTypeData.GetStats(b.UnitType).DisplayName;
            _mergeDialog.DialogText = $"¿Deseas fusionar {nameA} y {nameB} en un Ejército?";
            _mergeDialog.PopupCentered();
        }

        private void ShowJoinArmyDialog(Unit unit, Army army)
        {
            _pendingMergeA   = unit;
            _pendingMergeB   = null;
            _pendingJoinArmy = army;
            var name = UnitTypeData.GetStats(unit.UnitType).DisplayName;
            _mergeDialog.DialogText = $"¿Deseas que {name} se una al Ejército ({army.Count} unidades)?";
            _mergeDialog.PopupCentered();
        }

        private void OnMergeConfirmed()
        {
            if (_pendingMergeA == null) return;

            if (_pendingJoinArmy != null)
            {
                // Unidad uniéndose a ejército existente
                JoinUnitIntoArmy(_pendingMergeA, _pendingJoinArmy);
            }
            else if (_pendingMergeB != null)
            {
                // Dos unidades → nuevo ejército en la posición de la unidad A
                MergeUnitsIntoArmy(_pendingMergeA, _pendingMergeB);
            }

            _pendingMergeA   = null;
            _pendingMergeB   = null;
            _pendingJoinArmy = null;
        }

        private void OnMergeCanceled()
        {
            _pendingMergeA   = null;
            _pendingMergeB   = null;
            _pendingJoinArmy = null;
        }

        private void MergeUnitsIntoArmy(Unit a, Unit b)
        {
            if (!IsInstanceValid(a) || !IsInstanceValid(b)) return;

            // El ejército se crea en el hex de la unidad A (la que inició la fusión)
            var army = new Army
            {
                CivColor = a.CivColor,
                CivIndex = a.CivIndex,
            };
            AddChild(army);
            army.PlaceAt(a.Q, a.R, _map.GetTileHeight(a.Q, a.R));

            // Quitar ambas unidades del mapa individual
            Deselect();
            _byHex.Remove(new HexCoord(a.Q, a.R));
            _byHex.Remove(new HexCoord(b.Q, b.R));

            army.AddUnit(a);
            army.AddUnit(b);

            _armies.Add(army);
            _armiesByHex[new HexCoord(army.Q, army.R)] = army;

            if (a.CivIndex == 0) RefreshFog();
            SelectArmy(army);
            EmitSignal(SignalName.CombatEvent,
                $"⚔  Ejército formado ({army.Count} unidades)");
        }

        private void JoinUnitIntoArmy(Unit unit, Army army)
        {
            if (!IsInstanceValid(unit) || !IsInstanceValid(army)) return;

            Deselect();
            _byHex.Remove(new HexCoord(unit.Q, unit.R));

            army.AddUnit(unit);

            if (unit.CivIndex == 0) RefreshFog();
            SelectArmy(army);
            EmitSignal(SignalName.CombatEvent,
                $"⚔  Ejército reforzado ({army.Count} unidades)");
        }

        /// <summary>
        /// Despliega una unidad del ejército hacia un hex adyacente vacío.
        /// Llamado por el HUD (botón "Desplegar") y confirmado con right-click.
        /// </summary>
        public void DeployUnitFromArmy(Army army, Unit unit, int q, int r)
        {
            if (!IsInstanceValid(army) || !IsInstanceValid(unit)) return;
            if (!army.Units.Contains(unit)) return;

            army.RemoveUnit(unit);   // restaura unit.Visible = true
            unit.PlaceAt(q, r, _map.GetTileHeight(q, r));
            unit.ConsumeAllMovement();

            _byHex[new HexCoord(q, r)] = unit;
            _units.Remove(unit);     // quitar y re-añadir para asegurar consistencia
            _units.Add(unit);

            // Si el ejército quedó con una sola unidad, disolver
            if (army.Count <= 1)
            {
                DisbandArmy(army, reassignToMap: true);
            }
            else
            {
                SelectArmy(army);   // re-seleccionar para refrescar el panel HUD
            }

            if (unit.CivIndex == 0) RefreshFog();
            EmitSignal(SignalName.CombatEvent, $"→  Unidad desplegada en ({q},{r})");
        }

        /// <summary>
        /// Disuelve el ejército. Si <c>reassignToMap</c>, las unidades restantes
        /// se devuelven al mapa en la posición del ejército.
        /// </summary>
        private void DisbandArmy(Army army, bool reassignToMap = false)
        {
            DeselectArmy();
            _armiesByHex.Remove(new HexCoord(army.Q, army.R));
            _armies.Remove(army);

            if (reassignToMap)
            {
                foreach (var u in army.Units.ToList())
                {
                    army.RemoveUnit(u);
                    u.PlaceAt(army.Q, army.R, _map.GetTileHeight(army.Q, army.R));
                    _byHex[new HexCoord(u.Q, u.R)] = u;
                }
            }

            army.QueueFree();
        }

        /// <summary>
        /// Llamado desde el HUD cuando el jugador hace clic en "Desplegar" junto a una unidad.
        /// La siguiente vez que el jugador haga right-click en un hex adyacente válido,
        /// la unidad será desplegada.
        /// </summary>
        public void PrepareDeployUnit(Unit unit)
        {
            if (_selectedArmy == null) return;
            if (!_selectedArmy.Units.Contains(unit)) return;
            _pendingDeployUnit = unit;
            var name = UnitTypeData.GetStats(unit.UnitType).DisplayName;
            EmitSignal(SignalName.CombatEvent,
                $"→  {name} lista para desplegar — click derecho en hex adyacente");
        }

        // ================================================================
        //  EJÉRCITOS — COMBATE
        // ================================================================

        private void TryAttackArmyVsUnit(Army army, Unit defender)
        {
            var dStats    = UnitTypeData.GetStats(defender.UnitType);
            float atkPow  = Mathf.Max(1f, army.CombatStrength);
            float defPow  = Mathf.Max(1f, dStats.CombatStrength
                                         * GetTerrainDefenseMultiplier(defender.Q, defender.R));
            float atkRoll = (float)GD.RandRange(0.8, 1.2) * atkPow;
            float defRoll = (float)GD.RandRange(0.8, 1.2) * defPow;

            int defDamage = Mathf.Clamp((int)(30f * atkRoll / defRoll), 5, 90);
            int atkDamage = Mathf.Clamp((int)(18f * defRoll / atkRoll), 2, 60);

            defender.TakeDamage(defDamage);
            bool armyAlive = army.TakeDamage(atkDamage);

            string msg = defender.CurrentHP <= 0
                ? $"⚔  Ejército derrotó a {dStats.DisplayName} ({defDamage} daño)"
                : $"⚔  Ejército→{defDamage}dmg | {dStats.DisplayName}→{atkDamage}dmg";
            EmitSignal(SignalName.CombatEvent, msg);

            int defQ = defender.Q, defR = defender.R;
            if (defender.CurrentHP <= 0) DestroyUnit(defender);
            if (!armyAlive) DestroyArmy(army); else army.ConsumeAllMovement();

            if (armyAlive && IsInstanceValid(army) && defender.CurrentHP <= 0)
            {
                var movePath = new List<HexCoord> { new(army.Q, army.R), new(defQ, defR) };
                _ = IssueArmyMove(army, movePath);
            }
        }

        private void TryAttackUnitVsArmy(Unit attacker, Army army)
        {
            var aStats    = UnitTypeData.GetStats(attacker.UnitType);
            if (aStats.CombatStrength == 0) return;

            float atkPow  = Mathf.Max(1f, aStats.CombatStrength + (attacker.IsVeteran ? 2 : 0));
            float defPow  = Mathf.Max(1f, army.CombatStrength);
            float atkRoll = (float)GD.RandRange(0.8, 1.2) * atkPow;
            float defRoll = (float)GD.RandRange(0.8, 1.2) * defPow;

            int defDamage = Mathf.Clamp((int)(30f * atkRoll / defRoll), 5, 90);
            int atkDamage = Mathf.Clamp((int)(18f * defRoll / atkRoll), 2, 60);

            bool armyAlive = army.TakeDamage(defDamage);
            attacker.TakeDamage(atkDamage);

            string msg = !armyAlive
                ? $"⚔  {aStats.DisplayName} destruyó el ejército"
                : $"⚔  {aStats.DisplayName}→{defDamage}dmg | Ejército→{atkDamage}dmg";
            EmitSignal(SignalName.CombatEvent, msg);

            if (!armyAlive) DestroyArmy(army);
            if (attacker.CurrentHP <= 0 && IsInstanceValid(attacker)) DestroyUnit(attacker);
            else if (IsInstanceValid(attacker)) attacker.ConsumeAllMovement();
        }

        private void TryAttackArmyVsArmy(Army attacker, Army defender)
        {
            float atkRoll = (float)GD.RandRange(0.8, 1.2) * Mathf.Max(1f, attacker.CombatStrength);
            float defRoll = (float)GD.RandRange(0.8, 1.2) * Mathf.Max(1f, defender.CombatStrength);

            int defDamage = Mathf.Clamp((int)(30f * atkRoll / defRoll), 5, 90);
            int atkDamage = Mathf.Clamp((int)(18f * defRoll / atkRoll), 2, 60);

            bool atkAlive = !attacker.TakeDamage(atkDamage) == false;  // true si sigue vivo
            bool defAlive = defender.TakeDamage(defDamage);

            atkAlive = attacker.Count > 0;  // re-evaluar tras el daño
            defAlive = defender.Count > 0;

            EmitSignal(SignalName.CombatEvent,
                $"⚔  Ejércitos se enfrentan: Atk→{defDamage}dmg | Def→{atkDamage}dmg");

            if (!defAlive) DestroyArmy(defender);
            if (!atkAlive) DestroyArmy(attacker);
            else attacker.ConsumeAllMovement();
        }

        private void DestroyArmy(Army army)
        {
            bool wasSelected = _selectedArmy == army;
            if (wasSelected) DeselectArmy();
            _armiesByHex.Remove(new HexCoord(army.Q, army.R));
            _armies.Remove(army);
            army.QueueFree();
            RefreshFog();
        }

        private static bool IsAdjacent(int q1, int r1, int q2, int r2)
            => HexDist(q1, r1, q2, r2) == 1;

        // ================================================================
        //  PICKING 3D POR RAYCAST FÍSICO
        // ================================================================

        private HexCoord? ScreenToHex(Vector2 screenPos)
        {
            var spaceState = GetWorld3D().DirectSpaceState;
            var from = _camera.ProjectRayOrigin(screenPos);
            var to   = from + _camera.ProjectRayNormal(screenPos) * 600f;

            var query  = PhysicsRayQueryParameters3D.Create(from, to);
            var result = spaceState.IntersectRay(query);

            if (result.Count == 0) return null;

            if (!result.TryGetValue("collider", out var colliderVar)) return null;
            var body = colliderVar.As<StaticBody3D>();
            if (body == null || !body.HasMeta("hex_q")) return null;

            int q = body.GetMeta("hex_q").AsInt32();
            int r = body.GetMeta("hex_r").AsInt32();
            return _map.GetTileType(q, r) != null ? new HexCoord(q, r) : null;
        }
    }
}
