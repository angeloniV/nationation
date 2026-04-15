using Godot;

namespace Natiolation.Map
{
    /// <summary>
    /// Celda hexagonal con renderizado procedural de calidad.
    ///
    ///  Técnicas usadas:
    ///   - Shading de vértices: gradiente top→bottom para efecto 3D
    ///   - Bevel border: arista iluminada arriba, sombra abajo
    ///   - AO edge: oscurecimiento sutil en el borde interior
    ///   - Terrain detail: árboles con sombra, picos con nieve, dunas, olas
    /// </summary>
    public partial class HexTile : Node2D
    {
        [Export] public int      Q    { get; set; }
        [Export] public int      R    { get; set; }
        [Export] public TileType Type { get; set; } = TileType.Plains;

        public bool TileVisible  { get; set; } = false;
        public bool WasExplored  { get; set; } = false;

        private Polygon2D _polygon    = null!;
        private Label     _coordLabel = null!;

        public const float HexSize = 40f;

        // ================================================================

        public override void _Ready()
        {
            _polygon    = GetNode<Polygon2D>("Polygon2D");
            _coordLabel = GetNode<Label>("CoordLabel");

            // Polygon2D del .tscn se oculta — dibujamos todo en _Draw()
            _polygon.Visible = false;
            QueueRedraw();
        }

        public void SetVisible(bool visible, bool explored)
        {
            TileVisible = visible;
            WasExplored = explored || WasExplored;
            QueueRedraw();
        }

        public void ShowCoords(bool show)
        {
            _coordLabel.Visible = show;
            if (show) _coordLabel.Text = $"{Q},{R}";
        }

        // ================================================================
        //  DRAW PRINCIPAL
        // ================================================================

        public override void _Draw()
        {
            var verts = HexVertices();

            // Tile no explorado → negro
            if (!TileVisible && !WasExplored)
            {
                DrawPolygon(verts, Paint(verts, new Color(0.04f, 0.04f, 0.06f)));
                return;
            }

            float fogMix = TileVisible ? 0f : 0.62f;   // cuánto mezclar con gris niebla
            var   fog    = new Color(0.28f, 0.30f, 0.34f);

            // --- Relleno base con GRADIENTE DE VÉRTICES (efecto 3D) ---
            var baseCol  = Type.MapColor();
            if (fogMix > 0f) baseCol = baseCol.Lerp(fog, fogMix);

            DrawPolygon(verts, ShadedGradient(verts, baseCol, 0.28f, 0.20f));

            // --- Ambient Occlusion en el borde interior ---
            var inset6 = InsetVerts(verts, 3.0f);
            DrawPolygon(inset6, Paint(inset6, new Color(0f, 0f, 0f, 0.06f)));

            // --- Detalle de terreno ---
            float a = TileVisible ? 1.0f : 0.40f;
            DrawTerrainDetail(baseCol, a);

            // --- Borde bevel ---
            DrawBevelBorder(verts, a);
        }

        // ================================================================
        //  GRADIENTE Y UTILIDADES DE PINTURA
        // ================================================================

        /// <summary>
        /// Devuelve un array de Color con gradiente top-lit por posición Y de vértice.
        /// lightTop: cuánto aclarar el vértice más alto (y negativo).
        /// darkBot:  cuánto oscurecer el vértice más bajo (y positivo).
        /// </summary>
        private static Color[] ShadedGradient(Vector2[] verts, Color base_,
                                               float lightTop, float darkBot)
        {
            var lightC = base_.Lightened(lightTop);
            var darkC  = base_.Darkened(darkBot);
            var cols   = new Color[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                // Y en coords locales: -HexSize (top) → +HexSize (bottom)
                float t   = (verts[i].Y + HexSize) / (2f * HexSize);  // 0=top 1=bot
                cols[i]   = lightC.Lerp(darkC, Mathf.Clamp(t, 0f, 1f));
            }
            return cols;
        }

        private static Color[] Paint(Vector2[] pts, Color c)
        {
            var cols = new Color[pts.Length];
            for (int i = 0; i < pts.Length; i++) cols[i] = c;
            return cols;
        }

        private static Color[] PaintN(int n, Color c)
        {
            var cols = new Color[n];
            for (int i = 0; i < n; i++) cols[i] = c;
            return cols;
        }

