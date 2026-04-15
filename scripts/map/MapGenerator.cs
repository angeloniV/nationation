using Godot;
using System.Collections.Generic;

namespace Natiolation.Map
{
    /// <summary>
    /// Genera el mapa usando Perlin Noise en capas (continentes + elevacion + temperatura).
    /// </summary>
    public static class MapGenerator
    {
        // Direcciones axiales hexagonales (compartidas con el resto del sistema)
        private static readonly int[] DQ = {  1, -1,  0,  0,  1, -1 };
        private static readonly int[] DR = {  0,  0,  1, -1, -1,  1 };

        public static TileType[,] Generate(int width, int height, int seed,
                                            out HashSet<(int q, int r, int dir)> riverEdges)
        {
            var map = new TileType[width, height];

            var noiseElevation = new FastNoiseLite();
            noiseElevation.Seed = seed;
            noiseElevation.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
            noiseElevation.Frequency = 0.025f;
            noiseElevation.FractalOctaves = 5;
            noiseElevation.FractalLacunarity = 2.0f;
            noiseElevation.FractalGain = 0.5f;

            var noiseMoisture = new FastNoiseLite();
            noiseMoisture.Seed = seed + 1000;
            noiseMoisture.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
            noiseMoisture.Frequency = 0.04f;
            noiseMoisture.FractalOctaves = 3;

            var noiseTemperature = new FastNoiseLite();
            noiseTemperature.Seed = seed + 2000;
            noiseTemperature.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
            noiseTemperature.Frequency = 0.02f;

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    float elevation  = (noiseElevation.GetNoise2D(x, y) + 1f) / 2f;   // 0..1
                    float moisture   = (noiseMoisture.GetNoise2D(x, y) + 1f) / 2f;
                    float tempNoise  = (noiseTemperature.GetNoise2D(x, y) + 1f) / 2f;

                    // Temperatura influenciada por latitud (polo = frio)
                    float latitudeFactor = 1f - (2f * Mathf.Abs(y - height / 2f) / height);
                    float temperature = Mathf.Clamp(latitudeFactor * 0.7f + tempNoise * 0.3f, 0f, 1f);

                    map[x, y] = ClassifyTile(elevation, moisture, temperature);
                }
            }

            // Segunda pasada: tiles de costa (ocean adyacente a tierra)
            ApplyCoastTiles(map, width, height);

            // Tercera pasada: ríos desde montañas hacia el océano
            riverEdges = new HashSet<(int, int, int)>();
            GenerateRivers(map, width, height, seed, riverEdges);

