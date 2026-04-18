using Godot;
using System;
using System.Collections.Generic;
using Natiolation.Core;
using Tech = Natiolation.Core.Technology;

namespace Natiolation.UI
{
    // ── Line drawer (separate top-level class, same file) ────────────────
    /// <summary>Draws prerequisite arrows between tech cards.</summary>
    public partial class TechLineDrawer : Control
    {
        public new TechTreePanel Owner = null!;

        public override void _Draw()
        {
            if (Owner == null) return;
            var origin = GetGlobalRect().Position;

            foreach (var (tech, card) in Owner.CardNodes)
            {
                var stats = TechnologyData.GetStats(tech);
                foreach (var prereq in stats.Prerequisites)
                {
                    if (!Owner.CardNodes.TryGetValue(prereq, out var prereqCard)) continue;

                    var srcRect = prereqCard.GetGlobalRect();
                    var dstRect = card.GetGlobalRect();

                    var srcGlobal = srcRect.Position + new Vector2(srcRect.Size.X, srcRect.Size.Y * 0.5f);
                    var dstGlobal = dstRect.Position + new Vector2(0f, dstRect.Size.Y * 0.5f);

                    var localSrc = srcGlobal - origin;
                    var localDst = dstGlobal - origin;

                    bool prereqDone = GameManager.Instance.HasTech(prereq);
                    Color col = prereqDone
                        ? new Color(0.28f, 0.90f, 0.96f, 0.75f)
                        : new Color(0.38f, 0.42f, 0.52f, 0.55f);

                    DrawLine(localSrc, localDst, col, 2.2f, true);

                    // Arrow head
                    var dir  = (localDst - localSrc).Normalized();
                    var perp = new Vector2(-dir.Y, dir.X) * 5f;
                    DrawLine(localDst - dir * 10f + perp, localDst, col, 2.0f);
                    DrawLine(localDst - dir * 10f - perp, localDst, col, 2.0f);
                }
            }
        }
    }

    // =====================================================================
    /// <summary>
    /// Panel del Árbol de Tecnologías estilo Civ.
    /// Columnas por era, líneas de prerequisito, estado visual por tech.
    /// Abierto con T o el botón 🔬 en el HUD.
    /// </summary>
    public partial class TechTreePanel : Control
    {
        // ── Paleta ───────────────────────────────────────────────────────
        private static readonly Color BgMain        = new(0.04f, 0.06f, 0.10f, 0.97f);
        private static readonly Color BgHeader      = new(0.07f, 0.10f, 0.16f, 1.00f);
        private static readonly Color BgCard        = new(0.10f, 0.14f, 0.21f, 1.00f);
        private static readonly Color Gold          = new(1.00f, 0.82f, 0.14f);
        private static readonly Color TextMain      = new(0.94f, 0.94f, 0.97f);
        private static readonly Color TextDim       = new(0.55f, 0.60f, 0.68f);
        private static readonly Color CSci          = new(0.28f, 0.90f, 0.96f);
        private static readonly Color BtnBlue       = new(0.14f, 0.26f, 0.55f);
        private static readonly Color BtnBlueH      = new(0.20f, 0.36f, 0.72f);
        private static readonly Color BtnGreen      = new(0.18f, 0.42f, 0.20f);
        private static readonly Color BtnGreenH     = new(0.26f, 0.56f, 0.28f);
        private static readonly Color BorderResearched   = new(0.30f, 0.35f, 0.42f, 1.00f);
        private static readonly Color BorderResearchable = new(1.00f, 0.82f, 0.14f, 1.00f);
        private static readonly Color BorderLocked       = new(0.18f, 0.22f, 0.30f, 1.00f);
        private static readonly Color BorderActive       = new(0.28f, 0.90f, 0.96f, 1.00f);

        private static readonly string[] EraNames = { "✨  ANTIGÜEDAD", "⚔  CLÁSICA", "🛡  MEDIEVAL" };

        private static readonly Tech[][] EraTechs =
        {
            new[] { Tech.Archery, Tech.BronzeWorking, Tech.Writing, Tech.Masonry },
            new[] { Tech.IronWorking, Tech.Mathematics, Tech.Currency, Tech.Philosophy },
            new[] { Tech.Steel, Tech.Gunpowder },
        };