        private static Vector2[] InsetVerts(Vector2[] verts, float amount)
        {
            var inset = new Vector2[verts.Length];
            for (int i = 0; i < verts.Length; i++)
                inset[i] = verts[i] * ((HexSize - amount) / HexSize);
            return inset;
        }

        // ================================================================
        //  BORDE BEVEL
        // ================================================================

        private void DrawBevelBorder(Vector2[] verts, float a)
        {
            int n = verts.Length;
            for (int i = 0; i < n; i++)
            {
                var a1 = verts[i];
                var b1 = verts[(i + 1) % n];

                // Midpoint Y determina si es borde "alto" (iluminado) o "bajo" (sombra)
                float midY = (a1.Y + b1.Y) * 0.5f;
                float t    = (midY + HexSize) / (2f * HexSize); // 0=top 1=bot

                Color edgeCol;
                if (t < 0.35f)
                    edgeCol = new Color(1f, 1f, 1f, (0.40f - t) * a);   // arista iluminada
                else if (t > 0.65f)
                    edgeCol = new Color(0f, 0f, 0f, (t - 0.60f) * a * 0.8f); // arista sombra
                else
                    edgeCol = new Color(0f, 0f, 0f, 0.18f * a);         // borde neutro

                DrawLine(a1, b1, edgeCol, 1.5f, true);
            }
        }

        // ================================================================
        //  DETALLES POR TERRENO
        // ================================================================

        private void DrawTerrainDetail(Color baseCol, float a)
        {
            switch (Type)
            {
                case TileType.Grassland:
                case TileType.Plains:    DrawGrass(baseCol, a);    break;
                case TileType.Forest:    DrawForest(a);            break;
                case TileType.Hills:     DrawHills(baseCol, a);    break;
                case TileType.Mountains: DrawMountains(a);         break;
                case TileType.Desert:    DrawDesert(baseCol, a);   break;
                case TileType.Ocean:
                case TileType.Coast:     DrawWater(a);             break;
                case TileType.Tundra:    DrawTundra(a);            break;
                case TileType.Arctic:    DrawArctic(a);            break;
            }
        }

        // ── Pasto / Llanura ──────────────────────────────────────────────────
        private void DrawGrass(Color baseCol, float a)
        {
            bool isGrass  = (Type == TileType.Grassland);
            var  gc       = isGrass
                ? new Color(0.12f, 0.52f, 0.08f, 0.70f * a)
                : new Color(0.48f, 0.55f, 0.06f, 0.60f * a);
            var shadow    = new Color(0f, 0f, 0f, 0.10f * a);

            for (int i = -3; i <= 3; i++)
            {
                float x  = i * 9f;
                float yb = 7f + (i % 2 == 0 ? 0f : 6f);
                // Sombra de la hierba
                DrawLine(new Vector2(x, yb + 1f), new Vector2(x - 1f, yb - 5f), shadow, 1f, true);
                // Hierba
                DrawLine(new Vector2(x, yb),      new Vector2(x - 2f, yb - 7f),  gc, 1.5f, true);
                DrawLine(new Vector2(x, yb),      new Vector2(x + 2f, yb - 8f),  gc, 1.5f, true);
                DrawLine(new Vector2(x, yb - 2f), new Vector2(x,      yb - 10f), gc, 1.2f, true);
            }
        }

        // ── Bosque ───────────────────────────────────────────────────────────
        private void DrawForest(float a)
        {
            var ground  = new Color(0.18f, 0.38f, 0.06f, 0.50f * a);   // suelo oscuro
            var trunk   = new Color(0.30f, 0.16f, 0.04f, 0.95f * a);
            var crown1  = new Color(0.04f, 0.28f, 0.06f, 0.95f * a);   // copa oscura
            var crown2  = new Color(0.06f, 0.42f, 0.10f, 0.90f * a);   // copa media
            var crown3  = new Color(0.08f, 0.55f, 0.14f, 0.80f * a);   // copa clara
            var treeShadow = new Color(0f, 0f, 0f, 0.18f * a);

            Vector2[] centers = { new(-11f, 8f), new(11f, 8f), new(0f, -2f) };
            float[]   heights = { 15f, 15f, 18f };
            float[]   widths  = { 9f,  9f,  11f };

            for (int t = 0; t < 3; t++)
            {
                var   c  = centers[t];
                float h  = heights[t];
                float w  = widths[t];

                // Sombra elíptica en el suelo
                DrawEllipse(c + new Vector2(3f, 5f), w * 0.8f, 3f, treeShadow);

                // Tronco
                DrawLine(c + new Vector2(0f, 5f), c + new Vector2(0f, 14f), trunk, 3.5f, true);

                // Copa: 3 capas de triángulos (efecto de profundidad)
                DrawTriangle(c + new Vector2(0f, -h),
                             c + new Vector2(-w * 0.90f, -h * 0.30f),
                             c + new Vector2( w * 0.90f, -h * 0.30f), crown1);

                DrawTriangle(c + new Vector2(0f, -h * 0.65f),
                             c + new Vector2(-w, h * 0.05f),
                             c + new Vector2( w, h * 0.05f), crown2);

                DrawTriangle(c + new Vector2(0f, -h * 0.35f),
                             c + new Vector2(-w * 0.80f, h * 0.38f),
                             c + new Vector2( w * 0.80f, h * 0.38f), crown3);

                // Brillo en la punta del árbol
                DrawCircle(c + new Vector2(-1.5f, -h + 2f), 2.5f,
                           new Color(0.15f, 0.70f, 0.20f, 0.40f * a));
            }
        }

