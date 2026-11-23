// The PS1 framebuffer: one colour per block of the line grid, 15-bit colour with the console's 4x4 ordered dither. The block grid is
// anchored at the screen centre, as the vertex snap is. A full-screen ColorRect under the HUD runs it.
shader_type canvas_item;

uniform sampler2D screen_tex : hint_screen_texture, filter_nearest;
global uniform vec2 ps1_res;

const mat4 D = mat4(vec4(-4., 0., -3., 1.), vec4(2., -2., 3., -1.), vec4(-3., 1., -4., 0.), vec4(3., -1., 2., -2.));

void fragment() {
	vec2 size = 1.0 / SCREEN_PIXEL_SIZE;
	float s = size.y / ps1_res.y;
	vec2 b = floor((FRAGCOORD.xy - size * 0.5) / s);
	vec3 c = textureLod(screen_tex, (size * 0.5 + (b + 0.5) * s) / size, 0.0).rgb * 255.0;
	ivec2 q = ivec2(mod(b, 4.0));
	c = clamp(c + D[q.x][q.y], 0.0, 255.0);
	COLOR = vec4(floor(c / 8.0) * 8.0 / 255.0, 1.0);
}
