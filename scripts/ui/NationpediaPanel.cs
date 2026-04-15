using Godot;
using System;
using System.Collections.Generic;
using Natiolation.Core;
using Natiolation.Units;
using Natiolation.Cities;
using Natiolation.Map;

namespace Natiolation.UI
{
    /// <summary>
    /// Panel de referencia estilo "Civilopedia": accesible desde el HUD,
    /// muestra información de todas las mecánicas, unidades, edificios,
    /// tecnologías y tipos de terreno del juego.
    /// </summary>
    public partial class NationpediaPanel : CanvasLayer
    {
        // ── Paleta de colores ────────────────────────────────────────────
        private static readonly Color BgMain    = new(0.04f, 0.06f, 0.10f, 0.97f);
        private static readonly Color BgSidebar = new(0.06f, 0.09f, 0.14f, 1.00f);
        private static readonly Color BgContent = new(0.08f, 0.11f, 0.17f, 1.00f);
        private static readonly Color BgCard    = new(0.10f, 0.14f, 0.22f, 1.00f);
        private static readonly Color Gold      = new(1.00f, 0.82f, 0.14f);
        private static readonly Color TextMain  = new(0.94f, 0.94f, 0.97f);
        private static readonly Color TextDim   = new(0.60f, 0.65f, 0.72f);
        private static readonly Color Accent    = new(0.30f, 0.60f, 1.00f);
        private static readonly Color CFood     = new(0.30f, 0.90f, 0.40f);
        private static readonly Color CProd     = new(0.98f, 0.68f, 0.14f);
        private static readonly Color CGold     = new(1.00f, 0.86f, 0.22f);
        private static readonly Color CSci      = new(0.28f, 0.90f, 0.96f);

        private PanelContainer _root     = null!;
        private VBoxContainer  _sideList = null!;
        private ScrollContainer _scrollContent = null!;
        private VBoxContainer   _contentCol    = null!;

        private int _activeCategory = 0;
        private readonly List<Button> _catButtons = new();

        private readonly string[] _categoryNames =
        {
            "📖  Cómo Jugar",
            "🌿  Terrenos",
            "⚔  Unidades",
            "🏛  Edificios",
            "🔬  Tecnologías",
            "🎯  Conceptos",
        };

        // ================================================================

        public override void _Ready()
        {
            Layer = 50;
            Visible = false;
            BuildPanel();
            ShowCategory(0);
        }

        public void Toggle()
        {
            Visible = !Visible;
            if (Visible) ShowCategory(_activeCategory);
        }

        // ================================================================
        //  CONSTRUCCIÓN DEL PANEL
        // ================================================================