        // Public so TechLineDrawer can access
        public readonly Dictionary<Tech, Control> CardNodes = new();

        private TechLineDrawer _lineDrawer = null!;
        private Control _cardsRoot = null!;

        // ================================================================
        //  PUBLIC
        // ================================================================

        public void Toggle()
        {
            Visible = !Visible;
            if (Visible) Refresh();
        }

        // ================================================================
        //  GODOT
        // ================================================================

        public override void _Ready()
        {
            // Ocupa todo el viewport del CanvasLayer padre
            SetAnchorsPreset(LayoutPreset.FullRect);
            MouseFilter = MouseFilterEnum.Stop;
            Visible = false;
            BuildPanel();
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            if (!Visible) return;
            if (@event is InputEventKey key && key.Pressed && !key.Echo
                && key.Keycode == Key.Escape)
            {
                Visible = false;
                GetViewport().SetInputAsHandled();
            }
        }

        // ================================================================
        //  BUILD
        // ================================================================

        private void BuildPanel()
        {
            // Semi-transparent bg overlay
            var bg = new ColorRect { Color = new Color(0, 0, 0, 0.52f) };
            bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            bg.MouseFilter = Control.MouseFilterEnum.Ignore;
            AddChild(bg);

            // Main panel
            var panel = new PanelContainer();
            panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            var sb = new StyleBoxFlat { BgColor = BgMain };
            panel.AddThemeStyleboxOverride("panel", sb);
            AddChild(panel);

            var vbox = new VBoxContainer();
            panel.AddChild(vbox);

            vbox.AddChild(BuildHeader());

            // Scrollable content
            var scroll = new ScrollContainer();
            scroll.SizeFlagsVertical   = Control.SizeFlags.ExpandFill;
            scroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Auto;
            scroll.VerticalScrollMode   = ScrollContainer.ScrollMode.Auto;
            vbox.AddChild(scroll);

            // Tree container (lines + era columns live here)
            var treeContainer = new Control();
            treeContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            treeContainer.SizeFlagsVertical   = Control.SizeFlags.ExpandFill;
            treeContainer.CustomMinimumSize    = new Vector2(800, 400);
            scroll.AddChild(treeContainer);

            // Era columns
            var hbox = new HBoxContainer();
            hbox.AddThemeConstantOverride("separation", 0);
            hbox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            hbox.SizeFlagsVertical   = Control.SizeFlags.ExpandFill;
            treeContainer.AddChild(hbox);
            hbox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _cardsRoot = hbox;

            for (int i = 0; i < EraTechs.Length; i++)
            {
                hbox.AddChild(BuildEraColumn(i));
                if (i < EraTechs.Length - 1)
                {
                    var gap = new Control { CustomMinimumSize = new Vector2(44, 0) };
                    gap.MouseFilter = Control.MouseFilterEnum.Ignore;
                    hbox.AddChild(gap);
                }
            }

            // Line drawer on top (behind cards visually but drawn over background)
            _lineDrawer = new TechLineDrawer { Owner = this };
            _lineDrawer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _lineDrawer.MouseFilter = Control.MouseFilterEnum.Ignore;
            treeContainer.AddChild(_lineDrawer);
        }

        private Control BuildHeader()
        {
            var header = new PanelContainer();
            var sb = new StyleBoxFlat { BgColor = BgHeader };
            header.AddThemeStyleboxOverride("panel", sb);

            var m = new MarginContainer();
            m.AddThemeConstantOverride("margin_left",   24);
            m.AddThemeConstantOverride("margin_right",  16);
            m.AddThemeConstantOverride("margin_top",    12);
            m.AddThemeConstantOverride("margin_bottom", 12);
            header.AddChild(m);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 16);
            m.AddChild(row);

            var title = new Label { Text = "🔬  ÁRBOL DE TECNOLOGÍAS" };
            title.AddThemeColorOverride("font_color", Gold);
            title.AddThemeFontSizeOverride("font_size", 20);
            title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            title.VerticalAlignment   = VerticalAlignment.Center;
            row.AddChild(title);

            var hint = new Label { Text = "[T] / [Esc] Cerrar" };
            hint.AddThemeColorOverride("font_color", TextDim);
            hint.AddThemeFontSizeOverride("font_size", 13);
            hint.VerticalAlignment = VerticalAlignment.Center;
            row.AddChild(hint);