        // ── Colinas ──────────────────────────────────────────────────────────
        private void DrawHills(Color baseCol, float a)
        {
            var dark = baseCol.Darkened(0.28f);
            dark.A   = 0.85f * a;
            var mid  = baseCol.Darkened(0.12f);
            mid.A    = 0.80f * a;
            var hi   = baseCol.Lightened(0.25f);
            hi.A     = 0.55f * a;
            var grass= new Color(0.25f, 0.55f, 0.10f, 0.50f * a);

            DrawHillShape(new Vector2(-12f, 10f), 12f, 9f,  dark, mid, hi);
            DrawHillShape(new Vector2( 12f, 10f), 12f, 9f,  dark, mid, hi);
            DrawHillShape(new Vector2(  0f,  3f), 15f, 12f, dark, mid, hi);

            // Linea de pasto en la cresta de la colina central
            DrawArc(new Vector2(0f, 3f), 15f, -Mathf.Pi * 0.95f, -Mathf.Pi * 0.05f, 16,
                    grass, 2f, true);
        }

        private void DrawHillShape(Vector2 pos, float rw, float rh,
                                   Color dark, Color mid, Color hi)
        {
            // Cara de la colina (gradiente interno con 3 poligonos concentricos)
            var full = SemiEllipse(pos, rw, rh);
            var half = SemiEllipse(pos, rw * 0.65f, rh * 0.65f);

            DrawPolygon(full, Paint(full, dark));
            DrawPolygon(half, Paint(half, mid));

            // Brillo en la cresta
            DrawLine(full[3], full[8], hi, 2f, true);
        }

        private static Vector2[] SemiEllipse(Vector2 pos, float rw, float rh, int segs = 12)
        {
            var pts = new Vector2[segs];
            for (int i = 0; i < segs; i++)
            {
                float ang = Mathf.Pi * i / (segs - 1f);
                pts[i]    = pos + new Vector2(-Mathf.Cos(ang) * rw, -Mathf.Sin(ang) * rh);
            }
            return pts;
        }

        // ── Montañas ─────────────────────────────────────────────────────────
        private void DrawMountains(float a)
        {
            // Sombra en el suelo
            DrawEllipse(new Vector2(0f, 12f), 28f, 5f, new Color(0f, 0f, 0f, 0.18f * a));

            var rockDark = new Color(0.25f, 0.25f, 0.28f, 0.95f * a);
            var rockMid  = new Color(0.44f, 0.43f, 0.46f, 0.95f * a);
            var rockLit  = new Color(0.60f, 0.59f, 0.62f, 0.90f * a);
            var snow     = new Color(0.96f, 0.97f, 1.00f, 0.97f * a);
            var snowLine = new Color(0.80f, 0.84f, 0.90f, 0.70f * a);

            DrawPeak(new Vector2(-13f, 9f), 10f, 18f, rockDark, rockMid, rockLit, snow, snowLine);
            DrawPeak(new Vector2( 13f, 9f), 10f, 18f, rockDark, rockMid, rockLit, snow, snowLine);
            DrawPeak(new Vector2(  0f, 5f), 13f, 24f, rockDark, rockMid, rockLit, snow, snowLine);
        }