        private void BuildPanel()
        {
            _root = new PanelContainer();
            _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.AddThemeStyleboxOverride("panel", FlatStyle(BgMain));
            AddChild(_root);

            var hbox = new HBoxContainer();
            hbox.AddThemeConstantOverride("separation", 0);
            _root.AddChild(hbox);

            // ── Sidebar izquierdo ──────────────────────────────────────
            var sidebar = new PanelContainer();
            sidebar.CustomMinimumSize = new Vector2(230, 0);
            sidebar.AddThemeStyleboxOverride("panel", FlatStyle(BgSidebar));
            hbox.AddChild(sidebar);

            var sideVbox = new VBoxContainer();
            sideVbox.AddThemeConstantOverride("separation", 0);
            sidebar.AddChild(sideVbox);

            // Título del panel
            var titlePanel = new PanelContainer();
            titlePanel.AddThemeStyleboxOverride("panel", FlatStyle(new Color(0.06f, 0.10f, 0.18f)));
            sideVbox.AddChild(titlePanel);

            var titleMargin = new MarginContainer();
            titleMargin.AddThemeConstantOverride("margin_left",   20);
            titleMargin.AddThemeConstantOverride("margin_right",  20);
            titleMargin.AddThemeConstantOverride("margin_top",    16);
            titleMargin.AddThemeConstantOverride("margin_bottom", 16);
            titlePanel.AddChild(titleMargin);

            var titleLabel = new Label { Text = "NATIONPEDIA" };
            titleLabel.AddThemeColorOverride("font_color",   Gold);
            titleLabel.AddThemeFontSizeOverride("font_size", 22);
            titleMargin.AddChild(titleLabel);

            var separator = new HSeparator();
            sideVbox.AddChild(separator);

            _sideList = new VBoxContainer();
            _sideList.AddThemeConstantOverride("separation", 2);
            var sideMargin = new MarginContainer();
            sideMargin.AddThemeConstantOverride("margin_left",  8);
            sideMargin.AddThemeConstantOverride("margin_right", 8);
            sideMargin.AddThemeConstantOverride("margin_top",   8);
            sideVbox.AddChild(sideMargin);
            sideMargin.AddChild(_sideList);

            for (int i = 0; i < _categoryNames.Length; i++)
            {
                int idx = i;
                var btn = new Button { Text = _categoryNames[i], Flat = false };
                btn.AddThemeFontSizeOverride("font_size", 16);
                btn.AddThemeColorOverride("font_color",          TextDim);
                btn.AddThemeColorOverride("font_hover_color",    TextMain);
                btn.AddThemeColorOverride("font_pressed_color",  Gold);
                btn.AddThemeColorOverride("font_focus_color",    Gold);
                btn.CustomMinimumSize = new Vector2(0, 40);
                btn.Pressed += () => ShowCategory(idx);
                _sideList.AddChild(btn);
                _catButtons.Add(btn);
            }

            // Spacer + botón cerrar al final del sidebar
            var sidespacer = new Control();
            sidespacer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            sideVbox.AddChild(sidespacer);

            var closeBtn = new Button { Text = "✕  Cerrar" };
            closeBtn.AddThemeFontSizeOverride("font_size", 16);
            closeBtn.AddThemeColorOverride("font_color", TextDim);
            closeBtn.CustomMinimumSize = new Vector2(0, 42);
            var closePad = new MarginContainer();
            closePad.AddThemeConstantOverride("margin_left",   8);
            closePad.AddThemeConstantOverride("margin_right",  8);
            closePad.AddThemeConstantOverride("margin_bottom", 8);
            sideVbox.AddChild(closePad);
            closePad.AddChild(closeBtn);
            closeBtn.Pressed += () => Visible = false;

            // ── Área de contenido ──────────────────────────────────────
            var contentPanel = new PanelContainer();
            contentPanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            contentPanel.AddThemeStyleboxOverride("panel", FlatStyle(BgContent));
            hbox.AddChild(contentPanel);

            _scrollContent = new ScrollContainer();
            _scrollContent.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _scrollContent.SizeFlagsVertical   = Control.SizeFlags.ExpandFill;
            contentPanel.AddChild(_scrollContent);

            _contentCol = new VBoxContainer();
            _contentCol.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _contentCol.AddThemeConstantOverride("separation", 12);
            var contentMargin = new MarginContainer();
            contentMargin.AddThemeConstantOverride("margin_left",   24);
            contentMargin.AddThemeConstantOverride("margin_right",  24);
            contentMargin.AddThemeConstantOverride("margin_top",    20);
            contentMargin.AddThemeConstantOverride("margin_bottom", 20);
            contentMargin.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _scrollContent.AddChild(contentMargin);
            contentMargin.AddChild(_contentCol);
        }

        // ================================================================
        //  LLENADO DE CONTENIDO POR CATEGORÍA
        // ================================================================

        private void ShowCategory(int idx)
        {
            _activeCategory = idx;

            // Resaltar botón activo
            for (int i = 0; i < _catButtons.Count; i++)
            {
                bool active = i == idx;
                _catButtons[i].AddThemeColorOverride("font_color",
                    active ? Gold : TextDim);
            }

            // Limpiar contenido anterior
            foreach (var c in _contentCol.GetChildren()) c.QueueFree();

            switch (idx)
            {
                case 0: BuildHowToPlay();    break;
                case 1: BuildTerrainList();  break;
                case 2: BuildUnitList();     break;
                case 3: BuildBuildingList(); break;
                case 4: BuildTechList();     break;
                case 5: BuildConcepts();     break;
            }
        }

        // ── Cómo Jugar ────────────────────────────────────────────────────

