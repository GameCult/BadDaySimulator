// The PS1 look, a port of eureka-site prototype/ps1.js (MIT): every vertex snapped to the low-res grid, affine texture coordinates,
// unfiltered texels, per-vertex (Gouraud) light and fog. The arithmetic is the console's, in raw sRGB values with no colour management:
// the compatibility renderer writes ALBEDO to the screen as it is, so nothing converts. The framebuffer's colour depth and dither are ps1_post.gdshader. Look owns every number.
// Look.Ps1 makes the translucent variant by defining TRANSLUCENT ahead of this source: blended, no depth write, both faces.
shader_type spatial;
#ifdef TRANSLUCENT
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled;
#else
render_mode unshaded, cull_back;
#endif

global uniform vec2 ps1_res;
global uniform vec3 ps1_key_dir;
global uniform vec3 ps1_key_color;
global uniform vec3 ps1_ambient;
global uniform vec3 ps1_fog_color;
global uniform float ps1_fog_near;
global uniform float ps1_fog_far;
global uniform vec3 ps1_glow_pos;
global uniform vec3 ps1_glow_color;
global uniform float ps1_glow_energy;

uniform sampler2D albedo_tex : hint_default_white, filter_nearest, repeat_enable;
uniform vec3 tint = vec3(1.0);
uniform float alpha = 1.0;
uniform bool emissive = false;
uniform float tile = 0.0; // metres per texture repeat on the world's axes; 0 takes the mesh's own UV

varying vec3 auv;
varying vec3 light;
varying float fog;

void vertex() {
	vec4 mv = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	vec4 clip = PROJECTION_MATRIX * mv;
	if (clip.w > 0.05) {
		vec2 h = ps1_res * 0.5;
		clip.xy = floor(clip.xy / clip.w * h + 0.5) / h * clip.w; // the GTE had no sub-pixel precision
	}
	POSITION = clip;

	vec3 p = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 n = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	vec2 uv = UV;
	if (tile > 0.0) {
		vec3 a = abs(n);
		uv = (a.y >= a.x && a.y >= a.z ? p.xz : (a.x >= a.z ? p.zy : p.xy)) / tile;
	}
	auv = vec3(uv * clip.w, clip.w); // pre-multiplied by w: the GPU's perspective division cancels, the texture is affine

	vec3 to_glow = ps1_glow_pos - p;
	float d = length(to_glow);
	float near = clamp(1.0 - d / 0.9, 0.0, 1.0);
	vec3 glow = ps1_glow_color * ps1_glow_energy * max(dot(n, to_glow / max(d, 1e-4)), 0.0) * near * near;
	light = emissive ? vec3(1.0) : ps1_ambient + ps1_key_color * max(dot(n, ps1_key_dir), 0.0) + glow;
	fog = smoothstep(ps1_fog_near, ps1_fog_far, -mv.z);
}

void fragment() {
	vec3 c = texture(albedo_tex, auv.xy / auv.z).rgb * tint * light;
	c = mix(c, ps1_fog_color, fog);
	ALBEDO = c;
#ifdef TRANSLUCENT
	ALPHA = alpha;
#endif
}
