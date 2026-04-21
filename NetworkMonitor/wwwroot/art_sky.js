import * as THREE from 'https://cdn.jsdelivr.net/npm/three@0.160.0/build/three.module.js';
import GUI from 'https://unpkg.com/lil-gui@0.19.1/dist/lil-gui.esm.min.js';

// --- THEMES DEFINITION ---
const THEMES = {
    orange: { main: [1.0, 0.95, 0.7], low: [0.95, 0.75, 0.4], mid: [0.98, 0.7, 0.6], high: [1.0, 1.0, 1.0] },
    blue: { main: [0.7, 0.85, 1.0], low: [0.4, 0.6, 0.9], mid: [0.5, 0.7, 1.0], high: [0.9, 0.95, 1.0] },
    purple: { main: [0.9, 0.75, 1.0], low: [0.6, 0.45, 0.9], mid: [0.7, 0.55, 1.0], high: [0.95, 0.9, 1.0] },
    green: { main: [0.75, 1.0, 0.85], low: [0.4, 0.8, 0.6], mid: [0.5, 0.9, 0.7], high: [0.9, 1.0, 0.95] },
    crimson: { main: [1.0, 0.75, 0.75], low: [0.9, 0.5, 0.5], mid: [1.0, 0.6, 0.6], high: [1.0, 0.9, 0.9] },
};

// --- SHADERS ---
const vertexShader = `
        in vec3 position;
        in vec2 uv;
        out vec2 out_uv;

        void main() {
            // Three.js plane UVs go from 0 to 1
            out_uv = uv;
            // The original shader expects Y to be flipped in vertex shader
            out_uv.y = 1.0 - out_uv.y;
            gl_Position = vec4(position, 1.0);
        }`;

