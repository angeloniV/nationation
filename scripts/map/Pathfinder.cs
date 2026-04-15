using System;
using System.Collections.Generic;

namespace Natiolation.Map
{
    /// <summary>
    /// A* sobre el grid hexagonal axial.
    /// </summary>
    public static class Pathfinder
    {
        private static readonly int[] DQ = {  1, -1,  0,  0,  1, -1 };
        private static readonly int[] DR = {  0,  0,  1, -1, -1,  1 };

        /// <summary>
        /// Encuentra el camino de menor costo entre dos hexes.
        /// Devuelve null si no hay camino o el destino es infranqueable.
        /// El camino incluye el hex de inicio.
        /// </summary>
        public static List<HexCoord>? FindPath(
            MapManager map,
            int startQ, int startR,
            int endQ,   int endR,
            float moveBudget = float.MaxValue)
        {
            if (startQ == endQ && startR == endR)
                return new List<HexCoord> { new(startQ, startR) };

            var endType = map.GetTileType(endQ, endR);
            if (endType == null || !IsPassable(endType.Value)) return null;

            var openSet  = new PriorityQueue<HexCoord, float>();
            var cameFrom = new Dictionary<HexCoord, HexCoord>();
            var gScore   = new Dictionary<HexCoord, float>();

            var start = new HexCoord(startQ, startR);
            var end   = new HexCoord(endQ,   endR);

            gScore[start] = 0f;
            openSet.Enqueue(start, Heuristic(start, end));

            while (openSet.Count > 0)
            {
                var current = openSet.Dequeue();
                if (current == end)
                    return Reconstruct(cameFrom, current);

                foreach (var nb in GetPassableNeighbors(map, current))
                {
                    float cost  = map.GetEffectiveCost(nb.Q, nb.R);
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

        /// <summary>
        /// Calcula todos los hexes alcanzables dentro de un presupuesto de movimiento.
        /// </summary>
        public static HashSet<HexCoord> GetReachable(MapManager map, int q, int r, float budget)
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

                foreach (var nb in GetPassableNeighbors(map, current))
                {
                    float newCost = cur + map.GetEffectiveCost(nb.Q, nb.R);
                    if (newCost > budget) continue;

                    if (!costs.TryGetValue(nb, out float prev) || newCost < prev)
                    {
                        costs[nb] = newCost;
                        frontier.Enqueue(nb, newCost);
                        reachable.Add(nb);
                    }
                }
            }

            return reachable;
        }

        public static bool IsPassable(TileType t) =>
            t != TileType.Ocean && t != TileType.Coast;

        private static IEnumerable<HexCoord> GetPassableNeighbors(MapManager map, HexCoord h)
        {
            for (int i = 0; i < 6; i++)
            {
                int nq = h.Q + DQ[i], nr = h.R + DR[i];
                var t = map.GetTileType(nq, nr);
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
