import gsap from 'https://esm.sh/gsap@3.13.0'
import Draggable from 'https://esm.sh/gsap@3.13.0/Draggable'
import { Pane } from 'https://esm.sh/tweakpane@4.0.4'
gsap.registerPlugin(Draggable)

// Vertex shader - pass through UV coordinates
const vertexShaderSource = `
  attribute vec2 a_position;
  void main() {
    gl_Position = vec4(a_position, 0.0, 1.0);
  }
`

// Fragment shader - converted from Metal shader
const fragmentShaderSource = `
  precision highp float;

  uniform vec2 u_resolution;
  uniform float u_time;
  uniform float u_tap;
  uniform float u_speed;
  uniform float u_amplitude;
  uniform float u_pulseMin;
  uniform float u_pulseMax;
  uniform float u_noiseType;

  // Hash-based noise (original)
  float hash(float n) {
    return fract(sin(n) * 753.5453123);
  }

  float noiseHash(vec2 x) {
    vec2 p = floor(x);
    vec2 f = fract(x);
    f = f * f * (3.0 - 2.0 * f);

    float n = p.x + p.y * 157.0;
    return mix(
      mix(hash(n + 0.0), hash(n + 1.0), f.x),
      mix(hash(n + 157.0), hash(n + 158.0), f.x),
      f.y
    );
  }

  // Trigonometric noise (more periodic)
  float noiseTrig(vec2 p) {
    float x = p.x;
    float y = p.y;

    float n = sin(x * 1.0 + sin(y * 1.3)) * 0.5;
    n += sin(y * 1.0 + sin(x * 1.1)) * 0.5;
    n += sin((x + y) * 0.5) * 0.25;
    n += sin((x - y) * 0.7) * 0.25;

    return n * 0.5 + 0.5;
  }

  // Noise dispatcher
  float noise(vec2 p) {
    if (u_noiseType < 0.5) {
      return noiseHash(p);
    } else {
      return noiseTrig(p);
    }
  }

  // Fractional Brownian Motion
  float fbm(vec2 p, vec3 a) {
    float v = 0.0;
    v += noise(p * a.x) * 0.50;
    v += noise(p * a.y) * 1.50;
    v += noise(p * a.z) * 0.125 * 0.1;
    return v;
  }

  // Draw animated lines
  vec3 drawLines(vec2 uv, vec3 fbmOffset, vec3 color1, float secs) {
    float timeVal = secs * 0.1;
    vec3 finalColor = vec3(0.0);

    vec3 colorSets[4];
    colorSets[0] = vec3(0.7, 0.05, 1.0);
    colorSets[1] = vec3(1.0, 0.19, 0.0);
    colorSets[2] = vec3(0.0, 1.0, 0.3);
    colorSets[3] = vec3(0.0, 0.38, 1.0);

    // First pass - base lines
    for(int i = 0; i < 4; i++) {
      float indexAsFloat = float(i);
      float amp = u_amplitude + (indexAsFloat * 0.0);
      float period = 2.0 + (indexAsFloat + 2.0);
      float thickness = mix(0.4, 0.2, noise(uv * 2.0));

      float t = abs(1.0 / (sin(uv.y + fbm(uv + timeVal * period, fbmOffset)) * amp) * thickness);

      finalColor += t * colorSets[i];
    }

    // Second pass - secondary lines
    for(int i = 0; i < 4; i++) {
      float indexAsFloat = float(i);
      float amp = (u_amplitude * 0.5) + (indexAsFloat * 5.0);
      float period = 9.0 + (indexAsFloat + 2.0);
      float thickness = mix(0.1, 0.1, noise(uv * 12.0));

      float t = abs(1.0 / (sin(uv.y + fbm(uv + timeVal * period, fbmOffset)) * amp) * thickness);

      finalColor += t * colorSets[i] * color1;
    }

    return finalColor;
  }

  void main() {
    // Normalize coordinates matching original Metal shader
    vec2 uv = (gl_FragCoord.xy / u_resolution.x) * 1.0 - 1.0;
    uv *= 1.5;

    vec3 lineColor1 = vec3(1.0, 0.0, 0.5);
    vec3 lineColor2 = vec3(0.3, 0.5, 1.5);

    float spread = abs(u_tap);
    vec3 finalColor = vec3(0.0);

    float t = sin(u_time) * 0.5 + 0.5;
    float pulse = mix(u_pulseMin, u_pulseMax, t);

    // Combine both line passes
    finalColor = drawLines(uv, vec3(65.2, 40.0, 4.0), lineColor1, u_time * u_speed) * pulse;
    finalColor += drawLines(uv, vec3(5.0 * spread / 2.0, 2.1 * spread, 1.0), lineColor2, u_time * u_speed);

    gl_FragColor = vec4(finalColor, 1.0);
  }
`