        private void BuildHowToPlay()
        {
            AddPageTitle("📖  Cómo Jugar");

            AddSection("Objetivo del juego",
                "Construye una civilización poderosa explorando el mapa, fundando ciudades, " +
                "investigando tecnologías y expandiendo tu territorio. Desarrolla tu economía, " +
                "entrena ejércitos y lleva tu nación a la prosperidad.");

            AddSection("El turno",
                "Cada turno:\n" +
                "• Mueve o da órdenes a TODAS tus unidades (no puedes terminar el turno si quedan sin órdenes)\n" +
                "• Gestiona la producción de tus ciudades (siempre deben estar produciendo algo)\n" +
                "• Elige qué tecnología investigar (no puedes saltar turnos sin investigar)\n" +
                "• Pulsa ENTER o el botón 'Fin de turno' para pasar al siguiente turno");

            AddSection("Unidades — Controles",
                "[Click izquierdo] en unidad → seleccionarla\n" +
                "[Click izquierdo] en tile → mover la unidad seleccionada\n" +
                "[Click derecho] en tile → establecer destino multi-turno (waypoint)\n" +
                "[F] → Fortificar (la unidad defiende sin moverse)\n" +
                "[S] → Saltear turno (renunciar al movimiento de este turno)\n" +
                "[B] → Fundar ciudad (solo el Colono, en tile compatible)\n" +
                "[I] → Construir irrigación (Worker, en Pastizal o Llanura junto a río)\n" +
                "[G] → Construir granja (Worker, en Pastizal o Llanura)\n" +
                "[M] → Construir mina (Worker, en Colinas o Montaña)\n" +
                "[R] → Construir camino (Worker, en cualquier tile)\n" +
                "[T] → Abrir selector de tecnología\n" +
                "[ESC] → Deseleccionar unidad");

            AddSection("Economía",
                "🌾 Comida: Cada ciudad necesita comida para crecer. Con más población trabaja más tiles.\n" +
                "⚒  Producción: Se usa para construir unidades y edificios.\n" +
                "💰 Oro: Financia el mantenimiento de edificios y unidades.\n" +
                "🔬 Ciencia: Se acumula para investigar tecnologías. Una ciudad base genera 1/turno.");

            AddSection("Combate",
                "Las batallas se resuelven automáticamente al mover una unidad sobre un enemigo.\n" +
                "La unidad más fuerte gana con probabilidad mayor. Las unidades veteranas (★) " +
                "tienen +2 de fuerza.\n\n" +
                "Factores que afectan el combate:\n" +
                "• Fuerza de combate base del tipo de unidad\n" +
                "• Bonus de veterano (+2)\n" +
                "• Defensa de terreno (no implementada aún)");

            AddSection("Niebla de guerra",
                "El mapa comienza inexplorado. Las unidades revelan el terreno circundante " +
                "según su rango de visión. Los tiles explorados pero sin unidades cerca aparecen " +
                "en niebla (colores apagados). Solo los tiles visibles muestran unidades enemigas.");
        }

        // ── Terrenos ──────────────────────────────────────────────────────