        private void DrawPeak(Vector2 base_, float hw, float h,
                              Color dark, Color mid, Color lit,
                              Color snow, Color snowLine)
        {
            // Cara izquierda oscura
            DrawPolygon(new[] {
                base_ + new Vector2(-hw,   0f),
                base_ + new Vector2(  0f, -h),
                base_ + new Vector2(  0f,  0f),
            }, new[] { dark, dark, dark });

            // Cara derecha iluminada
            DrawPolygon(new[] {
                base_ + new Vector2(hw,   0f),
                base_ + new Vector2( 0f, -h),
                base_ + new Vector2( 0f,  0f),
            }, new[] { lit, mid, lit });

            // Linea de nieve (borde inferior de la nieve)
            DrawLine(base_ + new Vector2(-hw * 0.55f, -h * 0.58f),
                     base_ + new Vector2( hw * 0.55f, -h * 0.58f),
                     snowLine, 1.5f, true);

            // Nieve en la cima
            DrawPolygon(new[] {
                base_ + new Vector2(     0f,  -h),
                base_ + new Vector2(-hw * 0.5f, -h * 0.60f),
                base_ + new Vector2( hw * 0.5f, -h * 0.60f),
            }, new[] { snow, snow, snow });

            // Brillo en la cima
            DrawCircle(base_ + new Vector2(-1f, -h + 1.5f), 2f,
                       new Color(1f, 1f, 1f, 0.6f));
        }

        // ── Desierto ─────────────────────────────────────────────────────────
        private void DrawDesert(Color baseCol, float a)
        {
            var dune   = baseCol.Lightened(0.15f);  dune.A  = 0.70f * a;
            var shadow = baseCol.Darkened(0.25f);   shadow.A = 0.65f * a;
            var hot    = new Color(0.95f, 0.75f, 0.15f, 0.22f * a); // tinte calor

            // Dunas con shading interno
            for (int i = -1; i <= 1; i++)
            {
                float cx = i * 14f;
                float cy = i * 4f + 5f;
                // Sombra de la duna
                DrawArc(new Vector2(cx + 1f, cy + 14f), 14f,
                        -Mathf.Pi * 0.85f, -Mathf.Pi * 0.15f, 14, shadow, 5f, true);
                // Cresta de la duna
                DrawArc(new Vector2(cx, cy + 12f), 14f,
                        -Mathf.Pi * 0.82f, -Mathf.Pi * 0.18f, 14, dune, 2.5f, true);
            }

            // Textura de arena: puntos dispersos
            float[] xs = { -18f, -9f, 0f, 9f, 18f, -13f, 4f, -4f, 13f };
            float[] ys = {  -8f, -5f,  3f, -7f, 2f,   5f, -2f,  8f, -4f };
            for (int i = 0; i < xs.Length; i++)
                DrawCircle(new Vector2(xs[i], ys[i]), 1.3f, shadow);

            // Tinte de calor superpuesto
            DrawCircle(new Vector2(0f, 0f), 18f, hot);
        }

        // ── Agua ─────────────────────────────────────────────────────────────
        private void DrawWater(float a)
        {
            bool coast  = (Type == TileType.Coast);
            var  deep   = coast
                ? new Color(0.22f, 0.54f, 0.80f, 0.55f * a)
                : new Color(0.04f, 0.25f, 0.60f, 0.45f * a);
            var  wave   = coast
                ? new Color(0.55f, 0.82f, 0.98f, 0.60f * a)
                : new Color(0.20f, 0.55f, 0.82f, 0.55f * a);
            var  foam   = new Color(0.88f, 0.95f, 1.00f, 0.45f * a);

            // Gradiente de profundidad: circulo mas claro en el centro
            var verts = HexVertices();
            DrawPolygon(InsetVerts(verts, 10f), Paint(InsetVerts(verts, 10f), deep));

            // Olas con forma de arco mas suave
            for (int i = -2; i <= 2; i++)
            {
                float y  = i * 9f;
                float xo = (i % 2 == 0) ? 6f : -6f;
                float xo2= -xo * 0.4f;

                DrawLine(new Vector2(-16f + xo,  y),      new Vector2(-6f + xo,  y), wave, 2.2f, true);
                DrawLine(new Vector2(  4f + xo2, y - 1f), new Vector2(16f + xo2, y - 1f), wave, 1.8f, true);

                if (coast)
                    DrawLine(new Vector2(-14f + xo, y + 2f), new Vector2(-8f + xo, y + 2f), foam, 1.2f, true);
            }
        }

