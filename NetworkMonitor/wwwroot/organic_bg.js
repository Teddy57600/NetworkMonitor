import * as THREE from 'https://cdn.jsdelivr.net/npm/three@0.160.0/build/three.module.js';
import GUI from 'https://unpkg.com/lil-gui@0.19.1/dist/lil-gui.esm.min.js';

// --------------------------------------------------------
// 1. Scene Setup
// --------------------------------------------------------
const scene = new THREE.Scene();

// Orthographic camera is perfect for 2D screen-space shaders
const camera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.1, 10);
camera.position.z = 0;

const renderer = new THREE.WebGLRenderer({ antialias: false });
const pixelRatio = Math.min(window.devicePixelRatio, 2);
renderer.setSize(window.innerWidth, window.innerHeight);
renderer.setPixelRatio(pixelRatio); // Limit pixel ratio for performance

// --- FORÇAGE ABSOLU DES STYLES EN JS ---
renderer.domElement.id = 'organic-bg';
renderer.domElement.style.setProperty('position', 'fixed', 'important');
renderer.domElement.style.setProperty('top', '0', 'important');
renderer.domElement.style.setProperty('left', '0', 'important');
renderer.domElement.style.setProperty('width', '100vw', 'important');
renderer.domElement.style.setProperty('height', '100vh', 'important');
renderer.domElement.style.setProperty('z-index', '-999', 'important');
renderer.domElement.style.setProperty('pointer-events', 'none', 'important');
renderer.domElement.style.setProperty('margin', '0', 'important');
renderer.domElement.style.setProperty('padding', '0', 'important');
// ----------------------------------------


document.body.appendChild(renderer.domElement);

// --------------------------------------------------------
// 2. Shader Material & Geometry
// --------------------------------------------------------

// Setup uniforms including resolution, time, and custom GUI parameters
const uniforms = {
    iTime: { value: 0.0 },
    iResolution: { value: new THREE.Vector3(window.innerWidth * pixelRatio, window.innerHeight * pixelRatio, 1.0) },
    uSpeed: { value: 0.6 },
    uScaleX: { value: 2.0 },
    uScaleY: { value: 2.0 },
    uColorOffset: { value: 3.0 },
    uIterLimit: { value: 10.0 },
    uRoundness: { value: 1.0 }, // New parameter for corner rounding
    uColor1: { value: new THREE.Color('#004cff') },
    uColor2: { value: new THREE.Color('#03123f') },
    uColor3: { value: new THREE.Color('#2e89ff') }
};

const vertexShader = `
            void main() {
                // Render a full screen quad
                gl_Position = vec4(position, 1.0);
            }
        `;

const fragmentShader = `
            uniform vec3 iResolution;
            uniform float iTime;
            
            uniform float uSpeed;
            uniform float uScaleX;
            uniform float uScaleY;
            uniform float uColorOffset;
            uniform float uIterLimit;
            uniform float uRoundness;
            uniform vec3 uColor1;
            uniform vec3 uColor2;
            uniform vec3 uColor3;

            // Using GLSL 3.0 out color
            out vec4 fragColor;

            // Random function for dithering
            float random(vec2 st) {
                return fract(sin(dot(st.xy, vec2(12.9898, 78.233))) * 43758.5453123);
            }

            void mainImage(out vec4 O, vec2 I) {
                // Initialize variables safely for GLSL
                float i = 0.0, z = 0.0, d = 0.0;
                O = vec4(0.0); 
                
                // Raymarch steps based on GUI limit
                for(O *= i; i++ < uIterLimit;) {
                    // Compute raymarch point from raymarch distance and ray direction
                    vec3 p = z * normalize(vec3(I + I, 0.0) - iResolution.xyy);
                    vec3 v;
                    
                    // --- REVERTED TO THE GOOD WAVE MATH ---
                    // This brings back the clear, glowing neon lines!
                    p.x += sin(p.x + iTime * uSpeed * 0.5) + cos(p.y + iTime * uSpeed * 0.3);
                    p.y += cos(p.x - iTime * uSpeed * 0.4) + sin(p.y + iTime * uSpeed * 0.6);
                    p.z += sin(iTime * uSpeed * 0.2) * 1.5;
                    
                    // Modifying space scale
                    p.x *= uScaleX;
                    p.y *= uScaleY;
                    
                    // Compute distance for sine pattern. 
                    v = cos(p) - sin(p).yzx;
                    
                    // --- NEW WAY TO ROUND CORNERS ---
                    // The sharp cuts come from max(). Instead of complex smooth-max math,
                    // we simply fade out the sharp 'max()' cut into the pure, naturally smooth 
                    // trigonometric field 'v'.
                    vec3 shape = mix(max(v, v.yzx * 0.2), v, uRoundness);
                    
                    z += d = 1e-4 + 0.5 * length(shape);
                    
                    // --- CUSTOM COLORS BLENDING ---
                    // Calculate weights based on spatial coordinates
                    vec3 weights = abs(cos(p));
                    weights /= dot(weights, vec3(1.0)); // Normalize weights
                    
                    // Mix the three selected colors
                    vec3 customColor = uColor1 * weights.x + uColor2 * weights.y + uColor3 * weights.z;
                    
                    // Use mixed color and uColorOffset as intensity multiplier
                    O.rgb += (customColor * uColorOffset) / d;
                }
                
                // --- BRIGHTNESS ---
                // Tonemapping divisor reduced (was 1e3 / 1000) to make the image much brighter
                O /= O + 300.0;
                
                // --- SATURATION ---
                float luminance = dot(O.rgb, vec3(0.299, 0.587, 0.114));
                O.rgb = mix(vec3(luminance), O.rgb, 1.6); // 1.6x saturation boost
                
                // --- DITHERING ---
                // Add a tiny amount of static noise to eliminate color banding (лесенки)
                O.rgb += (random(I) - 0.5) / 128.0;
                
                O.a = 1.0;
            }

            void main() {
                // Pass fragColor and coordinates to the Shadertoy-like mainImage
                mainImage(fragColor, gl_FragCoord.xy);
            }
        `;