        private void BuildTerrainList()
        {
            AddPageTitle("🌿  Tipos de Terreno");

            var terrains = new (string name, string emoji, Color color, string desc, string stats)[]
            {
                ("Llanura",    "🟨", new Color(0.88f, 0.85f, 0.42f),
                 "Tierra plana fácil de recorrer. Ideal para farms y ciudades.",
                 "🌾 +1  ⚒ +1  💰 0  Movimiento: 1"),
                ("Pastizal",   "🟩", new Color(0.38f, 0.72f, 0.28f),
                 "Terreno verde y fértil. Excelente para la agricultura.",
                 "🌾 +2  ⚒ 0   💰 0  Movimiento: 1"),
                ("Desierto",   "🟫", new Color(0.88f, 0.78f, 0.40f),
                 "Árido y hostil. Poca producción pero transitable.",
                 "🌾 0   ⚒ 0   💰 0  Movimiento: 1"),
                ("Colinas",    "🟤", new Color(0.62f, 0.52f, 0.34f),
                 "Terreno elevado. Rico en minerales, costoso de cruzar.",
                 "🌾 0   ⚒ +2  💰 0  Movimiento: 2  [Mina +1⚒]"),
                ("Bosque",     "🌲", new Color(0.18f, 0.48f, 0.14f),
                 "Espeso y difícil de atravesar. Buena madera y defensa.",
                 "🌾 +1  ⚒ +1  💰 0  Movimiento: 2"),
                ("Montaña",    "🏔", new Color(0.55f, 0.55f, 0.60f),
                 "Casi infranqueable. No se puede fundar ciudad aquí.",
                 "🌾 0   ⚒ 0   💰 0  Movimiento: 3  (intransitable)"),
                ("Costa",      "🌊", new Color(0.26f, 0.62f, 0.86f),
                 "Aguas poco profundas. Permite pesca. Puertos posibles.",
                 "🌾 +1  ⚒ 0   💰 +1  Solo por agua"),
                ("Océano",     "🌊", new Color(0.10f, 0.30f, 0.72f),
                 "Mar profundo. Infranqueable sin unidades navales.",
                 "🌾 0   ⚒ 0   💰 0   Solo por agua"),
                ("Tundra",     "❄",  new Color(0.70f, 0.82f, 0.90f),
                 "Fría y poco fértil pero transitable.",
                 "🌾 +1  ⚒ 0   💰 0  Movimiento: 1"),
                ("Ártico",     "⛄", new Color(0.90f, 0.95f, 1.00f),
                 "Hielo permanente. Casi nada crece aquí.",
                 "🌾 0   ⚒ 0   💰 0  Movimiento: 2"),
                ("Río (bonus)","💧", new Color(0.28f, 0.62f, 0.96f),
                 "Los bordes de ríos dan bonificación al tile adyacente.",
                 "🌾 +1  💰 +1  Bonus al tile que lo bordea"),
            };

            foreach (var (name, emoji, color, desc, stats) in terrains)
            {
                AddTerrainCard(emoji + "  " + name, color, desc, stats);
            }
        }

        // ── Unidades ──────────────────────────────────────────────────────

        private void BuildUnitList()
        {
            AddPageTitle("⚔  Unidades");

            var units = new (UnitType type, string desc, string req)[]
            {
                (UnitType.Settler,   "Funda nuevas ciudades. No puede atacar. Esencial para expandir el territorio.", "Sin requisito"),
                (UnitType.Worker,    "Construye mejoras de terreno: caminos, granjas, irrigación, minas.", "Sin requisito"),
                (UnitType.Scout,     "Explorador rápido. Alta visión y movimiento. Fuerza de combate débil.", "Sin requisito"),
                (UnitType.Warrior,   "Unidad de combate básica. Equilibrada entre fuerza y movimiento.", "Sin requisito"),
                (UnitType.Archer,    "Ataque a distancia. Fuerza cuerpo a cuerpo menor pero ataque de rango.", "Sin requisito"),
                (UnitType.Longbowman,"Arquero avanzado con mayor rango y fuerza. Requiere investigar Tiro con Arco.", "Tiro con Arco"),
                (UnitType.Swordsman, "Infantería pesada con escudo. Alta fuerza de combate melee.", "Trabajo con Bronce"),
                (UnitType.Knight,    "Caballería veloz con lanza. Tres movimientos por turno.", "Trabajo con Hierro"),
                (UnitType.Ballista,  "Artillería de madera. Bajo movimiento pero alto alcance.", "Matemáticas"),
                (UnitType.Longswordsman, "Espadachín con armadura de placa. Alta fuerza melee.", "Acero"),
                (UnitType.Musketman, "Arcabucero con mosquete. La unidad más poderosa disponible.", "Pólvora"),
            };

            foreach (var (utype, desc, req) in units)
            {
                var stats = UnitTypeData.GetStats(utype);
                string statsStr =
                    $"CS: {stats.CombatStrength}  " +
                    (stats.RangedStrength > 0 ? $"RS: {stats.RangedStrength}  " : "") +
                    $"Mov: {stats.MaxMovement}  Vista: {stats.SightRange}  " +
                    $"Costo: {stats.ProductionCost}⚒";
                AddUnitCard(stats.DisplayName, GetUnitEmoji(utype), desc, statsStr, req);
            }
        }

        // ── Edificios ─────────────────────────────────────────────────────

