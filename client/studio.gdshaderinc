// The studio the glaze reflects: a dark room, one large soft box (key), two tall strip lights (rims) and a dim floor. Look owns the
// numbers and feeds the same ones to the punctual lights, so the lights and the reflections are one studio. The directions are the
// directions the light comes from, in view space (the studio turns with the camera, as a photographer's lights do). Radiances are in the render's linear units and are meant to clip where the glaze catches them.
// The studio is prefiltered: it takes the spread of the pixel's normal and returns the average over the directions the pixel reflects, so a highlight does not flicker as the camera moves.
uniform vec3 studio_room = vec3(0.0);
uniform vec3 studio_key_dir = vec3(0.0, 1.0, 0.0);
uniform float studio_key_size = 0.0;
uniform float studio_key_radiance = 0.0;
uniform vec3 studio_rim_a = vec3(0.0, 1.0, 0.0);
uniform vec3 studio_rim_b = vec3(0.0, 1.0, 0.0);
uniform vec2 studio_rim_size = vec2(0.0);
uniform vec3 studio_rim_tint = vec3(1.0);
uniform float studio_rim_radiance = 0.0;
uniform float studio_floor = 0.0;

// A rectangular emitter seen from the origin along c, w by h on the plane at unit distance, prefiltered by the pixel's normal spread a2
// (GGX alpha^2; the shader's kernel). A reflected direction that spreads by variance 2 a2 per axis spreads (1 + |q|^2) times wider on the
// plane at unit distance, so each axis of the box (half width 0.9 h) is averaged over a box kernel of half width w (variance w^2 / 3: the
// lobe's 2 a2 times that stretch squared, plus the authored edge's own (0.1 h)^2). The result is a trapezoid: a linear ramp from 0.8 h to h
// at a2 = 0 (the authored edge was a smoothstep over the same span, so the two differ by under a tenth of the box's level), widening as a2
// grows. The box's integral over the reflected directions is kept to about 6% up to a2 = 0.05; at the 0.18 clamp about a third of it remains,
// where the filter has stopped following the lobe and the highlight dims rather than brightens.
float studio_box(vec3 d, vec3 c, vec2 half_size, float a2) {
	vec3 up = abs(c.y) > 0.9 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
	vec3 u = normalize(cross(up, c));
	vec3 v = cross(c, u);
	float f = dot(d, c);
	if (f <= 0.0) return 0.0;
	vec2 q = abs(vec2(dot(d, u), dot(d, v)) / f);
	float stretch = 1.0 + dot(q, q);
	vec2 edge = 0.9 * half_size;
	vec2 w = max(sqrt(0.01 * half_size * half_size + 6.0 * a2 * stretch * stretch), vec2(1e-6));
	vec2 e = max(min(q + w, edge) - max(q - w, -edge), 0.0) / (2.0 * w);
	return e.x * e.y;
}

vec3 studio(vec3 d, float a2) {
	vec3 c = studio_room;
	c += studio_key_radiance * studio_box(d, normalize(studio_key_dir), vec2(studio_key_size, studio_key_size * 0.7), a2) * vec3(1.0, 0.98, 0.95);
	c += studio_rim_radiance * studio_box(d, normalize(studio_rim_a), studio_rim_size, a2) * studio_rim_tint;
	c += studio_rim_radiance * studio_box(d, normalize(studio_rim_b), studio_rim_size, a2) * studio_rim_tint;
	c += studio_floor * smoothstep(0.0, -0.3, d.y) * vec3(1.0);
	return c;
}