            var closeBtn = new Button { Text = "✕" };
            StyleBtn(closeBtn, new Color(0.45f, 0.08f, 0.06f), new Color(0.65f, 0.12f, 0.09f));
            closeBtn.CustomMinimumSize = new Vector2(32, 32);
            closeBtn.Pressed += () => Visible = false;
            row.AddChild(closeBtn);

            return header;
        }

        private Control BuildEraColumn(int eraIdx)
        {
            var col = new VBoxContainer();
            col.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            col.AddThemeConstantOverride("separation", 0);

            // Era title
            var eraHeader = new PanelContainer();
            var ehSb = new StyleBoxFlat
            {
                BgColor = new Color(0.07f + eraIdx * 0.015f, 0.10f, 0.17f + eraIdx * 0.015f),
            };
            eraHeader.AddThemeStyleboxOverride("panel", ehSb);

            var ehM = new MarginContainer();
            ehM.AddThemeConstantOverride("margin_left",   16);
            ehM.AddThemeConstantOverride("margin_right",  16);
            ehM.AddThemeConstantOverride("margin_top",    10);
            ehM.AddThemeConstantOverride("margin_bottom", 10);
            eraHeader.AddChild(ehM);

            var ehLabel = new Label { Text = EraNames[eraIdx] };
            ehLabel.AddThemeColorOverride("font_color", Gold.Lightened(eraIdx * 0.12f));
            ehLabel.AddThemeFontSizeOverride("font_size", 15);
            ehLabel.HorizontalAlignment = HorizontalAlignment.Center;
            ehM.AddChild(ehLabel);
            col.AddChild(eraHeader);

            // Cards area
            var cardsMarg = new MarginContainer();
            cardsMarg.AddThemeConstantOverride("margin_left",   14);
            cardsMarg.AddThemeConstantOverride("margin_right",  14);
            cardsMarg.AddThemeConstantOverride("margin_top",    14);
            cardsMarg.AddThemeConstantOverride("margin_bottom", 14);
            cardsMarg.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            col.AddChild(cardsMarg);

            var cardsVbox = new VBoxContainer();
            cardsVbox.AddThemeConstantOverride("separation", 10);
            cardsMarg.AddChild(cardsVbox);

            foreach (var tech in EraTechs[eraIdx])
            {
                var card = BuildTechCard(tech);
                cardsVbox.AddChild(card);
                CardNodes[tech] = card;
            }

            return col;
        }

        private Control BuildTechCard(Tech tech)
        {
            var gm    = GameManager.Instance;
            var stats = TechnologyData.GetStats(tech);

            bool isResearched   = gm.HasTech(tech);
            bool isActive       = gm.CurrentResearch == tech;
            bool isResearchable = !isResearched && gm.CanResearch(tech);

            Color borderCol = isResearched   ? BorderResearched
                            : isActive       ? BorderActive
                            : isResearchable ? BorderResearchable
                            :                  BorderLocked;

            Color bgCol = isResearched ? new Color(0.07f, 0.09f, 0.12f, 1f)
                        : isActive     ? new Color(0.07f, 0.17f, 0.21f, 1f)
                        :                BgCard;

            // Outer border (2px styled border via nested panels)
            var outer = new PanelContainer();
            var outerSb = new StyleBoxFlat { BgColor = borderCol };
            outerSb.SetCornerRadiusAll(6);
            outer.AddThemeStyleboxOverride("panel", outerSb);

            var innerMarg = new MarginContainer();
            innerMarg.AddThemeConstantOverride("margin_left",   2);
            innerMarg.AddThemeConstantOverride("margin_right",  2);
            innerMarg.AddThemeConstantOverride("margin_top",    2);
            innerMarg.AddThemeConstantOverride("margin_bottom", 2);
            outer.AddChild(innerMarg);

            var inner = new PanelContainer();
            var innerSb = new StyleBoxFlat { BgColor = bgCol };
            innerSb.SetCornerRadiusAll(4);
            inner.AddThemeStyleboxOverride("panel", innerSb);
            innerMarg.AddChild(inner);

            var content = new MarginContainer();
            content.AddThemeConstantOverride("margin_left",   12);
            content.AddThemeConstantOverride("margin_right",  12);
            content.AddThemeConstantOverride("margin_top",    10);
            content.AddThemeConstantOverride("margin_bottom", 10);
            inner.AddChild(content);

            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 5);
            content.AddChild(col);