const fragmentShader = `
        precision highp float;

        #define NUM_OCTAVES (4)

        in vec2 out_uv;
        out vec4 fragColor;

        uniform float u_time;
        uniform vec2 u_viewport;

        uniform sampler2D uTextureNoise;
        uniform vec3 u_bloopColorMain;
        uniform vec3 u_bloopColorLow;
        uniform vec3 u_bloopColorMid;
        uniform vec3 u_bloopColorHigh;

        // GUI Uniforms
        uniform float u_windSpeed;
        uniform float u_warpPower;
        uniform float u_fbmStrength;
        uniform float u_blurRadius;
        uniform float u_zoom;
        uniform float u_grainStrength; // Added uniform for grain/texture noise
        uniform float u_grainScale;    // Added uniform for grain size
        uniform float u_noiseScale;    // Added uniform for procedural noise scale

        vec3 blendLinearBurn_13_5(vec3 base, vec3 blend, float opacity) {
            return (max(base + blend - vec3(1.0), vec3(0.0))) * opacity + base * (1.0 - opacity);
        }

        vec4 permute(vec4 x) { return mod((x * 34.0 + 1.0) * x, 289.0); }
        vec4 taylorInvSqrt(vec4 r) { return 1.79284291400159 - 0.85373472095314 * r; }
        vec3 fade(vec3 t) { return t * t * t * (t * (t * 6.0 - 15.0) + 10.0); }
        float rand(vec2 n) { return fract(sin(dot(n, vec2(12.9898, 4.1414))) * 43758.5453); }

        float noise(vec2 p) {
            vec2 ip = floor(p);
            vec2 u = fract(p);
            u = u * u * (3.0 - 2.0 * u);
            float res = mix(
                mix(rand(ip), rand(ip + vec2(1.0, 0.0)), u.x),
                mix(rand(ip + vec2(0.0, 1.0)), rand(ip + vec2(1.0, 1.0)), u.x),
                u.y
            );
            return res * res;
        }

        float fbm(vec2 x) {
            float v = 0.0;
            float a = 0.5;
            vec2 shift = vec2(100.0);
            mat2 rot = mat2(cos(0.5), sin(0.5), -sin(0.5), cos(0.5));
            for (int i = 0; i < NUM_OCTAVES; ++i) {
                v += a * noise(x);
                x = rot * x * 2.0 + shift;
                a *= 0.5;
            }
            return v;
        }

        float cnoise(vec3 P) {
            vec3 Pi0 = floor(P); vec3 Pi1 = Pi0 + vec3(1.0);
            Pi0 = mod(Pi0, 289.0); Pi1 = mod(Pi1, 289.0);
            vec3 Pf0 = fract(P); vec3 Pf1 = Pf0 - vec3(1.0);
            vec4 ix = vec4(Pi0.x, Pi1.x, Pi0.x, Pi1.x);
            vec4 iy = vec4(Pi0.yy, Pi1.yy);
            vec4 iz0 = vec4(Pi0.z); vec4 iz1 = vec4(Pi1.z);
            vec4 ixy = permute(permute(ix) + iy);
            vec4 ixy0 = permute(ixy + iz0); vec4 ixy1 = permute(ixy + iz1);
            vec4 gx0 = ixy0 / 7.0; vec4 gy0 = fract(floor(gx0) / 7.0) - 0.5;
            gx0 = fract(gx0);
            vec4 gz0 = vec4(0.5) - abs(gx0) - abs(gy0);
            vec4 sz0 = step(gz0, vec4(0.0));
            gx0 -= sz0 * (step(vec4(0.0), gx0) - 0.5);
            gy0 -= sz0 * (step(vec4(0.0), gy0) - 0.5);
            vec4 gx1 = ixy1 / 7.0; vec4 gy1 = fract(floor(gx1) / 7.0) - 0.5;
            gx1 = fract(gx1);
            vec4 gz1 = vec4(0.5) - abs(gx1) - abs(gy1);
            vec4 sz1 = step(gz1, vec4(0.0));
            gx1 -= sz1 * (step(vec4(0.0), gx1) - 0.5);
            gy1 -= sz1 * (step(vec4(0.0), gy1) - 0.5);
            vec3 g000 = vec3(gx0.x, gy0.x, gz0.x); vec3 g100 = vec3(gx0.y, gy0.y, gz0.y);
            vec3 g010 = vec3(gx0.z, gy0.z, gz0.z); vec3 g110 = vec3(gx0.w, gy0.w, gz0.w);
            vec3 g001 = vec3(gx1.x, gy1.x, gz1.x); vec3 g101 = vec3(gx1.y, gy1.y, gz1.y);
            vec3 g011 = vec3(gx1.z, gy1.z, gz1.z); vec3 g111 = vec3(gx1.w, gy1.w, gz1.w);
            vec4 norm0 = taylorInvSqrt(vec4(dot(g000, g000), dot(g010, g010), dot(g100, g100), dot(g110, g110)));
            g000 *= norm0.x; g010 *= norm0.y; g100 *= norm0.z; g110 *= norm0.w;
            vec4 norm1 = taylorInvSqrt(vec4(dot(g001, g001), dot(g011, g011), dot(g101, g101), dot(g111, g111)));
            g001 *= norm1.x; g011 *= norm1.y; g101 *= norm1.z; g111 *= norm1.w;
            float n000 = dot(g000, Pf0); float n100 = dot(g100, vec3(Pf1.x, Pf0.yz));
            float n010 = dot(g010, vec3(Pf0.x, Pf1.y, Pf0.z)); float n110 = dot(g110, vec3(Pf1.xy, Pf0.z));
            float n001 = dot(g001, vec3(Pf0.xy, Pf1.z)); float n101 = dot(g101, vec3(Pf1.x, Pf0.y, Pf1.z));
            float n011 = dot(g011, vec3(Pf0.x, Pf1.yz)); float n111 = dot(g111, Pf1);
            vec3 fade_xyz = fade(Pf0);
            vec4 n_z = mix(vec4(n000, n100, n010, n110), vec4(n001, n101, n011, n111), fade_xyz.z);
            vec2 n_yz = mix(n_z.xy, n_z.zw, fade_xyz.y);
            float n_xyz = mix(n_yz.x, n_yz.y, fade_xyz.x);
            return 2.2 * n_xyz;
        }

        vec3 getFluidColor(vec2 st, float time) {
            float scaleFactor = 1.0 / (2.0 * u_zoom);
            vec2 uv = st * scaleFactor + 0.5;
            uv.y = 1.0 - uv.y;

            float noiseScale = u_noiseScale; // Replaced hardcoded 1.25 with GUI uniform
            float windSpeed = u_windSpeed;
            float warpPower = u_warpPower;
            float fbmStrength = u_fbmStrength;
            float blurRadius = u_blurRadius;

            float waterColorNoiseScale = 18.0;
            float waterColorNoiseStrength = 0.02;
            float textureNoiseScale = u_grainScale; // Replaced hardcoded 1.0 with GUI uniform
            float textureNoiseStrength = u_grainStrength; // Replaced hardcoded 0.15 with GUI uniform
            float verticalOffset = 0.09;
            float waveSpread = 1.0;
            float layer1Amplitude = 1.5;
            float layer1Frequency = 1.0;
            float layer2Amplitude = 1.4;
            float layer2Frequency = 1.0;
            float layer3Amplitude = 1.3;
            float layer3Frequency = 1.0;
            float fbmPowerDamping = 0.55;
            float timescale = 1.0;

            time = time * timescale * 0.85;
            verticalOffset += 1.0 - waveSpread;

            // Apply u_noiseScale to the spatial coordinates of the procedural noise
            float noiseX = cnoise(vec3(uv * noiseScale + vec2(0.0, 74.8572), time * 0.3));
            float noiseY = cnoise(vec3(uv * noiseScale + vec2(203.91282, 10.0), time * 0.3));
            uv += vec2(noiseX * 2.0, noiseY) * warpPower;

            float noiseA = cnoise(vec3(uv * waterColorNoiseScale + vec2(344.91282, 0.0), time * 0.3)) +
                           cnoise(vec3(uv * waterColorNoiseScale * 2.2 + vec2(723.937, 0.0), time * 0.4)) * 0.5;
            uv += noiseA * waterColorNoiseStrength;
            uv.y -= verticalOffset;

            vec2 textureUv = uv * textureNoiseScale;
            float textureSampleR0 = texture(uTextureNoise, textureUv).r;
            float textureSampleG0 = texture(uTextureNoise, vec2(textureUv.x, 1.0 - textureUv.y)).g;
            float textureNoiseDisp0 = mix(textureSampleR0 - 0.5, textureSampleG0 - 0.5, (sin(time) + 1.0) * 0.5) * textureNoiseStrength;

            textureUv += vec2(63.861, 368.937);
            float textureSampleR1 = texture(uTextureNoise, textureUv).r;
            float textureSampleG1 = texture(uTextureNoise, vec2(textureUv.x, 1.0 - textureUv.y)).g;
            float textureNoiseDisp1 = mix(textureSampleR1 - 0.5, textureSampleG1 - 0.5, (sin(time) + 1.0) * 0.5) * textureNoiseStrength;

            textureUv += vec2(272.861, 829.937);
            textureUv += vec2(180.302, 819.871);
            float textureSampleR3 = texture(uTextureNoise, textureUv).r;
            float textureSampleG3 = texture(uTextureNoise, vec2(textureUv.x, 1.0 - textureUv.y)).g;
            float textureNoiseDisp3 = mix(textureSampleR3 - 0.5, textureSampleG3 - 0.5, (sin(time) + 1.0) * 0.5) * textureNoiseStrength;
            uv += textureNoiseDisp0;

            vec2 st_fbm = uv * noiseScale;
            vec2 q = vec2(0.0);
            q.x = fbm(st_fbm * 0.5 + windSpeed * time);
            q.y = fbm(st_fbm * 0.5 + windSpeed * time);
            vec2 r = vec2(0.0);
            r.x = fbm(st_fbm + 1.0 * q + vec2(0.3, 9.2) + 0.15 * time);
            r.y = fbm(st_fbm + 1.0 * q + vec2(8.3, 0.8) + 0.126 * time);
            float f = fbm(st_fbm + r - q);
            float fullFbm = (f + 0.6 * f * f + 0.7 * f + 0.5) * 0.5;
            fullFbm = pow(fullFbm, fbmPowerDamping);
            fullFbm *= fbmStrength;

            blurRadius = blurRadius * 1.5;

            vec2 snUv = (uv + vec2((fullFbm - 0.5) * 1.2) + vec2(0.0, 0.025) + textureNoiseDisp0) * vec2(layer1Frequency, 1.0);
            float sn = noise(snUv * 2.0 + vec2(0.0, time * 0.5)) * 2.0 * layer1Amplitude;
            float sn2 = smoothstep(sn - 1.2 * blurRadius, sn + 1.2 * blurRadius, (snUv.y - 0.5 * waveSpread) * 5.0 + 0.5);

            vec2 snUvBis = (uv + vec2((fullFbm - 0.5) * 0.85) + vec2(0.0, 0.025) + textureNoiseDisp1) * vec2(layer2Frequency, 1.0);
            float snBis = noise(snUvBis * 4.0 + vec2(293.0, time * 1.0)) * 2.0 * layer2Amplitude;
            float sn2Bis = smoothstep(snBis - 0.9 * blurRadius, snBis + 0.9 * blurRadius, (snUvBis.y - 0.6 * waveSpread) * 5.0 + 0.5);

            vec2 snUvThird = (uv + vec2((fullFbm - 0.5) * 1.1) + textureNoiseDisp3) * vec2(layer3Frequency, 1.0);
            float snThird = noise(snUvThird * 6.0 + vec2(153.0, time * 1.2)) * 2.0 * layer3Amplitude;
            float sn2Third = smoothstep(snThird - 0.7 * blurRadius, snThird + 0.7 * blurRadius, (snUvThird.y - 0.9 * waveSpread) * 6.0 + 0.5);

            sn2 = pow(sn2, 0.8);
            sn2Bis = pow(sn2Bis, 0.9);

            vec3 sinColor;
            sinColor = blendLinearBurn_13_5(u_bloopColorMain, u_bloopColorLow, 1.0 - sn2);
            sinColor = blendLinearBurn_13_5(sinColor, mix(u_bloopColorMain, u_bloopColorMid, 1.0 - sn2Bis), sn2);
            sinColor = mix(sinColor, mix(u_bloopColorMain, u_bloopColorHigh, 1.0 - sn2Third), sn2 * sn2Bis);

            return sinColor;
        }

        void main() {
            vec2 st = out_uv - 0.5;
            
            // Adjust X to maintain aspect ratio across varying screen sizes
            st.x *= u_viewport.x / u_viewport.y;

            vec3 finalColor = getFluidColor(st, u_time);

            // Output color directly without the SDF shape mask
            fragColor = vec4(finalColor, 1.0);
        }`;