        private void BuildBuildingList()
        {
            AddPageTitle("🏛  Edificios");

            var buildings = new (BuildingType type, string emoji, string desc, string req)[]
            {
                (BuildingType.Granary,   "🌾", "Almacén de grano. Al crecer la ciudad, se retiene el 50% de la comida excedente.", "Sin requisito"),
                (BuildingType.Barracks,  "⚔", "Cuartel militar. Las unidades producidas aquí son veteranas (★) y tienen +2 CS.", "Sin requisito"),
                (BuildingType.Temple,    "⛩", "Templo. Genera 1 oro adicional por turno gracias a las donaciones.", "Sin requisito"),
                (BuildingType.Workshop,  "⚙", "Taller. Aumenta la producción de la ciudad en +2/turno.", "Sin requisito"),
                (BuildingType.Market,    "💰", "Mercado. Aumenta el oro generado en un 50% por turno.", "Sin requisito"),
                (BuildingType.Library,   "📚", "Biblioteca. Aumenta la ciencia de la ciudad en +3/turno.", "Escritura"),
                (BuildingType.Forge,     "🔥", "Fragua. Aumenta la producción en +1/turno.", "Cantería"),
                (BuildingType.Harbor,    "⚓", "Puerto. Genera +2 oro/turno en ciudades costeras.", "Moneda"),
                (BuildingType.University,"🎓", "Universidad. +5 ciencia/turno (requiere Biblioteca).", "Filosofía"),
                (BuildingType.CityWalls, "🏰", "Murallas. Mejora las defensas de la ciudad.", "Sin requisito"),
            };

            foreach (var (btype, emoji, desc, req) in buildings)
            {
                var stats = BuildingTypeData.GetStats(btype);
                string statsStr = $"Costo: {stats.ProductionCost}⚒  Mantenimiento: {stats.MaintenanceCost}💰/turno";
                AddBuildingCard(stats.DisplayName, emoji, desc, statsStr, req);
            }
        }

        // ── Tecnologías ───────────────────────────────────────────────────

        private void BuildTechList()
        {
            AddPageTitle("🔬  Árbol Tecnológico");

            AddLabel("Las tecnologías se investigan acumulando 🔬 Ciencia cada turno.\n" +
                     "Debes tener los prerequisitos investigados antes de empezar una nueva tecnología.\n" +
                     "Al terminar una investigación, el juego te pedirá elegir la siguiente.",
                     TextDim, 16);
            AddSpacer(8);

            var techs = new (Technology t, string emoji)[]
            {
                (Technology.Archery,        "🏹"),
                (Technology.BronzeWorking,  "⚔"),
                (Technology.Writing,        "📜"),
                (Technology.Masonry,        "🧱"),
                (Technology.IronWorking,    "🗡"),
                (Technology.Mathematics,    "📐"),
                (Technology.Currency,       "💰"),
                (Technology.Philosophy,     "🎓"),
                (Technology.Steel,          "⚙"),
                (Technology.Gunpowder,      "💥"),
            };

            foreach (var (tech, emoji) in techs)
            {
                var stats = TechnologyData.GetStats(tech);
                string prereqStr = stats.Prerequisites.Length == 0
                    ? "Sin prerequisitos"
                    : "Requiere: " + string.Join(", ", System.Array.ConvertAll(stats.Prerequisites,
                        p => TechnologyData.GetStats(p).DisplayName));
                string unlocksStr = "";
                if (stats.UnlocksUnits.Length > 0)
                    unlocksStr += "Unidades: " + string.Join(", ", System.Array.ConvertAll(stats.UnlocksUnits, u => UnitTypeData.GetStats(u).DisplayName));
                if (stats.UnlocksBuildings.Length > 0)
                {
                    if (unlocksStr.Length > 0) unlocksStr += "\n";
                    unlocksStr += "Edificios: " + string.Join(", ", System.Array.ConvertAll(stats.UnlocksBuildings, b => BuildingTypeData.GetStats(b).DisplayName));
                }
                AddTechCard(emoji + "  " + stats.DisplayName, stats.ResearchCost, prereqStr, unlocksStr, stats.Description);
            }
        }

        // ── Conceptos ─────────────────────────────────────────────────────