class ChaosButton {
    constructor(button, config) {
        this.button = button
        this.canvas = button.querySelector('.chaos-canvas')
        this.config = config
        this.startTime = Date.now()
        this.lastTime = 0
        this.phase = 0 // Accumulated phase for smooth speed transitions

        // Current animated values
        this.currentSpeed = config.restingSpeed
        this.currentAmplitude = config.restingAmplitude
        this.currentPulseMin = config.restingPulseMin
        this.currentPulseMax = config.restingPulseMax
        this.currentTap = config.restingTap

        this.setupWebGL()
        this.setupEvents()
        this.render()
    }

    setupWebGL() {
        const gl = this.canvas.getContext('webgl', {
            alpha: false,
            antialias: true,
        })

        if (!gl) {
            console.error('WebGL not supported')
            return
        }

        this.gl = gl

        // Compile shaders
        const vertexShader = this.compileShader(
            gl.VERTEX_SHADER,
            vertexShaderSource
        )
        const fragmentShader = this.compileShader(
            gl.FRAGMENT_SHADER,
            fragmentShaderSource
        )

        // Create program
        const program = gl.createProgram()
        gl.attachShader(program, vertexShader)
        gl.attachShader(program, fragmentShader)
        gl.linkProgram(program)

        if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
            console.error('Program link error:', gl.getProgramInfoLog(program))
            return
        }

        this.program = program
        gl.useProgram(program)

