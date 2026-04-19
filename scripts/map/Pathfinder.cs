using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Natiolation.Map
{
    /// <summary>
    /// A* sobre el grid hexagonal axial.
    ///
    /// Regla 1 UPT (Un-Unit-Per-Tile):
    ///   Los parámetros opcionales <c>blockedHexes</c> indican tiles en los que
    ///   una unidad no puede TERMINAR su movimiento (por ejemplo, porque hay una
    ///   unidad amiga del mismo tipo). El pathfinder puede atravesar estos tiles
    ///   como nodos intermedios, pero no los incluye en los resultados de destino.
    ///
    /// Thread-safety:
    ///   El algoritmo trabaja internamente sobre <see cref="MapSnapshot"/>, un objeto
    ///   inmutable que captura el estado del mapa en el hilo principal.
    ///   Los métodos *Async capturan el snapshot antes de lanzar Task.Run, por lo
    ///   que la lectura del mapa ocurre en el hilo principal y el A* en el pool.
    /// </summary>
    public static class Pathfinder
    {
        private static readonly int[] DQ = {  1, -1,  0,  0,  1, -1 };
        private static readonly int[] DR = {  0,  0,  1, -1, -1,  1 };

        // ================================================================
        //  API PÚBLICA — ASYNC
        //  Recomendada para movimiento iniciado por el jugador.
        //  El snapshot se captura en el hilo llamante (main thread) antes
        //  de despachar el A* al pool de hilos.
        // ================================================================

        /// <summary>
        /// Busca el camino de menor costo de forma asíncrona.
        /// El snapshot se toma en el hilo actual (debe ser el main thread).
        /// El A* se ejecuta en un hilo de fondo.
        /// </summary>
        public static Task<List<HexCoord>?> FindPathAsync(
            MapManager map,
            int startQ, int startR,
            int endQ,   int endR,
            float moveBudget = float.MaxValue,
            HashSet<HexCoord>? blockedHexes = null)
        {
            // Capturar estado del mapa en el hilo principal
            var snap = map.TakeSnapshot();
            // Copiar blocked set: el caller podría mutar su colección mientras el Task corre
            var blocked = blockedHexes != null ? new HashSet<HexCoord>(blockedHexes) : null;
            return Task.Run(() => FindPathCore(snap, startQ, startR, endQ, endR, moveBudget, blocked));
        }

        /// <summary>
        /// Calcula hexes alcanzables de forma asíncrona.
        /// </summary>
        public static Task<HashSet<HexCoord>> GetReachableAsync(
            MapManager map,
            int q, int r,
            float budget,
            HashSet<HexCoord>? blockedHexes = null)
        {
            var snap    = map.TakeSnapshot();
            var blocked = blockedHexes != null ? new HashSet<HexCoord>(blockedHexes) : null;
            return Task.Run(() => GetReachableCore(snap, q, r, budget, blocked));
        }

        // ================================================================
        //  API PÚBLICA — SÍNCRONA
        //  Compatible con el código existente. Útil para la IA y cálculos
        //  en los que el overhead de Task.Run no merece la pena.
        // ================================================================

        /// <summary>
        /// Encuentra el camino de menor costo entre dos hexes (síncrono).
        /// Devuelve null si no hay camino, el destino es infranqueable o está bloqueado por 1 UPT.
        /// El camino incluye el hex de inicio.
        /// </summary>
        public static List<HexCoord>? FindPath(
            MapManager map,
            int startQ, int startR,
            int endQ,   int endR,
            float moveBudget = float.MaxValue,
            HashSet<HexCoord>? blockedHexes = null)
            => FindPathCore(map.TakeSnapshot(), startQ, startR, endQ, endR, moveBudget, blockedHexes);

        /// <summary>
        /// Calcula todos los hexes alcanzables dentro de un presupuesto de movimiento (síncrono).
        /// </summary>
        public static HashSet<HexCoord> GetReachable(
            MapManager map,
            int q, int r,
            float budget,
            HashSet<HexCoord>? blockedHexes = null)
            => GetReachableCore(map.TakeSnapshot(), q, r, budget, blockedHexes);

        public static bool IsPassable(TileType t) =>
            t != TileType.Ocean && t != TileType.Coast;

        // ================================================================
        //  CORE — trabaja sobre MapSnapshot (thread-safe)
        // ================================================================

        private static List<HexCoord>? FindPathCore(
            MapSnapshot snap,
            int startQ, int startR,
            int endQ,   int endR,
            float moveBudget,
            HashSet<HexCoord>? blockedHexes)
        {
            if (startQ == endQ && startR == endR)
                return new List<HexCoord> { new(startQ, startR) };

            var endType = snap.GetTileType(endQ, endR);
            if (endType == null || !IsPassable(endType.Value)) return null;

            // 1 UPT: no se puede llegar a un hex bloqueado por unidad amiga
            var end = new HexCoord(endQ, endR);
            if (blockedHexes != null && blockedHexes.Contains(end)) return null;

            var openSet  = new PriorityQueue<HexCoord, float>();
            var cameFrom = new Dictionary<HexCoord, HexCoord>();
            var gScore   = new Dictionary<HexCoord, float>();

            var start = new HexCoord(startQ, startR);
            gScore[start] = 0f;
            openSet.Enqueue(start, Heuristic(start, end));

            while (openSet.Count > 0)
            {
                var current = openSet.Dequeue();
                if (current == end)
                    return Reconstruct(cameFrom, current);

                foreach (var nb in GetPassableNeighbors(snap, current))
                {
                    float cost  = snap.GetEffectiveCost(nb.Q, nb.R);
                    float tentG = gScore[current] + cost;

                    if (tentG > moveBudget) continue;

                    if (!gScore.TryGetValue(nb, out float existing) || tentG < existing)
                    {
                        cameFrom[nb] = current;
                        gScore[nb]   = tentG;
                        openSet.Enqueue(nb, tentG + Heuristic(nb, end));
                    }
                }
            }

            return null;
        }

        private static HashSet<HexCoord> GetReachableCore(
            MapSnapshot snap,
            int q, int r,
            float budget,
            HashSet<HexCoord>? blockedHexes)
        {
            var reachable = new HashSet<HexCoord>();
            var frontier  = new PriorityQueue<HexCoord, float>();
            var costs     = new Dictionary<HexCoord, float>();

            var start = new HexCoord(q, r);
            costs[start] = 0f;
            frontier.Enqueue(start, 0f);

            while (frontier.Count > 0)
            {
                var current = frontier.Dequeue();
                float cur   = costs[current];

                foreach (var nb in GetPassableNeighbors(snap, current))
                {
                    float newCost = cur + snap.GetEffectiveCost(nb.Q, nb.R);
                    if (newCost > budget) continue;

                    if (!costs.TryGetValue(nb, out float prev) || newCost < prev)
                    {
                        costs[nb] = newCost;
                        frontier.Enqueue(nb, newCost);

                        // 1 UPT: el tile es traversable pero no es destino válido
                        if (blockedHexes == null || !blockedHexes.Contains(nb))
                            reachable.Add(nb);
                    }
                }
            }

            return reachable;
        }

        private static IEnumerable<HexCoord> GetPassableNeighbors(MapSnapshot snap, HexCoord h)
        {
            for (int i = 0; i < 6; i++)
            {
                int nq = h.Q + DQ[i], nr = h.R + DR[i];
                var t = snap.GetTileType(nq, nr);
                if (t != null && IsPassable(t.Value))
                    yield return new HexCoord(nq, nr);
            }
        }

        private static float Heuristic(HexCoord a, HexCoord b)
        {
            int s1 = -a.Q - a.R, s2 = -b.Q - b.R;
            return (Math.Abs(a.Q - b.Q) + Math.Abs(a.R - b.R) + Math.Abs(s1 - s2)) / 2f;
        }

        private static List<HexCoord> Reconstruct(Dictionary<HexCoord, HexCoord> from, HexCoord end)
        {
            var path = new List<HexCoord> { end };
            while (from.TryGetValue(end, out var prev)) { end = prev; path.Insert(0, end); }
            return path;
        }
    }
}