        private void BuildConcepts()
        {
            AddPageTitle("🎯  Conceptos del Juego");

            AddSection("Mejoras de terreno (Worker)",
                "Los Workers pueden construir mejoras que aumentan los rendimientos de los tiles:\n\n" +
                "[G] Granja (+2🌾) — Solo en Llanura o Pastizal\n" +
                "[I] Irrigación (+1🌾) — Solo en Llanura/Pastizal junto a río\n" +
                "[M] Mina (+1⚒) — Solo en Colinas o Montañas\n" +
                "[R] Camino — Reduce el costo de movimiento a 1/3\n\n" +
                "Construir una mejora consume todos los puntos de movimiento del Worker.");

            AddSection("Crecimiento de ciudades",
                "Las ciudades crecen cuando acumulan suficiente Comida:\n\n" +
                "• Umbral de crecimiento = 10 + Población × 5\n" +
                "• Al crecer, la ciudad trabaja un tile más (hasta Population tiles)\n" +
                "• Un Granero retiene el 50% de la comida al crecer\n" +
                "• Las ciudades más grandes generan más recursos pero son más difíciles de mantener");

            AddSection("Sistema de producción",
                "Cada ciudad produce unidades o edificios de a uno a la vez:\n\n" +
                "• La producción se acumula turno a turno (⚒ almacenadas)\n" +
                "• Al completarse, aparece la unidad o el edificio\n" +
                "• No puedes terminar el turno si una ciudad está inactiva\n" +
                "• Las ciudades con Cuartel producen unidades veteranas (★)\n" +
                "• Algunos edificios y unidades requieren tecnologías específicas");

            AddSection("Waypoints (destinos multi-turno)",
                "Con [Click Derecho] en un tile puedes establecer un destino automático:\n\n" +
                "• La unidad avanzará hacia ese punto automáticamente al final de cada turno\n" +
                "• Se muestra con el indicador '→' en la etiqueta de la unidad\n" +
                "• Cualquier acción manual (mover, fortificar, saltear) cancela el waypoint\n" +
                "• Útil para enviar unidades a destinos lejanos sin microgestión");

            AddSection("Veteranos (★)",
                "Las unidades producidas en ciudades CON CUARTEL y que tengan fuerza de combate > 0 " +
                "son automáticamente veteranas:\n\n" +
                "• Se muestran con el símbolo ★ en su etiqueta\n" +
                "• Tienen +2 de Fuerza de Combate\n" +
                "• Ideal para unidades de ataque importantes");

            AddSection("Fog of War",
                "El mapa tiene tres estados de visibilidad:\n\n" +
                "🔲 Inexplorado: nunca visitado, completamente oscuro\n" +
                "🌫 Niebla: explorado previamente, visible en gris (no muestra enemigos)\n" +
                "🟢 Visible: actualmente dentro del rango de visión de una unidad o ciudad\n\n" +
                "El rango de visión varía por unidad (Scout ve más lejos). Las ciudades revelan 3 tiles.");

            AddSection("Economía — Oro y Mantenimiento",
                "Cada edificio tiene un costo de mantenimiento en oro/turno.\n" +
                "Si tu tesoro cae a 0, los edificios pueden quedar inactivos.\n\n" +
                "Fuentes de oro:\n" +
                "• Tiles costeros, tiles de comercio\n" +
                "• Edificios: Mercado (×1.5), Temple (+1), Harbor (+2 costera)\n" +
                "• Ríos (+1 al tile adyacente)");
        }

        // ================================================================
        //  WIDGETS DE CONTENIDO
        // ================================================================

        private void AddPageTitle(string text)
        {
            var lbl = new Label { Text = text };
            lbl.AddThemeColorOverride("font_color",   Gold);
            lbl.AddThemeFontSizeOverride("font_size", 28);
            _contentCol.AddChild(lbl);

            var sep = new HSeparator();
            _contentCol.AddChild(sep);
            AddSpacer(4);
        }

        private void AddSection(string title, string body)
        {
            var card = new PanelContainer();
            card.AddThemeStyleboxOverride("panel", RoundedStyle(BgCard, 6));
            card.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _contentCol.AddChild(card);

            var m = new MarginContainer();
            m.AddThemeConstantOverride("margin_left",   16);
            m.AddThemeConstantOverride("margin_right",  16);
            m.AddThemeConstantOverride("margin_top",    12);
            m.AddThemeConstantOverride("margin_bottom", 12);
            card.AddChild(m);

            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 6);
            m.AddChild(col);