        // ── Tundra ───────────────────────────────────────────────────────────
        private void DrawTundra(float a)
        {
            var ic   = new Color(0.75f, 0.85f, 0.88f, 0.65f * a);
            var sc   = new Color(0.92f, 0.97f, 1.00f, 0.60f * a);
            var dead = new Color(0.38f, 0.30f, 0.20f, 0.55f * a); // rama seca

            for (int i = -2; i <= 2; i++)
            {
                float x = i * 12f;
                float y = (i % 2 == 0) ? -5f : 6f;
                DrawCircle(new Vector2(x, y), 7f,  ic);
                DrawCircle(new Vector2(x, y), 4f,  sc);
                DrawCircle(new Vector2(x, y), 1.5f, new Color(0.6f, 0.7f, 0.75f, 0.4f * a));
            }
            // Ramas muertas
            DrawLine(new Vector2(-8f, 3f), new Vector2(-4f, -6f), dead, 1.5f, true);
            DrawLine(new Vector2(-4f, -6f), new Vector2(-1f, -2f), dead, 1.2f, true);
            DrawLine(new Vector2( 8f, 3f), new Vector2( 5f, -5f), dead, 1.5f, true);
        }

        // ── Ártico ───────────────────────────────────────────────────────────
        private void DrawArctic(float a)
        {
            var ice   = new Color(0.84f, 0.93f, 1.00f, 0.72f * a);
            var crack = new Color(0.50f, 0.72f, 0.88f, 0.65f * a);
            var glare = new Color(1.00f, 1.00f, 1.00f, 0.40f * a);

            // Bloques de hielo: polígonos ligeramente inclinados
            DrawPolygon(new[] {
                new Vector2(-18f, -2f), new Vector2(-5f, -8f),
                new Vector2( 2f, -3f), new Vector2(-8f,  5f),
            }, new[] { ice, ice, ice, ice });
            DrawPolygon(new[] {
                new Vector2(  5f, -6f), new Vector2(18f, -3f),
                new Vector2(16f,  6f), new Vector2(  3f,  4f),
            }, new[] { ice, ice, ice, ice });
            DrawPolygon(new[] {
                new Vector2(-10f, 6f), new Vector2(10f,  4f),
                new Vector2( 8f, 14f), new Vector2(-8f, 14f),
            }, new[] { ice, ice, ice, ice });

            // Grietas entre bloques
            DrawLine(new Vector2(-5f, -8f), new Vector2( 5f, -6f), crack, 1.5f, true);
            DrawLine(new Vector2(-8f,  5f), new Vector2( 3f,  4f), crack, 1.5f, true);
            DrawLine(new Vector2(-8f,  5f), new Vector2(-10f, 6f), crack, 1.2f, true);
            DrawLine(new Vector2( 3f,  4f), new Vector2( 10f, 4f), crack, 1.2f, true);

            // Brillo de nieve
            DrawCircle(new Vector2(-10f, -4f), 4f, glare);
            DrawCircle(new Vector2( 11f, -4f), 3f, glare);
        }

        // ================================================================
        //  HELPERS DE DIBUJO
        // ================================================================

        private void DrawTriangle(Vector2 a, Vector2 b, Vector2 c, Color col)
        {
            DrawPolygon(new[] { a, b, c }, new[] { col, col, col });
        }

        private void DrawEllipse(Vector2 center, float rx, float ry, Color col, int segs = 16)
        {
            var pts = new Vector2[segs];
            for (int i = 0; i < segs; i++)
            {
                float ang = Mathf.Tau * i / segs;
                pts[i]    = center + new Vector2(Mathf.Cos(ang) * rx, Mathf.Sin(ang) * ry);
            }
            DrawPolygon(pts, Paint(pts, col));
        }

        // ================================================================
        //  COORDENADAS
        // ================================================================

        public static Vector2 AxialToWorld(int q, int r)
        {
            float x = HexSize * MathF.Sqrt(3f) * (q + r / 2f);
            float y = HexSize * 1.5f * r;
            return new Vector2(x, y);
        }

        public static Vector2[] HexVertices()
        {
            var verts = new Vector2[6];
            for (int i = 0; i < 6; i++)
            {
                float angle = MathF.PI / 180f * (60f * i + 30f);
                verts[i] = new Vector2(HexSize * MathF.Cos(angle), HexSize * MathF.Sin(angle));
            }
            return verts;
        }
    }
}
