namespace Natiolation.Map
{
    /// <summary>
    /// Snapshot inmutable de los datos de mapa necesarios para pathfinding.
    ///
    /// Se construye en el hilo principal mediante <see cref="MapManager.TakeSnapshot"/>;
    /// una vez creado, es seguro pasarlo a hilos de fondo (Task.Run).
    ///
    /// • _types  — referencia al array original (los tipos de tile no cambian tras generación)
    /// • _costs  — copia de los costos efectivos en el momento del snapshot (captura caminos/roads)
    /// </summary>
    public sealed class MapSnapshot
    {
        public readonly int Width;
        public readonly int Height;

        private readonly TileType[,] _types;
        private readonly float[,]    _costs;

        internal MapSnapshot(int width, int height, TileType[,] types, float[,] costs)
        {
            Width  = width;
            Height = height;
            _types = types;
            _costs = costs;
        }

        public TileType? GetTileType(int q, int r)
        {
            if ((uint)q >= (uint)Width || (uint)r >= (uint)Height) return null;
            return _types[q, r];
        }

        public float GetEffectiveCost(int q, int r)
        {
            if ((uint)q >= (uint)Width || (uint)r >= (uint)Height) return 1f;
            return _costs[q, r];
        }
    }
}
