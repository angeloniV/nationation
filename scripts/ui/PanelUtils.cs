using Godot;
using System;

namespace Natiolation.UI
{
    /// <summary>
    /// Utilidades de UI compartidas entre paneles.
    /// </summary>
    public static class PanelUtils
    {
        /// <summary>
        /// Añade un botón ✕ flotante anclado a la esquina superior derecha del panel.
        /// Spec: Anchor=TopRight, OffsetRight=-10, OffsetTop=10, tamaño 32×32.
        ///
        /// El botón se dibuja sobre cualquier contenido del panel (Z-order superior)
        /// y no interfiere con el layout interno.
        /// </summary>
        /// <param name="parent">Panel al que se añade el botón (debe ser Control).</param>
        /// <param name="onClose">Acción ejecutada al pulsar el botón.</param>
        /// <returns>El botón creado (por si se necesita stilizar más).</returns>
        public static Button AddCloseButton(Control parent, Action onClose)
        {
            const float BtnSize = 32f;
            const float Margin  = 10f;

            var btn = new Button { Text = "✕" };

            // Estilo rojo oscuro — consistente con TechTreePanel
            var sbNorm = new StyleBoxFlat
            {
                BgColor               = new Color(0.40f, 0.07f, 0.06f),
                CornerRadiusTopLeft   = 4,
                CornerRadiusTopRight  = 4,
                CornerRadiusBottomLeft  = 4,
                CornerRadiusBottomRight = 4,
            };
            var sbHov = new StyleBoxFlat
            {
                BgColor               = new Color(0.62f, 0.12f, 0.10f),
                CornerRadiusTopLeft   = 4,
                CornerRadiusTopRight  = 4,
                CornerRadiusBottomLeft  = 4,
                CornerRadiusBottomRight = 4,
            };
            btn.AddThemeStyleboxOverride("normal",   sbNorm);
            btn.AddThemeStyleboxOverride("hover",    sbHov);
            btn.AddThemeStyleboxOverride("pressed",  sbNorm);
            btn.AddThemeStyleboxOverride("focus",    sbNorm);
            btn.AddThemeColorOverride("font_color",        Colors.White);
            btn.AddThemeColorOverride("font_hover_color",  Colors.White);
            btn.AddThemeFontSizeOverride("font_size", 16);

            // Tamaño explícito
            btn.CustomMinimumSize = new Vector2(BtnSize, BtnSize);

            // Anclar a la esquina Top-Right del parent
            // AnchorLeft=1, AnchorRight=1, AnchorTop=0, AnchorBottom=0
            // OffsetRight = -Margin          → borde derecho del btn = parent.Width - Margin
            // OffsetLeft  = -(Margin+BtnSize)→ borde izquierdo
            // OffsetTop   = Margin
            // OffsetBottom= Margin + BtnSize
            btn.SetAnchorsPreset(Control.LayoutPreset.TopRight);
            btn.OffsetRight  = -Margin;
            btn.OffsetLeft   = -(Margin + BtnSize);
            btn.OffsetTop    =  Margin;
            btn.OffsetBottom =  Margin + BtnSize;

            btn.Pressed += () => onClose();
            parent.AddChild(btn);
            return btn;
        }
    }
}
