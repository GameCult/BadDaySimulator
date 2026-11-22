shader_type spatial;
render_mode cull_back;
#include "res://studio.gdshaderinc"

// The puppet is one mesh that the CPU skins every frame (PuppetSkin), so the shader draws what it is given and moves
// nothing: VERTEX is already where the body is, and the colour, depth and shadow passes all draw those positions.
//
// The soft bevel is the normal tilted toward each edge's bisector, within a width of the edge, and blended to the smooth
// vertex normal where edges meet. The edges are those of the triangle's source (PuppetShade): the triangle itself where it is its own
// source (a ball, an uncut body, a decimated triangle the cut left whole), the decimated triangle or the cutter triangle it is a fragment of
// where a socket was cut, so the bevel does not change from fragment to fragment of one source and a rim, where a body's surface meets a
// socket's wall, has no edge to band. Each corner of a triangle carries:
//   CUSTOM0: component k is the corner's distance, in metres along the source's plane and positive inside it, to line k, the line of
//            the source's edge opposite its corner k (affine on the plane, so the interpolated component is the distance at the pixel);
//            w is the source's smallest altitude.
//   CUSTOM1: the source's three creases, crease[k] for line k: half the turn from the source's normal to its neighbour's across that edge,
//            positive where the edge is convex, the same on both sides of the edge.
//   CUSTOM2: the source's normal (NORMAL in vertex()).
//   CUSTOM3: this corner's vertex normal, the source's corner vertex normals interpolated to the corner (area-weighted over a body's
//            triangles, by angle over a cutter's); interpolated, it is the same on both sides of a source's edge.
// The normals are posed with their body each frame; the distances, creases and caps are a piece's own and never change, the piece being
// rigid. The in-plane direction to each line comes from screen derivatives of the distances, so the shader never evaluates the SDF. At an
// edge the tilt is exactly the bisector from both sides, so the normal is continuous across it.
//
// The width is on the screen: bevel_screen is a fraction of the viewport's height and the distance across one pixel (metres per pixel,
// from the derivatives of the distance to the edge) turns it into metres; bevel_min floors it, and half the source's smallest altitude
// caps it so the bands of one source never meet. A world width is bevel_screen 0 and bevel_min the width.
//
// The puppet is glazed porcelain. The bisque is the PBR surface (roughness, specular, backlight) lit by Look's studio lights. The glaze
// is analytic: Fresnel times the studio (studio.gdshaderinc) in the direction the shading normal reflects the view, added as emission, so
// it needs no radiance map and draws the same under gl_compatibility, whose clearcoat sees punctual lights only. The bevel's tilted
// normal feeds it, so the chamfers glint. Where two facets meet in a concave crease the glaze pools: the albedo goes darker and cooler,
// and warmer over a convex bevel. Every number comes from Look.Glaze.
//
// The highlights are prefiltered where they are decided (geometric specular antialiasing): the shading normal changes across a pixel
// along the bevel's chamfers and at facet edges, so a point sample of the studio in the reflected direction flickers as the camera moves.
// The spread of the normal over the pixel, from its screen derivatives, widens the studio the glaze reads, so each pixel reflects the
// studio's average over the directions it covers. (The bisque's roughness is left as it is: widening it by the same spread changed no pixel
// at any framing, the bisque's specular being dim.) MSAA (project.godot) antialiases the silhouette and the
// triangle edges, which this does not touch.
uniform vec3 albedo : source_color = vec3(0.9, 0.9, 0.88);
uniform float bevel_screen = 0.0;
uniform float bevel_min = 0.0;
uniform float roughness = 1.0;
uniform float specular = 0.0;
uniform vec3 backlight = vec3(0.0);
uniform float glaze = 0.0; // 0 is the bisque alone
uniform float glaze_pool = 0.0;
uniform bool show_normals = false; // RenderCheck's view of the shading normal: the normal in view space as emission, rgb = n * 0.5 + 0.5.

varying vec4 edge;
varying vec3 crease;
varying vec3 vertex_normal;

void vertex() {
	NORMAL = CUSTOM2.xyz;
	edge = CUSTOM0;
	crease = CUSTOM1.xyz;
	vertex_normal = (MODELVIEW_MATRIX * vec4(CUSTOM3.xyz, 0.0)).xyz;
}