            // ── Name row ──────────────────────────────────────────────────
            var nameRow = new HBoxContainer();
            nameRow.AddThemeConstantOverride("separation", 8);
            col.AddChild(nameRow);

            string icon = isResearched   ? "✅"
                        : isActive       ? "🔬"
                        : isResearchable ? "💡"
                        :                  "🔒";
            var iconLbl = new Label { Text = icon };
            iconLbl.AddThemeFontSizeOverride("font_size", 14);
            nameRow.AddChild(iconLbl);

            var nameLbl = new Label { Text = stats.DisplayName };
            float dimT = isResearched ? 0.70f : 0f;
            nameLbl.AddThemeColorOverride("font_color", TextMain.Lerp(TextDim, dimT));
            nameLbl.AddThemeFontSizeOverride("font_size", 15);
            nameLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            nameLbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            nameRow.AddChild(nameLbl);

            // ── Cost row ──────────────────────────────────────────────────
            var costRow = new HBoxContainer();
            costRow.AddThemeConstantOverride("separation", 4);
            col.AddChild(costRow);

            var sciIcon = new Label { Text = "🔬" };
            sciIcon.AddThemeFontSizeOverride("font_size", 12);
            costRow.AddChild(sciIcon);

            var sciAmt = new Label { Text = $"{stats.ResearchCost}" };
            sciAmt.AddThemeColorOverride("font_color", CSci.Lerp(TextDim, isResearched ? 0.6f : 0f));
            sciAmt.AddThemeFontSizeOverride("font_size", 13);
            costRow.AddChild(sciAmt);

            if (isResearchable && !isActive)
            {
                int sciPerTurn = Math.Max(1, gm.ScienceLastDelta);
                int turns = (int)Math.Ceiling((double)stats.ResearchCost / sciPerTurn);
                var turnsLbl = new Label { Text = $"  (~{turns}t)" };
                turnsLbl.AddThemeColorOverride("font_color", TextDim);
                turnsLbl.AddThemeFontSizeOverride("font_size", 12);
                costRow.AddChild(turnsLbl);
            }
            else if (isActive)
            {
                int sciPerTurn = Math.Max(1, gm.ScienceLastDelta);
                int stored     = gm.ScienceStored;
                int remaining  = Math.Max(0, stats.ResearchCost - stored);
                int turns      = (int)Math.Ceiling((double)remaining / sciPerTurn);
                var progLbl = new Label { Text = $"  {stored}/{stats.ResearchCost} · {turns}t" };
                progLbl.AddThemeColorOverride("font_color", BorderActive);
                progLbl.AddThemeFontSizeOverride("font_size", 12);
                costRow.AddChild(progLbl);
            }

            // ── Separator ─────────────────────────────────────────────────
            var sep = new HSeparator();
            sep.Modulate = new Color(1, 1, 1, 0.12f);
            col.AddChild(sep);

            // ── Unlocks ───────────────────────────────────────────────────
            if (stats.UnlocksUnits.Length > 0 || stats.UnlocksBuildings.Length > 0)
            {
                var unlockRow = new HBoxContainer();
                unlockRow.AddThemeConstantOverride("separation", 6);
                col.AddChild(unlockRow);

                var unlockTitle = new Label { Text = "Desbloquea:" };
                unlockTitle.AddThemeColorOverride("font_color", TextDim);
                unlockTitle.AddThemeFontSizeOverride("font_size", 12);
                unlockRow.AddChild(unlockTitle);

                var sb2 = new System.Text.StringBuilder();
                foreach (var u in stats.UnlocksUnits)
                    sb2.Append($"⚔ {FormatUnitName(u)}  ");
                foreach (var b in stats.UnlocksBuildings)
                    sb2.Append($"🏛 {FormatBuildingName(b)}  ");

                var unlockVal = new Label { Text = sb2.ToString().TrimEnd() };
                unlockVal.AddThemeColorOverride("font_color", Gold.Lerp(TextDim, isResearched ? 0.7f : 0f));
                unlockVal.AddThemeFontSizeOverride("font_size", 12);
                unlockVal.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                unlockVal.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                unlockRow.AddChild(unlockVal);
            }