// Create a plane geometry that covers the entire screen
const geometry = new THREE.PlaneGeometry(2, 2);

const material = new THREE.ShaderMaterial({
    vertexShader: vertexShader,
    fragmentShader: fragmentShader,
    uniforms: uniforms,
    glslVersion: THREE.GLSL3 // Necessary for features like tanh()
});

const mesh = new THREE.Mesh(geometry, material);
scene.add(mesh);

// --------------------------------------------------------
// 3. lil-gui Setup (Settings Panel)
// --------------------------------------------------------
const gui = new GUI({ title: 'Shader Settings' });

const params = {
    speed: uniforms.uSpeed.value,
    scaleX: uniforms.uScaleX.value,
    scaleY: uniforms.uScaleY.value,
    colorOffset: uniforms.uColorOffset.value,
    iterations: uniforms.uIterLimit.value,
    roundness: uniforms.uRoundness.value,
    color1: '#004cff',
    color2: '#03123f',
    color3: '#2e89ff'
};

// Link GUI updates to Shader Uniforms
gui.add(params, 'speed', 0.0, 2.0).name('Animation Speed').onChange(v => uniforms.uSpeed.value = v);
gui.add(params, 'scaleX', 0.1, 2.0).name('Space Scale X').onChange(v => uniforms.uScaleX.value = v);
gui.add(params, 'scaleY', 0.1, 2.0).name('Space Scale Y').onChange(v => uniforms.uScaleY.value = v);
gui.add(params, 'colorOffset', 0.0, 5.0).name('Color Intensity').onChange(v => uniforms.uColorOffset.value = v);
gui.add(params, 'iterations', 1, 10).step(1).name('Ray Steps (Iter)').onChange(v => uniforms.uIterLimit.value = v);
gui.add(params, 'roundness', 0.0, 1.0).name('Corner Roundness').onChange(v => uniforms.uRoundness.value = v);
gui.addColor(params, 'color1').name('Color 1').onChange(v => uniforms.uColor1.value.set(v));
gui.addColor(params, 'color2').name('Color 2').onChange(v => uniforms.uColor2.value.set(v));
gui.addColor(params, 'color3').name('Color 3').onChange(v => uniforms.uColor3.value.set(v));

// --------------------------------------------------------
// 4. Resize Handling
// --------------------------------------------------------
window.addEventListener('resize', () => {
    const width = window.innerWidth;
    const height = window.innerHeight;

    renderer.setSize(width, height);
    uniforms.iResolution.value.set(width * pixelRatio, height * pixelRatio, 1.0);
});

// --------------------------------------------------------
// 5. Animation Loop
// --------------------------------------------------------
const clock = new THREE.Clock();

function animate() {
    requestAnimationFrame(animate);

    // Update time uniform for the shader
    uniforms.iTime.value = clock.getElapsedTime();

    renderer.render(scene, camera);
}

animate();