            return map;
        }

        private static TileType ClassifyTile(float elevation, float moisture, float temperature)
        {
            if (elevation < 0.38f) return TileType.Ocean;
            if (elevation > 0.85f)
            {
                if (temperature < 0.25f) return TileType.Arctic;
                return TileType.Mountains;
            }
            if (elevation > 0.68f)
            {
                if (temperature < 0.25f) return TileType.Tundra;
                return TileType.Hills;
            }

            // Tierra firme — clasificar por temperatura y humedad
            if (temperature < 0.20f) return TileType.Arctic;
            if (temperature < 0.35f) return TileType.Tundra;

            if (temperature > 0.70f && moisture < 0.35f) return TileType.Desert;

            if (moisture > 0.60f && temperature > 0.40f) return TileType.Forest;
            if (moisture > 0.45f) return TileType.Grassland;

            return TileType.Plains;
        }

        private static void ApplyCoastTiles(TileType[,] map, int width, int height)
        {
            var toConvert = new List<(int, int)>();

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (map[x, y] != TileType.Ocean) continue;
                    if (HasLandNeighbor(map, x, y, width, height))
                        toConvert.Add((x, y));
                }
            }

            foreach (var (x, y) in toConvert)
                map[x, y] = TileType.Coast;
        }

        private static bool HasLandNeighbor(TileType[,] map, int x, int y, int w, int h)
        {
            for (int i = 0; i < 6; i++)
            {
                int nx = x + DQ[i];
                int ny = y + DR[i];
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                if (map[nx, ny] != TileType.Ocean && map[nx, ny] != TileType.Coast)
                    return true;
            }
            return false;
        }

        // ================================================================
        //  GENERACIÓN DE RÍOS
        // ================================================================

        private static void GenerateRivers(TileType[,] map, int width, int height,
                                            int seed, HashSet<(int, int, int)> riverEdges)
        {
            var rng = new System.Random(seed ^ 0xF00D);

            // Recopilar fuentes potenciales (montañas o colinas alejadas de la costa)
            var sources = new List<(int q, int r)>();
            for (int q = 0; q < width; q++)
                for (int r = 0; r < height; r++)
                    if (map[q, r] == TileType.Mountains)
                        sources.Add((q, r));

            // Mezclar para variedad
            for (int i = sources.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (sources[i], sources[j]) = (sources[j], sources[i]);
            }

            int maxRivers = Mathf.Min(14, sources.Count);

            for (int ri = 0; ri < maxRivers; ri++)
            {
                var (sq, sr) = sources[ri];
                int q = sq, r = sr;
                var visited = new HashSet<(int, int)>();

                for (int step = 0; step < 60; step++)
                {
                    visited.Add((q, r));
                    var current = map[q, r];
                    if (current == TileType.Ocean || current == TileType.Coast) break;

                    // Mezclar direcciones para desempate aleatorio
                    int[] dirs = { 0, 1, 2, 3, 4, 5 };
                    for (int i = 5; i > 0; i--)
                    {
                        int j = rng.Next(i + 1);
                        (dirs[i], dirs[j]) = (dirs[j], dirs[i]);
                    }

                    // Primer intento: vecino con elevación estrictamente menor
                    float curElev  = TileElevation(current);
                    float bestElev = curElev;
                    int   bestDir  = -1;

                    foreach (int d in dirs)
                    {
                        int nq = q + DQ[d], nr = r + DR[d];
                        if (nq < 0 || nq >= width || nr < 0 || nr >= height) continue;
                        if (visited.Contains((nq, nr))) continue;

                        float ne = TileElevation(map[nq, nr]);
                        if (ne < bestElev) { bestElev = ne; bestDir = d; }
                    }

                    // Segundo intento: si estamos en meseta, avanzar en lateral
                    // (permite que el río atraviese terreno plano hasta el mar)
                    if (bestDir == -1)
                    {
                        foreach (int d in dirs)
                        {
                            int nq = q + DQ[d], nr = r + DR[d];
                            if (nq < 0 || nq >= width || nr < 0 || nr >= height) continue;
                            if (visited.Contains((nq, nr))) continue;
                            float ne = TileElevation(map[nq, nr]);
                            if (ne <= curElev && map[nq, nr] != TileType.Mountains)
                            {
                                bestDir = d;
                                break;
                            }
                        }
                    }

                    if (bestDir == -1) break;   // atrapado sin salida → detener

                    int nextQ = q + DQ[bestDir], nextR = r + DR[bestDir];

                    // Registrar el borde compartido (ambos lados)
                    riverEdges.Add((q,     r,     bestDir));
                    riverEdges.Add((nextQ, nextR, bestDir ^ 1));

                    q = nextQ;
                    r = nextR;
                }
            }
        }

        private static float TileElevation(TileType t) => t switch
        {
            TileType.Mountains => 4.0f,
            TileType.Hills     => 2.2f,
            TileType.Arctic    => 1.1f,
            TileType.Tundra    => 0.9f,
            TileType.Forest    => 0.85f,
            TileType.Grassland => 0.80f,
            TileType.Plains    => 0.75f,
            TileType.Desert    => 0.75f,
            TileType.Coast     => 0.4f,
            TileType.Ocean     => 0.2f,
            _                  => 0.75f
        };
    }
}