            // ── Prerequisites text ────────────────────────────────────────
            if (stats.Prerequisites.Length > 0)
            {
                var sb3 = new System.Text.StringBuilder("Req: ");
                foreach (var p in stats.Prerequisites)
                    sb3.Append(TechnologyData.GetStats(p).DisplayName + "  ");
                var prereqLbl = new Label { Text = sb3.ToString().TrimEnd() };
                prereqLbl.AddThemeColorOverride("font_color", TextDim.Lerp(Colors.Transparent, 0.3f));
                prereqLbl.AddThemeFontSizeOverride("font_size", 11);
                prereqLbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                col.AddChild(prereqLbl);
            }

            // ── Action button ─────────────────────────────────────────────
            if (isResearchable)
            {
                var btn = new Button { Text = "Investigar" };
                StyleBtn(btn, BtnGreen, BtnGreenH);
                btn.AddThemeFontSizeOverride("font_size", 13);
                var cap = tech;
                btn.Pressed += () =>
                {
                    gm.SetResearch(cap);
                    Refresh();
                };
                col.AddChild(btn);
            }
            else if (isResearched)
            {
                var doneLbl = new Label { Text = "✅ Investigada" };
                doneLbl.AddThemeColorOverride("font_color", TextDim);
                doneLbl.AddThemeFontSizeOverride("font_size", 12);
                col.AddChild(doneLbl);
            }
            else if (isActive)
            {
                var activeLbl = new Label { Text = "🔬 Investigando…" };
                activeLbl.AddThemeColorOverride("font_color", BorderActive);
                activeLbl.AddThemeFontSizeOverride("font_size", 12);
                col.AddChild(activeLbl);
            }

            return outer;
        }

        // ================================================================
        //  REFRESH
        // ================================================================

        public void Refresh()
        {
            if (!IsInsideTree()) return;

            foreach (var tech in new List<Tech>(CardNodes.Keys))
            {
                var card   = CardNodes[tech];
                var parent = card.GetParent();
                var idx    = card.GetIndex();
                card.QueueFree();
                var newCard = BuildTechCard(tech);
                parent.AddChild(newCard);
                parent.MoveChild(newCard, idx);
                CardNodes[tech] = newCard;
            }

            _lineDrawer?.QueueRedraw();
        }

        // ================================================================
        //  HELPERS
        // ================================================================

        private static void StyleBtn(Button btn, Color normal, Color hover)
        {
            var sb = new StyleBoxFlat { BgColor = normal };
            sb.SetCornerRadiusAll(5);
            var sbH = new StyleBoxFlat { BgColor = hover };
            sbH.SetCornerRadiusAll(5);
            btn.AddThemeStyleboxOverride("normal",  sb);
            btn.AddThemeStyleboxOverride("hover",   sbH);
            btn.AddThemeStyleboxOverride("pressed", sb);
            btn.AddThemeStyleboxOverride("focus",   sb);
            btn.AddThemeColorOverride("font_color", Colors.White);
        }

        private static string FormatUnitName(Units.UnitType u) => u switch
        {
            Units.UnitType.Warrior       => "Guerrero",
            Units.UnitType.Archer        => "Arquero",
            Units.UnitType.Scout         => "Explorador",
            Units.UnitType.Longbowman    => "Ballestero",
            Units.UnitType.Swordsman     => "Espadachín",
            Units.UnitType.Knight        => "Caballero",
            Units.UnitType.Ballista      => "Ballista",
            Units.UnitType.Longswordsman => "Espadón",
            Units.UnitType.Musketman     => "Mosquetero",
            Units.UnitType.Settler       => "Colono",
            Units.UnitType.Worker        => "Trabajador",
            _                            => u.ToString(),
        };

        private static string FormatBuildingName(Cities.BuildingType b) => b switch
        {
            Cities.BuildingType.Library    => "Biblioteca",
            Cities.BuildingType.Forge      => "Forja",
            Cities.BuildingType.Harbor     => "Puerto",
            Cities.BuildingType.University => "Universidad",
            _                              => b.ToString(),
        };
    }
}