        // Set up geometry (fullscreen quad)
        const positions = new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1])

        const buffer = gl.createBuffer()
        gl.bindBuffer(gl.ARRAY_BUFFER, buffer)
        gl.bufferData(gl.ARRAY_BUFFER, positions, gl.STATIC_DRAW)

        const positionLocation = gl.getAttribLocation(program, 'a_position')
        gl.enableVertexAttribArray(positionLocation)
        gl.vertexAttribPointer(positionLocation, 2, gl.FLOAT, false, 0, 0)

        // Get uniform locations
        this.uniformLocations = {
            resolution: gl.getUniformLocation(program, 'u_resolution'),
            time: gl.getUniformLocation(program, 'u_time'),
            tap: gl.getUniformLocation(program, 'u_tap'),
            speed: gl.getUniformLocation(program, 'u_speed'),
            amplitude: gl.getUniformLocation(program, 'u_amplitude'),
            pulseMin: gl.getUniformLocation(program, 'u_pulseMin'),
            pulseMax: gl.getUniformLocation(program, 'u_pulseMax'),
            noiseType: gl.getUniformLocation(program, 'u_noiseType'),
        }

        this.resize()
    }

    compileShader(type, source) {
        const shader = this.gl.createShader(type)
        this.gl.shaderSource(shader, source)
        this.gl.compileShader(shader)

        if (!this.gl.getShaderParameter(shader, this.gl.COMPILE_STATUS)) {
            console.error('Shader compile error:', this.gl.getShaderInfoLog(shader))
            this.gl.deleteShader(shader)
            return null
        }

        return shader
    }

    resize() {
        const dpr = Math.min(window.devicePixelRatio, 2)
        const rect = this.button.getBoundingClientRect()

        this.canvas.width = rect.width * dpr
        this.canvas.height = rect.height * dpr

        this.gl.viewport(0, 0, this.canvas.width, this.canvas.height)
        this.gl.uniform2f(
            this.uniformLocations.resolution,
            this.canvas.width,
            this.canvas.height
        )
    }

    setupEvents() {
        const activate = () => {
            if (this.config.previewMode) return
            // Kill any existing tweens to prevent overlapping animations
            gsap.killTweensOf(this)
            gsap.to(this, {
                currentSpeed: this.config.activeSpeed,
                currentAmplitude: this.config.activeAmplitude,
                currentPulseMin: this.config.activePulseMin,
                currentPulseMax: this.config.activePulseMax,
                currentTap: this.config.activeTap,
                duration: this.config.activeDuration,
                ease: this.config.activeEase,
            })
        }

        const deactivate = () => {
            if (this.config.previewMode) return
            // Kill any existing tweens to prevent overlapping animations
            gsap.killTweensOf(this)
            gsap.to(this, {
                currentSpeed: this.config.restingSpeed,
                currentAmplitude: this.config.restingAmplitude,
                currentPulseMin: this.config.restingPulseMin,
                currentPulseMax: this.config.restingPulseMax,
                currentTap: this.config.restingTap,
                duration: this.config.restingDuration,
                ease: this.config.restingEase,
            })
        }

        this.button.addEventListener('mousedown', activate)
        this.button.addEventListener('mouseup', deactivate)
        this.button.addEventListener('mouseleave', deactivate)
        this.button.addEventListener('touchstart', activate)
        this.button.addEventListener('touchend', deactivate)

        window.addEventListener('resize', () => this.resize())
    }

    setPreviewState(state) {
        if (state === 'resting') {
            this.currentSpeed = this.config.restingSpeed
            this.currentAmplitude = this.config.restingAmplitude
            this.currentPulseMin = this.config.restingPulseMin
            this.currentPulseMax = this.config.restingPulseMax
            this.currentTap = this.config.restingTap
        } else {
            this.currentSpeed = this.config.activeSpeed
            this.currentAmplitude = this.config.activeAmplitude
            this.currentPulseMin = this.config.activePulseMin
            this.currentPulseMax = this.config.activePulseMax
            this.currentTap = this.config.activeTap
        }
    }

    render = () => {
        const time = (Date.now() - this.startTime) / 1000
        const deltaTime = time - this.lastTime
        this.lastTime = time

        // Accumulate phase smoothly based on current speed
        this.phase += deltaTime * this.currentSpeed

        // Wrap phase to keep it in a reasonable range (prevents drift over time)
        // The noise functions are periodic, so we can safely wrap
        if (this.phase > 1000) {
            this.phase = this.phase % 1000
        }

        this.gl.uniform1f(this.uniformLocations.time, this.phase)
        this.gl.uniform1f(this.uniformLocations.tap, this.currentTap)
        this.gl.uniform1f(this.uniformLocations.speed, 1.0) // Speed is baked into phase now
        this.gl.uniform1f(this.uniformLocations.amplitude, this.currentAmplitude)
        this.gl.uniform1f(this.uniformLocations.pulseMin, this.currentPulseMin)
        this.gl.uniform1f(this.uniformLocations.pulseMax, this.currentPulseMax)
        this.gl.uniform1f(this.uniformLocations.noiseType, this.config.noiseType === 'trig' ? 1.0 : 0.0)

        this.gl.drawArrays(this.gl.TRIANGLE_STRIP, 0, 4)

        requestAnimationFrame(this.render)
    }
}

const config = {
    theme: 'dark',
    previewMode: false,
    previewState: 'resting',
    noiseType: 'trig',
    // Resting state
    restingSpeed: 0.35,
    restingAmplitude: 80,
    restingPulseMin: 0.05,
    restingPulseMax: 0.2,
    restingTap: 1.0,
    // Active state
    activeSpeed: 2.8,
    activeAmplitude: 10,
    activePulseMin: 0.05,
    activePulseMax: 0.4,
    activeTap: 1.0,
    // Animation
    activeDuration: 0.26,
    activeEase: 'power2.out',
    restingDuration: 3,
    restingEase: 'power2.out',
}

const ctrl = new Pane({
    title: 'config',
    expanded: true,
})

const update = () => {
    document.documentElement.dataset.theme = config.theme
}

const sync = (event) => {
    if (
        !document.startViewTransition ||
        event.target.controller.view.labelElement.innerText !== 'theme'
    )
        return update()
    document.startViewTransition(() => update())
}

// ctrl.addBinding(config, 'theme', {
//   label: 'theme',
//   options: {
//     system: 'system',
//     light: 'light',
//     dark: 'dark',
//   },
// })