void fragment() {
	vec3 n = normalize(NORMAL);
	// Derivatives are undefined in non-uniform control flow: take them all here, branch afterwards.
	vec3 dx = dFdx(VERTEX);
	vec3 dy = dFdy(VERTEX);
	vec3 fx = dFdx(edge.xyz);
	vec3 fy = dFdy(edge.xyz);
	vec3 r1 = cross(dy, n);
	vec3 r2 = cross(n, dx);
	float det = dot(dx, r1);
	if (abs(det) > 1e-20) {
		vec3 g[3];
		float width[3];
		vec3 tilt = vec3(0.0);
		for (int k = 0; k < 3; k++) {
			// The in-plane gradient of the distance to edge k points into the facet; the normal tips the other way.
			vec3 grad = (fx[k] * r1 + fy[k] * r2) / det;
			float len = length(grad);
			g[k] = len > 1e-12 ? grad / len : vec3(0.0);
			float metres_per_pixel = length(vec2(fx[k], fy[k]));
			width[k] = min(max(bevel_screen * VIEWPORT_SIZE.y * metres_per_pixel, bevel_min), 0.5 * edge.w);
			if (crease[k] != 0.0 && width[k] > 0.0) {
				float s = 1.0 - smoothstep(0.0, width[k], edge[k]);
				tilt += s * tan(clamp(crease[k], -1.4, 1.4)) * g[k];
			}
		}
		// Near a corner two edges' bands overlap and their tilts add up to a notch; there the normal goes to the smooth vertex normal instead.
		// Corner k is where edges i and j meet, and its exact distance from the pixel follows from the two edge distances and the angle between them.
		float corner = 0.0;
		for (int k = 0; k < 3; k++) {
			int i = (k + 1) % 3;
			int j = (k + 2) % 3;
			float c = dot(g[i], g[j]);
			float r_squared = (edge[i] * edge[i] + edge[j] * edge[j] - 2.0 * edge[i] * edge[j] * c) / max(1.0 - c * c, 1e-6);
			float reach = 1.5 * max(width[i], width[j]);
			if (reach > 0.0) corner = max(corner, 1.0 - smoothstep(0.0, reach, sqrt(max(r_squared, 0.0))));
		}
		n = normalize(n - (1.0 - corner) * tilt);
		n = normalize(mix(n, normalize(vertex_normal), corner));
	}
	NORMAL = n;
	// The pixel's normal spread as GGX alpha^2 (Tokuyoshi and Kaplanyan 2021, JCGT 10(2) 31-58, Listing 5) with the pixel filter's variance
	// 1/24 and the clamp of Kaplanyan et al. 2016 (HPG) at 0.18. 1/24 is half of a box one pixel wide's own variance (1/12) and an eighth of the
	// paper's 1/(2 pi); RenderCheck's flicker check holds it from both sides: with 1/24 the highlights stop crawling, and at 1/12 or more they
	// are blurred below the supersampled reference's brightness. Derivatives are taken here, after every branch has joined, in uniform control flow.
	const float PIXEL_VARIANCE = 1.0 / 24.0;
	const float MAX_SPREAD = 0.18;
	float kernel = min(2.0 * PIXEL_VARIANCE * (dot(dFdx(n), dFdx(n)) + dot(dFdy(n), dFdy(n))), MAX_SPREAD);
	// The creases within 6 mm of an edge: concave tints the albedo cooler and darker, convex warmer.
	float convex = 0.0, concave = 0.0;
	for (int k = 0; k < 3; k++) {
		float near = 1.0 - smoothstep(0.0, 0.006, edge[k]);
		convex = max(convex, near * clamp(crease[k] * 2.0, 0.0, 1.0));
		concave = max(concave, near * clamp(-crease[k] * 2.0, 0.0, 1.0));
	}
	vec3 a = albedo;
	a = mix(a, a * vec3(0.80, 0.82, 0.86), glaze_pool * concave);
	a = mix(a, a * vec3(1.02, 0.99, 0.94), glaze_pool * convex);
	ALBEDO = show_normals ? vec3(0.0) : a;
	// RenderCheck's normal view is the emission alone: no light, however shiny the bisque, may add to it.
	ROUGHNESS = show_normals ? 1.0 : roughness;
	SPECULAR = show_normals ? 0.0 : specular;
	BACKLIGHT = show_normals ? vec3(0.0) : backlight;
	EMISSION = show_normals ? n * 0.5 + 0.5 : vec3(0.0);
	if (!show_normals && glaze > 0.0) {
		vec3 r = reflect(-VIEW, n);
		float f = 0.04 + 0.96 * pow(1.0 - clamp(dot(n, VIEW), 0.0, 1.0), 5.0);
		EMISSION += glaze * f * studio(r, kernel);
	}
}