            var titleLbl = new Label { Text = title };
            titleLbl.AddThemeColorOverride("font_color",   Accent);
            titleLbl.AddThemeFontSizeOverride("font_size", 18);
            col.AddChild(titleLbl);

            var bodyLbl = new Label { Text = body, AutowrapMode = TextServer.AutowrapMode.Word };
            bodyLbl.AddThemeColorOverride("font_color",   TextMain);
            bodyLbl.AddThemeFontSizeOverride("font_size", 15);
            bodyLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            col.AddChild(bodyLbl);
        }

        private void AddTerrainCard(string name, Color color, string desc, string stats)
        {
            var card = new PanelContainer();
            card.AddThemeStyleboxOverride("panel", RoundedStyle(BgCard, 6));
            card.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _contentCol.AddChild(card);

            var hbox = new HBoxContainer();
            hbox.AddThemeConstantOverride("separation", 12);
            var m = new MarginContainer();
            m.AddThemeConstantOverride("margin_left", 12); m.AddThemeConstantOverride("margin_right",  12);
            m.AddThemeConstantOverride("margin_top",   8); m.AddThemeConstantOverride("margin_bottom",  8);
            card.AddChild(m); m.AddChild(hbox);

            // Color swatch
            var swatch = new ColorRect { Color = color, CustomMinimumSize = new Vector2(12, 0) };
            hbox.AddChild(swatch);

            var col = new VBoxContainer();
            col.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            col.AddThemeConstantOverride("separation", 3);
            hbox.AddChild(col);

            var nameLbl = new Label { Text = name };
            nameLbl.AddThemeColorOverride("font_color",   TextMain);
            nameLbl.AddThemeFontSizeOverride("font_size", 17);
            col.AddChild(nameLbl);

            var descLbl = new Label { Text = desc, AutowrapMode = TextServer.AutowrapMode.Word };
            descLbl.AddThemeColorOverride("font_color",   TextDim);
            descLbl.AddThemeFontSizeOverride("font_size", 14);
            descLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            col.AddChild(descLbl);

            var statLbl = new Label { Text = stats };
            statLbl.AddThemeColorOverride("font_color",   CProd);
            statLbl.AddThemeFontSizeOverride("font_size", 13);
            col.AddChild(statLbl);
        }

        private void AddUnitCard(string name, string emoji, string desc, string stats, string req)
        {
            var card = new PanelContainer();
            card.AddThemeStyleboxOverride("panel", RoundedStyle(BgCard, 6));
            card.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _contentCol.AddChild(card);

            var m = new MarginContainer();
            m.AddThemeConstantOverride("margin_left",  16); m.AddThemeConstantOverride("margin_right",  16);
            m.AddThemeConstantOverride("margin_top",   10); m.AddThemeConstantOverride("margin_bottom", 10);
            card.AddChild(m);

            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 4);
            m.AddChild(col);

            var headerRow = new HBoxContainer();
            col.AddChild(headerRow);
            var emojiLbl = new Label { Text = emoji };
            emojiLbl.AddThemeFontSizeOverride("font_size", 20);
            headerRow.AddChild(emojiLbl);
            var nameLbl = new Label { Text = "  " + name };
            nameLbl.AddThemeColorOverride("font_color",   TextMain);
            nameLbl.AddThemeFontSizeOverride("font_size", 19);
            headerRow.AddChild(nameLbl);
            var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            headerRow.AddChild(spacer);
            var reqLbl = new Label { Text = "🔬 " + req };
            reqLbl.AddThemeColorOverride("font_color",   req == "Sin requisito" ? TextDim : CSci);
            reqLbl.AddThemeFontSizeOverride("font_size", 13);
            headerRow.AddChild(reqLbl);

            var descLbl = new Label { Text = desc, AutowrapMode = TextServer.AutowrapMode.Word };
            descLbl.AddThemeColorOverride("font_color",   TextDim);
            descLbl.AddThemeFontSizeOverride("font_size", 14);
            descLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            col.AddChild(descLbl);