// --- UTILS: Generate Procedural Noise Texture ---
function generateNoiseTexture() {
    // Increased size back to 256 to allow for very fine, detailed grain
    const size = 256;
    const canvas = document.createElement('canvas');
    canvas.width = size;
    canvas.height = size;
    const context = canvas.getContext('2d');
    const imageData = context.createImageData(size, size);

    for (let i = 0; i < imageData.data.length; i += 4) {
        // Generate simple white noise
        const val = Math.random() * 255;
        imageData.data[i] = val; // R
        imageData.data[i + 1] = val; // G
        imageData.data[i + 2] = val; // B
        imageData.data[i + 3] = 255; // A
    }

    context.putImageData(imageData, 0, 0);
    const texture = new THREE.CanvasTexture(canvas);
    texture.wrapS = THREE.RepeatWrapping;
    texture.wrapT = THREE.RepeatWrapping;
    texture.minFilter = THREE.LinearFilter;
    texture.magFilter = THREE.LinearFilter;
    return texture;
}

// --- MAIN APP ---
let renderer, bgScene, bgCamera, shaderMaterial;
let clock;
let globalTime = 0; // Accumulator for smooth time scaling

// GUI State
const settings = {
    theme: 'blue',
    windSpeed: 0.144,
    warpPower: 0.2355,
    fbmStrength: 0.912,
    blurRadius: 1.2673,
    zoom: 0.3971,
    grainStrength: 0.014,
    grainScale: 2.5,
    noiseScale: 0.8673,
    speed: 0.72
};

