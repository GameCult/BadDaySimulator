shader_type spatial;
render_mode cull_back;

// The puppet is one mesh skinned to the bodies: each vertex carries two bones and the first one's weight in CUSTOM1
// (x: bone A, y: bone B, z: weight of A). bones[i] is world-from-spread-bind for body i. The shadow and depth passes
// run vertex() too, so they skin the same way. NORMAL is the facet normal, turned by the skin blend.
//
// The soft bevel is the facet normal tilted toward each edge's bisector, within bevel_width of the edge. Two more attributes
// carry what it needs from the mesh, the same at the three corners of a triangle where noted:
//   CUSTOM0: vertex k holds its own altitude h_k in component k and 0 in the others (so the interpolated component k is the
//            distance, in bind metres, to edge k, the edge opposite vertex k); w is the triangle's smallest altitude.
//   CUSTOM2: the triangle's three creases, crease[k] for edge k: half the turn from this facet's normal to the neighbour's,
//            positive where the edge is convex (PuppetShape.Crease).
// The in-plane direction to each edge comes from screen derivatives of the distances, so the shader never evaluates the SDF.
// At an edge the tilt is exactly the bisector from both sides, so the normal is continuous across it.
const int MAX_BONES = 32;
uniform mat4 bones[MAX_BONES];
uniform vec3 albedo : source_color = vec3(0.9, 0.9, 0.88);
uniform float bevel_width = 0.0;
uniform bool show_normals = false; // RenderCheck's view of the shading normal: the normal in view space as emission, rgb = n * 0.5 + 0.5.

varying vec4 edge;
varying vec3 crease;

void vertex() {
	int a = int(CUSTOM1.x + 0.5);
	int b = int(CUSTOM1.y + 0.5);
	float w = CUSTOM1.z;
	mat4 m = bones[a] * w + bones[b] * (1.0 - w);
	VERTEX = (m * vec4(VERTEX, 1.0)).xyz;
	NORMAL = normalize(mat3(m) * NORMAL);
	edge = CUSTOM0;
	crease = CUSTOM2.xyz;
}

void fragment() {
	vec3 n = normalize(NORMAL);
	// Derivatives are undefined in non-uniform control flow: take them all here, branch afterwards.
	vec3 dx = dFdx(VERTEX);
	vec3 dy = dFdy(VERTEX);
	vec3 r1 = cross(dy, n);
	vec3 r2 = cross(n, dx);
	float det = dot(dx, r1);
	vec3 fx = dFdx(edge.xyz);
	vec3 fy = dFdy(edge.xyz);
	float width = min(bevel_width, 0.5 * edge.w);
	if (width > 0.0 && abs(det) > 1e-20) {
		vec3 tilt = vec3(0.0);
		for (int k = 0; k < 3; k++) {
			// The in-plane gradient of the distance to edge k points into the facet; the normal tips the other way.
			vec3 grad = (fx[k] * r1 + fy[k] * r2) / det;
			float len = length(grad);
			if (crease[k] != 0.0 && len > 1e-12) {
				float s = 1.0 - smoothstep(0.0, width, edge[k]);
				tilt += s * tan(clamp(crease[k], -1.4, 1.4)) * grad / len;
			}
		}
		n = normalize(n - tilt);
	}
	NORMAL = n;
	ALBEDO = show_normals ? vec3(0.0) : albedo;
	EMISSION = show_normals ? n * 0.5 + 0.5 : vec3(0.0);
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}
