import AttractionCursor from "https://cdn.jsdelivr.net/npm/threejs-components@0.0.26/build/cursors/attraction1.min.js";

const canvas = document.getElementById('canvas');

if (canvas) {
    AttractionCursor(canvas, {
        particles: {
            attractionIntensity: 0.75,
            size: 1.5
        }
    });
}