init();
animate();

function init() {
    // Remove loading screen
    document.getElementById('loading').style.opacity = '0';
    setTimeout(() => document.getElementById('loading').remove(), 500);

    const container = document.createElement('div');
    document.body.appendChild(container);

    clock = new THREE.Clock();

    // Setup Renderer (canvas)
    renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.setSize(window.innerWidth, window.innerHeight);

    // FORÇAGE ABSOLU DES STYLES EN JS
    renderer.domElement.id = 'art-sky';
    renderer.domElement.style.setProperty('position', 'fixed', 'important');
    renderer.domElement.style.setProperty('top', '0', 'important');
    renderer.domElement.style.setProperty('left', '0', 'important');
    renderer.domElement.style.setProperty('width', '100vw', 'important');
    renderer.domElement.style.setProperty('height', '100vh', 'important');
    renderer.domElement.style.setProperty('z-index', '-999', 'important');
    renderer.domElement.style.setProperty('pointer-events', 'none', 'important');
    renderer.domElement.style.setProperty('margin', '0', 'important');
    renderer.domElement.style.setProperty('padding', '0', 'important');

    container.appendChild(renderer.domElement);

    // --- BACKGROUND SCENE (Fullscreen Shader) ---
    bgScene = new THREE.Scene();
    bgCamera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);

    const noiseTexture = generateNoiseTexture();
    const initialTheme = THEMES[settings.theme];

    shaderMaterial = new THREE.RawShaderMaterial({
        vertexShader: vertexShader,
        fragmentShader: fragmentShader,
        uniforms: {
            u_time: { value: 0 },
            u_viewport: { value: new THREE.Vector2(window.innerWidth, window.innerHeight) },
            uTextureNoise: { value: noiseTexture },
            u_bloopColorMain: { value: new THREE.Vector3(...initialTheme.main) },
            u_bloopColorLow: { value: new THREE.Vector3(...initialTheme.low) },
            u_bloopColorMid: { value: new THREE.Vector3(...initialTheme.mid) },
            u_bloopColorHigh: { value: new THREE.Vector3(...initialTheme.high) },

            // GUI uniforms
            u_windSpeed: { value: settings.windSpeed },
            u_warpPower: { value: settings.warpPower },
            u_fbmStrength: { value: settings.fbmStrength },
            u_blurRadius: { value: settings.blurRadius },
            u_zoom: { value: settings.zoom },
            u_grainStrength: { value: settings.grainStrength },
            u_grainScale: { value: settings.grainScale },
            u_noiseScale: { value: settings.noiseScale }
        },
        transparent: false,
        depthWrite: false,
        depthTest: false,
        glslVersion: THREE.GLSL3 // Ensures #version 300 es compatibility in Three.js
    });

    // Plane that covers the entire screen
    const bgGeometry = new THREE.PlaneGeometry(2, 2);
    const bgMesh = new THREE.Mesh(bgGeometry, shaderMaterial);
    bgScene.add(bgMesh);

    // --- LIL-GUI SETUP ---
    setupGUI();

    window.addEventListener('resize', onWindowResize);
}

