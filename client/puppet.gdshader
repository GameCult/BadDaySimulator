shader_type spatial;
render_mode cull_back;

// The puppet is one mesh skinned to the bodies: each vertex carries two bones and the first one's weight in CUSTOM1
// (x: bone A, y: bone B, z: weight of A). bones[i] is world-from-spread-bind for body i. The shadow and depth passes
// run vertex() too, so they skin the same way. NORMAL is the facet normal, so the shading is flat.
const int MAX_BONES = 32;
uniform mat4 bones[MAX_BONES];
uniform vec3 albedo : source_color = vec3(0.9, 0.9, 0.88);

void vertex() {
	int a = int(CUSTOM1.x + 0.5);
	int b = int(CUSTOM1.y + 0.5);
	float w = CUSTOM1.z;
	mat4 m = bones[a] * w + bones[b] * (1.0 - w);
	VERTEX = (m * vec4(VERTEX, 1.0)).xyz;
	NORMAL = normalize(mat3(m) * NORMAL);
}

void fragment() {
	ALBEDO = albedo;
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}