            var statLbl = new Label { Text = stats };
            statLbl.AddThemeColorOverride("font_color",   CProd);
            statLbl.AddThemeFontSizeOverride("font_size", 13);
            col.AddChild(statLbl);
        }

        private void AddBuildingCard(string name, string emoji, string desc, string stats, string req)
        {
            AddUnitCard(name, emoji, desc, stats, req);  // mismo layout
        }

        private void AddTechCard(string name, int cost, string prereq, string unlocks, string desc)
        {
            var card = new PanelContainer();
            card.AddThemeStyleboxOverride("panel", RoundedStyle(new Color(0.10f, 0.16f, 0.26f), 6));
            card.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _contentCol.AddChild(card);

            var m = new MarginContainer();
            m.AddThemeConstantOverride("margin_left",  16); m.AddThemeConstantOverride("margin_right",  16);
            m.AddThemeConstantOverride("margin_top",   10); m.AddThemeConstantOverride("margin_bottom", 10);
            card.AddChild(m);

            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 4);
            m.AddChild(col);

            // Encabezado
            var headerRow = new HBoxContainer();
            col.AddChild(headerRow);
            var nameLbl = new Label { Text = name };
            nameLbl.AddThemeColorOverride("font_color",   TextMain);
            nameLbl.AddThemeFontSizeOverride("font_size", 19);
            headerRow.AddChild(nameLbl);
            var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            headerRow.AddChild(spacer);
            var costLbl = new Label { Text = $"🔬 {cost}" };
            costLbl.AddThemeColorOverride("font_color",   CSci);
            costLbl.AddThemeFontSizeOverride("font_size", 16);
            headerRow.AddChild(costLbl);

            if (desc.Length > 0)
            {
                var descLbl = new Label { Text = desc, AutowrapMode = TextServer.AutowrapMode.Word };
                descLbl.AddThemeColorOverride("font_color",   TextDim);
                descLbl.AddThemeFontSizeOverride("font_size", 14);
                descLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                col.AddChild(descLbl);
            }

            var preRow = new HBoxContainer();
            col.AddChild(preRow);
            var preLbl = new Label { Text = prereq };
            preLbl.AddThemeColorOverride("font_color",   Gold);
            preLbl.AddThemeFontSizeOverride("font_size", 13);
            preRow.AddChild(preLbl);

            if (unlocks.Length > 0)
            {
                var unlockLbl = new Label { Text = "✅ Desbloquea: " + unlocks,
                                            AutowrapMode = TextServer.AutowrapMode.Word };
                unlockLbl.AddThemeColorOverride("font_color",   CFood);
                unlockLbl.AddThemeFontSizeOverride("font_size", 13);
                unlockLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                col.AddChild(unlockLbl);
            }
        }

        private void AddLabel(string text, Color color, int size)
        {
            var lbl = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.Word };
            lbl.AddThemeColorOverride("font_color",   color);
            lbl.AddThemeFontSizeOverride("font_size", size);
            lbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _contentCol.AddChild(lbl);
        }

        private void AddSpacer(int h)
        {
            var s = new Control { CustomMinimumSize = new Vector2(0, h) };
            _contentCol.AddChild(s);
        }

        // ================================================================
        //  HELPERS DE ESTILO
        // ================================================================

        private static StyleBoxFlat FlatStyle(Color bg)
        {
            return new StyleBoxFlat { BgColor = bg };
        }

        private static StyleBoxFlat RoundedStyle(Color bg, int radius)
        {
            var s = new StyleBoxFlat { BgColor = bg };
            s.SetCornerRadiusAll(radius);
            s.ContentMarginLeft  = 0; s.ContentMarginRight  = 0;
            s.ContentMarginTop   = 0; s.ContentMarginBottom = 0;
            return s;
        }

        private static string GetUnitEmoji(UnitType t) => t switch
        {
            UnitType.Settler       => "🏘",
            UnitType.Worker        => "⛏",
            UnitType.Scout         => "🏃",
            UnitType.Warrior       => "🗡",
            UnitType.Archer        => "🏹",
            UnitType.Longbowman    => "🏹",
            UnitType.Swordsman     => "⚔",
            UnitType.Knight        => "🐴",
            UnitType.Ballista      => "🎯",
            UnitType.Longswordsman => "🗡",
            UnitType.Musketman     => "🔫",
            _                      => "🪖",
        };
    }
}