function setupGUI() {
    const gui = new GUI({ title: 'Shader Settings' });

    const colorsFolder = gui.addFolder('Theme & Colors');
    colorsFolder.add(settings, 'theme', Object.keys(THEMES)).name('Color Theme').onChange((themeName) => {
        const theme = THEMES[themeName];
        // Smoothly update colors or just snap. Here we snap.
        shaderMaterial.uniforms.u_bloopColorMain.value.set(...theme.main);
        shaderMaterial.uniforms.u_bloopColorLow.value.set(...theme.low);
        shaderMaterial.uniforms.u_bloopColorMid.value.set(...theme.mid);
        shaderMaterial.uniforms.u_bloopColorHigh.value.set(...theme.high);
    });

    const physicsFolder = gui.addFolder('Fluid Physics');
    physicsFolder.add(settings, 'windSpeed', 0.0, 1.0).name('Wind Speed').onChange(v => shaderMaterial.uniforms.u_windSpeed.value = v);
    physicsFolder.add(settings, 'warpPower', 0.0, 1.5).name('Warp Power').onChange(v => shaderMaterial.uniforms.u_warpPower.value = v);
    physicsFolder.add(settings, 'fbmStrength', 0.0, 3.0).name('FBM Strength').onChange(v => shaderMaterial.uniforms.u_fbmStrength.value = v);
    physicsFolder.add(settings, 'blurRadius', 0.1, 3.0).name('Blur Radius').onChange(v => shaderMaterial.uniforms.u_blurRadius.value = v);
    physicsFolder.add(settings, 'zoom', 0.1, 2.0).name('Fluid Zoom').onChange(v => shaderMaterial.uniforms.u_zoom.value = v);
    physicsFolder.add(settings, 'grainStrength', 0.0, 0.1).step(0.001).name('Grain Strength').onChange(v => shaderMaterial.uniforms.u_grainStrength.value = v);
    physicsFolder.add(settings, 'grainScale', 0.1, 10.0).name('Grain Scale').onChange(v => shaderMaterial.uniforms.u_grainScale.value = v);
    physicsFolder.add(settings, 'noiseScale', 0.1, 5.0).name('Noise Scale').onChange(v => shaderMaterial.uniforms.u_noiseScale.value = v);
    physicsFolder.add(settings, 'speed', 0.0, 5.0).name('Animation Speed');
}

function onWindowResize() {
    const width = window.innerWidth;
    const height = window.innerHeight;

    // Update renderer
    renderer.setSize(width, height);

    // Update shader resolution for correct aspect ratio calculation
    if (shaderMaterial) {
        shaderMaterial.uniforms.u_viewport.value.set(width, height);
    }
}

function animate() {
    requestAnimationFrame(animate);

    const delta = clock.getDelta();
    globalTime += delta * settings.speed;

    // Update Shader Uniforms
    if (shaderMaterial) {
        shaderMaterial.uniforms.u_time.value = globalTime * 0.95;
    }

    // Render fullscreen background shader
    renderer.render(bgScene, bgCamera);
}