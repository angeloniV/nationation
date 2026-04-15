using Godot;
using System;

namespace Natiolation.Map
{
    /// <summary>
    /// Renderiza todo el terreno como un único mesh continuo + shader de biomas.
    ///
    /// Correcciones clave:
    ///   • tile_type_tex usa Image.Format.R8 (mejor compatibilidad que Rf).
    ///   • fog updates usan dirty-flag: muchas llamadas a UpdateFog() → una sola
    ///     subida de textura por frame en _Process().
    ///   • La malla se construye en world-space; el shader usa MODEL_MATRIX para
    ///     pasar v_world_pos como varying al fragment.
    /// </summary>
    public partial class TerrainRenderer : Node3D
    {
        public static TerrainRenderer? Instance { get; private set; }

        private ShaderMaterial _mat      = null!;
        private Image          _typeImg  = null!;
        private ImageTexture   _typeTex  = null!;
        private Image          _fogImg   = null!;
        private ImageTexture   _fogTex   = null!;

        private int    _mapW, _mapH;
        private double _waterTime = 0.0;
        private bool   _fogDirty  = false;

        // 2 vértices por unidad de mundo (con HexSize=4 → buena cobertura)
        private const float MeshStep = 2.0f;

        // ================================================================
        //  INICIALIZACIÓN
        // ================================================================

        public void Init(TileType[,] types, int w, int h)
        {
            Instance = this;
            _mapW = w;
            _mapH = h;

            BuildTypeTexture(types, w, h);
            BuildFogTexture(w, h);
            BuildTerrainMesh(types, w, h);

            GD.Print("[TerrainRenderer] Init completado.");
        }

        // ================================================================
        //  TEXTURAS
        // ================================================================

        private void BuildTypeTexture(TileType[,] types, int w, int h)
        {
            // R8: cada pixel almacena el tipo de tile normalizado 0–255
            // 0 → Ocean (0/9=0), 9 → Arctic (9/9=255)
            _typeImg = Image.CreateEmpty(w, h, false, Image.Format.R8);
            for (int q = 0; q < w; q++)
                for (int r = 0; r < h; r++)
                {
                    int   ty  = (int)types[q, r];
                    float val = ty / 9.0f;                         // 0.0–1.0
                    _typeImg.SetPixel(q, r, new Color(val, 0f, 0f, 1f));
                }
            _typeTex = ImageTexture.CreateFromImage(_typeImg);
            GD.Print($"[TerrainRenderer] Textura tipo: {w}×{h} R8");
        }

        private void BuildFogTexture(int w, int h)
        {
            // Rg8: R=visible (0/1), G=explorado (0/1)
            _fogImg = Image.CreateEmpty(w, h, false, Image.Format.Rg8);
            _fogImg.Fill(new Color(0f, 0f, 0f, 1f));   // todo inexplorado
            _fogTex = ImageTexture.CreateFromImage(_fogImg);
        }

        // ================================================================
        //  MALLA
        // ================================================================

        private void BuildTerrainMesh(TileType[,] types, int w, int h)
        {
            // Bounding box del mapa en world space
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            for (int q = 0; q < w; q++)
                for (int r = 0; r < h; r++)
                {
                    var p = HexTile3D.AxialToWorld(q, r);
                    float pad = HexTile3D.HexSize * 1.1f;
                    minX = MathF.Min(minX, p.X - pad);
                    maxX = MathF.Max(maxX, p.X + pad);
                    minZ = MathF.Min(minZ, p.Z - pad);
                    maxZ = MathF.Max(maxZ, p.Z + pad);
                }

            int cols = (int)MathF.Ceiling((maxX - minX) / MeshStep) + 1;
            int rows = (int)MathF.Ceiling((maxZ - minZ) / MeshStep) + 1;

            // Generar vértices con altura interpolada por IDW
            var verts = new Vector3[cols, rows];
            for (int ci = 0; ci < cols; ci++)
                for (int ri = 0; ri < rows; ri++)
                {
                    float x = minX + ci * MeshStep;
                    float z = minZ + ri * MeshStep;
                    float y = SampleHeight(types, w, h, x, z);
                    verts[ci, ri] = new Vector3(x, y, z);
                }

            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);

            for (int ci = 0; ci < cols - 1; ci++)
                for (int ri = 0; ri < rows - 1; ri++)
                {
                    var v00 = verts[ci,     ri    ];
                    var v10 = verts[ci + 1, ri    ];
                    var v01 = verts[ci,     ri + 1];
                    var v11 = verts[ci + 1, ri + 1];

                    var n0 = FaceNormal(v00, v10, v01);
                    var n1 = FaceNormal(v11, v01, v10);

                    st.SetNormal(n0); st.AddVertex(v00);
                    st.SetNormal(n0); st.AddVertex(v10);
                    st.SetNormal(n0); st.AddVertex(v01);

                    st.SetNormal(n1); st.AddVertex(v11);
                    st.SetNormal(n1); st.AddVertex(v01);
                    st.SetNormal(n1); st.AddVertex(v10);
                }

