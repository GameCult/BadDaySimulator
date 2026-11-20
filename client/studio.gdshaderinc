// The studio the glaze reflects: a dark room, one large soft box (key), two tall strip lights (rims) and a dim floor. Look owns the
// numbers and feeds the same ones to the punctual lights, so the lights and the reflections are one studio. The directions are the
// directions the light comes from, in view space when studio_in_view (the studio turns with the camera, as a photographer's lights do),
// else in world space. Radiances are in the render's linear units and are meant to clip where the glaze catches them.
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
uniform bool studio_in_view = true;

// A rectangular emitter seen from the origin along c, w by h on the plane at unit distance, soft at its edge.
float studio_box(vec3 d, vec3 c, vec2 half_size) {
	vec3 up = abs(c.y) > 0.9 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
	vec3 u = normalize(cross(up, c));
	vec3 v = cross(c, u);
	float f = dot(d, c);
	if (f <= 0.0) return 0.0;
	vec2 q = vec2(dot(d, u), dot(d, v)) / f;
	vec2 e = smoothstep(half_size, half_size * 0.8, abs(q));
	return e.x * e.y;
}

vec3 studio(vec3 d) {
	vec3 c = studio_room;
	c += studio_key_radiance * studio_box(d, normalize(studio_key_dir), vec2(studio_key_size, studio_key_size * 0.7)) * vec3(1.0, 0.98, 0.95);
	c += studio_rim_radiance * studio_box(d, normalize(studio_rim_a), studio_rim_size) * studio_rim_tint;
	c += studio_rim_radiance * studio_box(d, normalize(studio_rim_b), studio_rim_size) * studio_rim_tint;
	c += studio_floor * smoothstep(0.0, -0.3, d.y) * vec3(1.0);
	return c;
}
