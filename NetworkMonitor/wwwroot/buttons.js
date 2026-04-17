window.NetworkMonitorButtons = (() => {
    const HEXES = '3b82f61d4ed822c55e15803def4444b91c1cff6a00cc5500ffc800cca00014b8a60f766edb3b7cb02e649356d46b3fa13341551e293bffbf00cc9900ffffffd3e2ef'.match(/.{6}/g);
    const COLORS = {
        blue: 0,
        green: 1,
        red: 2,
        orange: 3,
        yellow: 4,
        teal: 5,
        pink: 6,
        purple: 7,
        slate: 8,
        amber: 9,
        white: 10
    };
    const CTX = document.createElement('canvas').getContext('2d');
    const resizeObservers = new WeakMap();
    let nextId = 0;

    function mixColor(hex, pct, target) {
        return `color-mix(in srgb, #${hex} ${pct}%, ${target})`;
    }

    function buildSquirclePath(width, height, radius, offsetX, offsetY) {
        let path = '';
        for (let quadrant = 0; quadrant < 4; quadrant++) {
            for (let point = 0; point < 31; point++) {
                const angle = ((quadrant + point / 30) * Math.PI) / 2;
                const cos = Math.cos(angle);
                const sin = Math.sin(angle);
                const x = offsetX
                    + (cos > 0 ? width - radius : radius)
                    + Math.sign(cos) * Math.pow(Math.abs(cos), 0.6) * radius;
                const y = offsetY
                    + (sin > 0 ? height - radius : radius)
                    + Math.sign(sin) * Math.pow(Math.abs(sin), 0.6) * radius;
                path += `${quadrant || point ? 'L' : 'M'}${x} ${y}`;
            }
        }

        return `${path}Z`;
    }

    function getColorName(button) {
        if (button.classList.contains('action-button') && button.classList.contains('active')) {
            return 'blue';
        }

        if (button.classList.contains('danger')) {
            return 'red';
        }

        if (button.classList.contains('secondary')) {
            return 'orange';
        }

        if (button.classList.contains('ghost')) {
            return 'white';
        }

        if (button.classList.contains('success')) {
            return 'green';
        }

        if (button.classList.contains('tab-button')) {
            return button.classList.contains('active') ? 'blue' : 'slate';
        }

        if (button.classList.contains('primary')) {
            return 'blue';
        }

        return 'slate';
    }

    function getButtonLabel(button) {
        return (button.dataset.buttonLabel ?? button.textContent ?? '').replace(/\s+/g, ' ').trim();
    }

    function getButtonIcon(button, colorName) {
        if (button.dataset.buttonIcon) {
            return button.dataset.buttonIcon;
        }

        if (button.id === 'themeToggleButton') {
            return '◐';
        }

        if (button.id === 'logoutButton') {
            return '↗';
        }

        if (button.id === 'checkNowButton') {
            return '⚡';
        }

        if (button.classList.contains('danger')) {
            return '✕';
        }

        if (button.classList.contains('secondary')) {
            return '⏸';
        }

        if (button.classList.contains('ghost')) {
            return colorName === 'white' ? '◉' : '○';
        }

        if (button.classList.contains('tab-button')) {
            return '•';
        }

        return '•';
    }

    function getButtonHeight(button) {
        const explicitHeight = Number(button.dataset.buttonHeight ?? '');
        if (!Number.isNaN(explicitHeight) && explicitHeight > 0) {
            return explicitHeight;
        }

        return button.classList.contains('tab-button') ? 38 : 44;
    }

    function ensureAccessibleLabel(button, label) {
        if (label) {
            button.setAttribute('aria-label', button.getAttribute('aria-label') || label);
        }
    }

    function measureWidth(label, icon, isSquare, height) {
        if (isSquare) {
            return 48;
        }

        CTX.font = '900 15px Inter, system-ui, sans-serif';
        const iconAllowance = icon ? 100 : 80;
        return Math.ceil((CTX.measureText(label.toUpperCase()).width + iconAllowance) * 1.1 * (height / 40));
    }

    function renderButton(button) {
        const label = getButtonLabel(button);
        const colorName = getColorName(button);
        const icon = getButtonIcon(button, colorName);
        const height = getButtonHeight(button);
        const paletteIndex = COLORS[colorName] ?? COLORS.blue;
        const baseHex = HEXES[paletteIndex * 2];
        const darkHex = HEXES[paletteIndex * 2 + 1];
        const whiteButton = colorName === 'white';
        const uniqueId = button.dataset.buttonSvgId || `nm-btn-${++nextId}`;
        const square = button.classList.contains('icon-only');
        let width = Number(button.dataset.buttonWidth ?? '0');

        button.dataset.buttonSvgId = uniqueId;
        ensureAccessibleLabel(button, label);

        if (!width) {
            width = measureWidth(label, icon, square, height);
            button.dataset.buttonWidth = String(width);
        }

        const scale = height / 40;
        const pressed = button.dataset.buttonPressed === 'true';
        const floating = button.classList.contains('button-floating');
        const faceY = 4 + (pressed ? 5 : 0);
        const baseY = 12;
        const edgeOffset = Math.min(0.5, 20 / Math.max(width, 1));
        const dropShadowY = floating ? (pressed ? 12 : 24) : (pressed ? 2 : 4);
        const dropShadowBlur = floating ? (pressed ? 6 : 12) : (pressed ? 1.5 : 3);
        const dropShadowOpacity = floating ? 0.15 : 0.3;
        const squircle = buildSquirclePath(width, 40, 18, 5, baseY);
        const hi = mixColor(baseHex, 70, 'white');
        const sh = mixColor(darkHex, 35, 'black');

        button.classList.add('svg-button-ready');
        button.style.width = button.classList.contains('button-full-width') ? '100%' : `${(width + 10) * scale}px`;
        button.style.height = `${60 * scale}px`;

        const layeredFaces = Array.from({ length: Math.max(0, baseY - faceY) }, (_, index) => {
            const gradientId = `nm-btn-gradient-${uniqueId}-${index}`;
            return `
                <defs>
                    <linearGradient id="${gradientId}">
                        <stop offset="0" stop-color="${mixColor(darkHex, 65, 'white')}"/>
                        <stop offset="${edgeOffset}" stop-color="${mixColor(darkHex, 90, 'white')}"/>
                        <stop offset="${1 - edgeOffset}" stop-color="${mixColor(darkHex, 90, 'white')}"/>
                        <stop offset="1" stop-color="${mixColor(darkHex, 65, 'white')}"/>
                    </linearGradient>
                </defs>
                <path d="${buildSquirclePath(width, 40, 18, 5, faceY + 1 + index)}" fill="url(#${gradientId})"/>`;
        }).join('');

        let svg = button.querySelector(':scope > .svg-button-art');
        if (!(svg instanceof SVGElement)) {
            const namespace = 'http://www.w3.org/2000/svg';
            svg = document.createElementNS(namespace, 'svg');
            svg.classList.add('svg-button-art');
            svg.setAttribute('aria-hidden', 'true');
            button.replaceChildren(svg);
        }

        svg.setAttribute('viewBox', `0 0 ${width + 10} 60`);
        svg.setAttribute('preserveAspectRatio', 'none');
        svg.innerHTML = `
            <defs>
                <filter id="nm-btn-shadow-${uniqueId}" x="-100%" y="-100%" width="300%" height="300%">
                    <feDropShadow dy="${dropShadowY}" stdDeviation="${dropShadowBlur}" flood-color="${sh}" flood-opacity="${dropShadowOpacity}"/>
                </filter>
            </defs>
            <path d="${squircle}" fill="${mixColor(darkHex, 60, 'black')}" filter="url(#nm-btn-shadow-${uniqueId})"/>
            <path d="${squircle}" fill="${mixColor(darkHex, 80, 'black')}" stroke="${floating ? hi : mixColor(darkHex, 50, 'black')}" stroke-width="1"/>
            ${layeredFaces}
            <path d="${buildSquirclePath(width, 40, 18, 5, faceY)}" fill="${whiteButton ? '#fff' : `#${baseHex}`}" stroke="${whiteButton ? '#e2e8f0' : hi}" stroke-width="1.5"/>
            <text x="${5 + width / 2}" y="${20 + faceY}" text-anchor="middle" dominant-baseline="central" fill="${whiteButton ? '#3b82f6' : '#fff'}" class="svg-button-text">
                <tspan class="svg-button-icon" dy="1">${icon}</tspan>
                ${square ? '' : `<tspan dx="${icon ? 8 : 0}" dy="0" class="svg-button-label">${label.toUpperCase()}</tspan>`}
            </text>`;
    }

    function bindPointerState(button) {
        if (button.dataset.buttonBound === 'true') {
            return;
        }

        let releaseTimerId = null;

        const clearReleaseTimer = () => {
            if (releaseTimerId === null) {
                return;
            }

            window.clearTimeout(releaseTimerId);
            releaseTimerId = null;
        };

        const press = isPressed => {
            clearReleaseTimer();
            button.dataset.buttonPressed = isPressed ? 'true' : 'false';
            renderButton(button);
        };

        const release = (delayMs = 0) => {
            clearReleaseTimer();
            releaseTimerId = window.setTimeout(() => {
                button.dataset.buttonPressed = 'false';
                renderButton(button);
                releaseTimerId = null;
            }, delayMs);
        };

        button.dataset.buttonPressed = 'false';
        button.addEventListener('pointerdown', () => press(true));
        button.addEventListener('pointerup', () => release(12));
        button.addEventListener('pointerleave', () => release(0));
        button.addEventListener('pointercancel', () => release(0));
        button.addEventListener('blur', () => release(0));
        button.addEventListener('keydown', event => {
            if (event.key === ' ' || event.key === 'Enter') {
                press(true);
            }
        });
        button.addEventListener('keyup', event => {
            if (event.key === ' ' || event.key === 'Enter') {
                release(0);
            }
        });
        button.dataset.buttonBound = 'true';
    }

    function observeResize(button) {
        if (resizeObservers.has(button)) {
            return;
        }

        const observer = new ResizeObserver(() => {
            if (!button.classList.contains('button-full-width')) {
                return;
            }

            const height = getButtonHeight(button);
            button.dataset.buttonWidth = String(Math.max(48, button.offsetWidth / (height / 40) - 10));
            renderButton(button);
        });

        observer.observe(button);
        resizeObservers.set(button, observer);
    }

    function prepareButton(button) {
        if (!(button instanceof HTMLElement)) {
            return;
        }

        button.dataset.buttonLabel = getButtonLabel(button);
        button.classList.add('svg-button-host');
        if (button.classList.contains('w-full')) {
            button.classList.add('button-full-width');
        }

        bindPointerState(button);
        observeResize(button);
        renderButton(button);
    }

    function refreshButton(button) {
        if (!(button instanceof HTMLElement)) {
            return;
        }

        button.dataset.buttonLabel = getButtonLabel(button);
        delete button.dataset.buttonWidth;
        renderButton(button);
    }

    function enhanceAll(root = document) {
        root.querySelectorAll('.action-button, .tab-button').forEach(prepareButton);
    }

    return {
        enhanceAll,
        prepareButton,
        refreshButton
    };
})();