            var mesh = st.Commit();

            // Shader material
            _mat = new ShaderMaterial();
            var shader = GD.Load<Shader>("res://shaders/terrain.gdshader");
            if (shader == null)
            {
                GD.PrintErr("[TerrainRenderer] ¡No se pudo cargar terrain.gdshader!");
                return;
            }
            _mat.Shader = shader;
            _mat.SetShaderParameter("tile_type_tex", _typeTex);
            _mat.SetShaderParameter("tile_fog_tex",  _fogTex);
            _mat.SetShaderParameter("map_size",      new Vector2(w, h));
            _mat.SetShaderParameter("hex_size",      HexTile3D.HexSize);
            _mat.SetShaderParameter("show_grid",     false);
            _mat.SetShaderParameter("grid_alpha",    0.22f);
            _mat.SetShaderParameter("water_time",    0.0f);

            var mi = new MeshInstance3D
            {
                Name             = "TerrainMesh",
                Mesh             = mesh,
                MaterialOverride = _mat,
            };
            AddChild(mi);

            GD.Print($"[TerrainRenderer] Malla: {cols}×{rows} vértices ({(cols-1)*(rows-1)*2} triángulos)");
        }

        // ── Altura en punto (x,z) por IDW entre tiles cercanos ───────────
        private static float SampleHeight(TileType[,] types, int w, int h, float x, float z)
        {
            float s3 = MathF.Sqrt(3f);
            // Axial round
            float fq = (s3 / 3f * x - z / 3f) / HexTile3D.HexSize;
            float fr = (2f / 3f * z) / HexTile3D.HexSize;
            float fs = -fq - fr;
            float rq = MathF.Round(fq), rr = MathF.Round(fr), rs = MathF.Round(fs);
            if (MathF.Abs(rq - fq) > MathF.Abs(rr - fr) && MathF.Abs(rq - fq) > MathF.Abs(rs - fs))
                rq = -rr - rs;
            else if (MathF.Abs(rr - fr) > MathF.Abs(rs - fs))
                rr = -rq - rs;
            int cq = (int)rq, cr = (int)rr;

            // IDW entre centro + 6 vecinos
            int[]  dq = {  0,  1, -1,  0,  0,  1, -1 };
            int[]  dr = {  0,  0,  0,  1, -1, -1,  1 };
            float heightSum = 0f, weightSum = 0f;

            for (int i = 0; i < 7; i++)
            {
                int nq = cq + dq[i], nr = cr + dr[i];
                if (nq < 0 || nq >= w || nr < 0 || nr >= h) continue;

                float cx = HexTile3D.HexSize * s3 * (nq + nr * 0.5f);
                float cz = HexTile3D.HexSize * 1.5f * nr;
                float dx = x - cx, dz = z - cz;
                float dist2 = dx * dx + dz * dz;
                float wt = 1f / MathF.Max(dist2, 0.0001f);

                heightSum += HexTile3D.GetHeight(types[nq, nr]) * wt;
                weightSum += wt;
            }
            return weightSum > 0f ? heightSum / weightSum : 0.75f;
        }

        private static Vector3 FaceNormal(Vector3 a, Vector3 b, Vector3 c)
        {
            var n = (b - a).Cross(c - a).Normalized();
            return n.Y < 0f ? -n : n;
        }

        // ================================================================
        //  API PÚBLICA
        // ================================================================

        /// <summary>
        /// Actualiza el fog of war de un tile.
        /// No sube la textura inmediatamente — se sube en _Process() con dirty-flag.
        /// </summary>
        public void UpdateFog(int q, int r, bool visible, bool explored)
        {
            if (_fogImg == null || q < 0 || r < 0 || q >= _mapW || r >= _mapH) return;
            _fogImg.SetPixel(q, r, new Color(visible ? 1f : 0f, explored ? 1f : 0f, 0f, 1f));
            _fogDirty = true;
        }

        /// <summary>Activa / desactiva la grilla hex (solo durante selección de movimiento).</summary>
        public void ShowGrid(bool show)
            => _mat?.SetShaderParameter("show_grid", show);

        // ================================================================
        //  PROCESO
        // ================================================================

        public override void _Process(double delta)
        {
            if (_mat == null) return;

            // Subir textura de fog una sola vez por frame si hubo cambios
            if (_fogDirty)
            {
                _fogTex.Update(_fogImg);
                _fogDirty = false;
            }

            // Animación de agua
            _waterTime += delta;
            _mat.SetShaderParameter("water_time", (float)_waterTime);
        }
    }
}
