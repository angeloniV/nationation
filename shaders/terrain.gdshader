shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_lambert_wrap, specular_schlick_ggx;

// ── Uniforms ──────────────────────────────────────────────────────
uniform sampler2D tile_type_tex : hint_default_black, filter_nearest, repeat_disable;
uniform sampler2D tile_fog_tex  : hint_default_black, filter_nearest, repeat_disable;
uniform vec2      map_size      = vec2(60.0, 40.0);
uniform float     hex_size      = 4.0;
uniform bool      show_grid     = false;
uniform float     grid_alpha    = 0.22;
uniform float     water_time    = 0.0;

// ── Posición mundo interpolada desde el vertex shader ────────────
varying vec3 v_world_pos;

// ── Vertex: captura posición mundo antes de la transformación MVP ──
void vertex() {
    v_world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

// ── Helpers de color por bioma (funciones, sin arrays const) ──────
vec3 biome_color(int ty) {
    if (ty == 0) return vec3(0.06, 0.20, 0.52);  // Ocean
    if (ty == 1) return vec3(0.12, 0.38, 0.68);  // Coast
    if (ty == 2) return vec3(0.72, 0.76, 0.34);  // Plains
    if (ty == 3) return vec3(0.22, 0.60, 0.18);  // Grassland
    if (ty == 4) return vec3(0.54, 0.48, 0.26);  // Hills
    if (ty == 5) return vec3(0.06, 0.36, 0.10);  // Forest
    if (ty == 6) return vec3(0.48, 0.46, 0.46);  // Mountains
    if (ty == 7) return vec3(0.86, 0.80, 0.40);  // Desert
    if (ty == 8) return vec3(0.62, 0.70, 0.70);  // Tundra
    return vec3(0.86, 0.90, 0.94);               // Arctic (9)
}

float biome_rough(int ty) {
    if (ty == 0) return 0.04;
    if (ty == 1) return 0.06;
    return 0.88;
}

// ── Coordenadas axiales ───────────────────────────────────────────
vec2 world_to_axial(vec2 xz) {
    float q = (0.57735 * xz.x - 0.33333 * xz.y) / hex_size;
    float r = (0.66667 * xz.y) / hex_size;
    return vec2(q, r);
}

ivec2 axial_round(vec2 fr) {
    float q = fr.x, r = fr.y, s = -q - r;
    float rq = round(q), rr = round(r), rs = round(s);
    float dq = abs(rq - q), dr = abs(rr - r), ds = abs(rs - s);
    if (dq > dr && dq > ds) rq = -rr - rs;
    else if (dr > ds)       rr = -rq - rs;
    return ivec2(int(rq), int(rr));
}

// ── Muestrea tipo de tile (0..9) de la textura ───────────────────
float tile_type_at(ivec2 qr) {
    if (qr.x < 0 || qr.y < 0) return 0.0;
    if (float(qr.x) >= map_size.x || float(qr.y) >= map_size.y) return 0.0;
    vec2 uv = (vec2(float(qr.x), float(qr.y)) + 0.5) / map_size;
    return round(texture(tile_type_tex, uv).r * 9.0);
}

// ── Centro mundo de un tile axial ────────────────────────────────
vec2 tile_center(ivec2 qr) {
    float cx = hex_size * 1.73205 * (float(qr.x) + float(qr.y) * 0.5);
    float cz = hex_size * 1.5 * float(qr.y);
    return vec2(cx, cz);
}

// ── Blend IDW entre el tile y sus 6 vecinos ──────────────────────
vec3 sample_blended(vec2 xz, ivec2 c) {
    vec3  col  = vec3(0.0);
    float wsum = 0.0;

    // centro + 6 vecinos axiales
    ivec2 nb;
    float dist, w;
    int ty;

    // Construye los 7 vecinos en un loop manual (máx compatibilidad GDShader)
    for (int i = 0; i < 7; i++) {
        if      (i == 0) nb = c;
        else if (i == 1) nb = c + ivec2( 1,  0);
        else if (i == 2) nb = c + ivec2(-1,  0);
        else if (i == 3) nb = c + ivec2( 0,  1);
        else if (i == 4) nb = c + ivec2( 0, -1);
        else if (i == 5) nb = c + ivec2( 1, -1);
        else             nb = c + ivec2(-1,  1);

        ty   = int(tile_type_at(nb));
        dist = length(xz - tile_center(nb));
        w    = 1.0 / max(dist * dist, 0.0001);
        col  += biome_color(ty) * w;
        wsum += w;
    }
    return col / wsum;
}

// ── Proximidad al borde hex [0=centro, 1=borde] ──────────────────
float hex_edge_proximity(vec2 xz, ivec2 c) {
    float d = length(xz - tile_center(c));
    return d / (hex_size * 0.866);
}

// ── Fog del tile ─────────────────────────────────────────────────
vec2 fog_at(ivec2 qr) {
    if (qr.x < 0 || qr.y < 0) return vec2(0.0);
    if (float(qr.x) >= map_size.x || float(qr.y) >= map_size.y) return vec2(0.0);
    vec2 uv = (vec2(float(qr.x), float(qr.y)) + 0.5) / map_size;
    return texture(tile_fog_tex, uv).rg;
}

// ── Fragment ──────────────────────────────────────────────────────
void fragment() {
    // Coordenadas mundo (NO view-space)
    vec2  xz  = v_world_pos.xz;
    vec2  fqr = world_to_axial(xz);
    ivec2 cqr = axial_round(fqr);
    int   tyC = int(tile_type_at(cqr));

    // Color suavizado por bioma con IDW
    vec3 col = sample_blended(xz, cqr);

    // Micro-detalle procedural
    float noise = fract(sin(dot(xz * 0.8, vec2(127.1, 311.7))) * 43758.5);
    col += (noise - 0.5) * 0.022;

    // Animación de agua (Ocean=0, Coast=1)
    if (tyC <= 1) {
        float w1 = sin(xz.x * 0.6 + water_time * 1.2);
        float w2 = sin(xz.y * 0.5 + water_time * 0.9);
        col += vec3(0.0, 0.025, 0.06) * (w1 * w2);
    }

    // Grilla hex solo cuando show_grid=true
    if (show_grid) {
        float ep   = hex_edge_proximity(xz, cqr);
        float line = smoothstep(0.84, 0.96, ep);
        col = mix(col, vec3(0.9, 0.95, 1.0), line * grid_alpha);
    }

    // Fog of war
    vec2  fog   = fog_at(cqr);
    float vis   = fog.r;
    float explo = fog.g;
    vec3  dark  = vec3(0.02, 0.02, 0.05);
    vec3  semi  = col * 0.28 + vec3(0.03, 0.04, 0.07);
    col = mix(dark, mix(semi, col, vis), max(vis, explo * 0.55));

    ALBEDO    = col;
    ROUGHNESS = biome_rough(tyC);
    METALLIC  = (tyC <= 1) ? 0.28 : 0.0;
}