const debugFolder = ctrl.addFolder({ title: 'Debug', expanded: true })

debugFolder.addBinding(config, 'previewMode', {
    label: 'Preview Mode',
})

debugFolder.addBinding(config, 'previewState', {
    label: 'Preview State',
    options: {
        Resting: 'resting',
        Active: 'active',
    },
})

debugFolder.addBinding(config, 'noiseType', {
    label: 'Noise Algorithm',
    options: {
        'Hash (Original)': 'hash',
        'Trigonometric': 'trig',
    },
})

const restingFolder = ctrl.addFolder({
    title: 'Resting State',
    expanded: false,
})

restingFolder.addBinding(config, 'restingSpeed', {
    label: 'Speed',
    min: 0.01,
    max: 1,
    step: 0.01,
})

restingFolder.addBinding(config, 'restingAmplitude', {
    label: 'Amplitude',
    min: 20,
    max: 150,
    step: 1,
})

restingFolder.addBinding(config, 'restingPulseMin', {
    label: 'Pulse Min',
    min: 0.01,
    max: 0.5,
    step: 0.01,
})

restingFolder.addBinding(config, 'restingPulseMax', {
    label: 'Pulse Max',
    min: 0.1,
    max: 1.0,
    step: 0.01,
})

restingFolder.addBinding(config, 'restingTap', {
    label: 'Chaos',
    min: 0.1,
    max: 5.0,
    step: 0.1,
})

const activeFolder = ctrl.addFolder({ title: 'Active State', expanded: false })

activeFolder.addBinding(config, 'activeSpeed', {
    label: 'Speed',
    min: 0.01,
    max: 5.0,
    step: 0.01,
})

activeFolder.addBinding(config, 'activeAmplitude', {
    label: 'Amplitude',
    min: 20,
    max: 150,
    step: 1,
})

activeFolder.addBinding(config, 'activePulseMin', {
    label: 'Pulse Min',
    min: 0.01,
    max: 0.5,
    step: 0.01,
})

activeFolder.addBinding(config, 'activePulseMax', {
    label: 'Pulse Max',
    min: 0.1,
    max: 1.0,
    step: 0.01,
})

activeFolder.addBinding(config, 'activeTap', {
    label: 'Chaos',
    min: 0.1,
    max: 8.0,
    step: 0.1,
})

const animationFolder = ctrl.addFolder({ title: 'Animation', expanded: false })

animationFolder.addBinding(config, 'activeDuration', {
    label: 'Active Duration',
    min: 0.1,
    max: 2.0,
    step: 0.1,
})

animationFolder.addBinding(config, 'activeEase', {
    label: 'Active Ease',
    options: {
        'power2.out': 'power2.out',
        'power3.out': 'power3.out',
        'expo.out': 'expo.out',
        'back.out': 'back.out',
    },
})

animationFolder.addBinding(config, 'restingDuration', {
    label: 'Resting Duration',
    min: 0.1,
    max: 3.0,
    step: 0.1,
})

animationFolder.addBinding(config, 'restingEase', {
    label: 'Resting Ease',
    options: {
        'power2.out': 'power2.out',
        'power3.out': 'power3.out',
        'expo.out': 'expo.out',
        'elastic.out': 'elastic.out',
    },
})

ctrl.on('change', (event) => {
    sync(event)

    // Update preview state when in preview mode
    if (config.previewMode) {
        chaosButton.setPreviewState(config.previewState)
    }
})

update()

// Initialize chaos button
const chaosButton = new ChaosButton(
    document.querySelector('.chaos-button'),
    config
)

// make tweakpane panel draggable
const tweakClass = 'div.tp-dfwv'
const d = Draggable.create(tweakClass, {
    type: 'x,y',
    allowEventDefault: true,
    trigger: tweakClass + ' button.tp-rotv_b',
})
document.querySelector(tweakClass).addEventListener('dblclick', () => {
    gsap.to(tweakClass, {
        x: `+=${d[0].x * -1}`,
        y: `+=${d[0].y * -1}`,
        onComplete: () => {
            gsap.set(tweakClass, { clearProps: 'all' })
        },
    })
})
