using Godot;
using System.Collections.Generic;
using Natiolation.Map;

namespace Natiolation.Core
{
    /// <summary>
    /// Pathfinder aislado para el modo de combate táctico.
    /// Funciona exclusivamente sobre el HashSet de hexes del arena táctica —
    /// NO usa MapManager ni el presupuesto de movimiento global.
    /// </summary>
    public static class TacticalPathfinder
    {
        private static readonly int[] DQ = {  1, -1,  0,  0,  1, -1 };
        private static readonly int[] DR = {  0,  0,  1, -1, -1,  1 };

        /// <summary>
        /// Encuentra el camino de menor número de pasos entre dos hexes del arena.
        /// Retorna null si no hay camino o destino fuera del arena.
        /// El camino incluye el hex de inicio.
        /// </summary>
        public static List<HexCoord>? FindPath(
            HexCoord start,
            HexCoord end,
            HashSet<HexCoord> arena,
            HashSet<HexCoord>? occupied = null)
        {
            if (!arena.Contains(end)) return null;
            if (start == end) return new List<HexCoord> { start };
            if (occupied != null && occupied.Contains(end)) return null;

            var openSet  = new PriorityQueue<HexCoord, float>();
            var cameFrom = new Dictionary<HexCoord, HexCoord>();
            var gScore   = new Dictionary<HexCoord, float>();

            gScore[start] = 0f;
            openSet.Enqueue(start, Heuristic(start, end));

            while (openSet.Count > 0)
            {
                var current = openSet.Dequeue();
                if (current == end)
                    return Reconstruct(cameFrom, current);

                foreach (var nb in GetNeighbors(current, arena))
                {
                    // Unidades enemigas bloquean el paso (no la llegada)
                    // La lógica de "occupied" para destino ya se chequea arriba
                    bool isIntermediate = nb != end;
                    if (isIntermediate && occupied != null && occupied.Contains(nb)) continue;

                    float tentG = gScore[current] + 1f;
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

        /// <summary>
        /// Calcula todos los hexes alcanzables dentro del arena con un presupuesto de pasos.
        /// Los hexes en <c>occupied</c> no se incluyen como destinos válidos pero
        /// NO bloquean el paso (el jugador no puede terminar allí, pero puede rodear).
        /// </summary>
        public static HashSet<HexCoord> GetReachable(
            HexCoord start,
            int movementBudget,
            HashSet<HexCoord> arena,
            HashSet<HexCoord>? occupied = null)
        {
            var reachable = new HashSet<HexCoord>();
            var dist      = new Dictionary<HexCoord, int> { [start] = 0 };
            var queue     = new Queue<HexCoord>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                int d = dist[current];
                if (d >= movementBudget) continue;

                foreach (var nb in GetNeighbors(current, arena))
                {
                    if (dist.ContainsKey(nb)) continue;
                    dist[nb] = d + 1;
                    if (occupied == null || !occupied.Contains(nb))
                        reachable.Add(nb);
                    queue.Enqueue(nb);
                }
            }

            reachable.Remove(start);
            return reachable;
        }

        /// <summary>
        /// Retorna hexes atacables: vecinos del hex actual que estén en el arena
        /// y que contengan unidades enemigas.
        /// </summary>
        public static HashSet<HexCoord> GetAttackable(
            HexCoord start,
            HashSet<HexCoord> arena,
            HashSet<HexCoord> enemyPositions)
        {
            var result = new HashSet<HexCoord>();
            foreach (var nb in GetNeighbors(start, arena))
            {
                if (enemyPositions.Contains(nb))
                    result.Add(nb);
            }
            return result;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static IEnumerable<HexCoord> GetNeighbors(HexCoord c, HashSet<HexCoord> arena)
        {
            for (int i = 0; i < 6; i++)
            {
                var nb = new HexCoord(c.Q + DQ[i], c.R + DR[i]);
                if (arena.Contains(nb)) yield return nb;
            }
        }

        private static float Heuristic(HexCoord a, HexCoord b)
        {
            int dq = a.Q - b.Q, dr = a.R - b.R, ds = -dq - dr;
            return (Mathf.Abs(dq) + Mathf.Abs(dr) + Mathf.Abs(ds)) * 0.5f;
        }

        private static List<HexCoord> Reconstruct(Dictionary<HexCoord, HexCoord> cameFrom, HexCoord current)
        {
            var path = new List<HexCoord>();
            while (cameFrom.ContainsKey(current))
            {
                path.Insert(0, current);
                current = cameFrom[current];
            }
            path.Insert(0, current);
            return path;
        }
    }
}
