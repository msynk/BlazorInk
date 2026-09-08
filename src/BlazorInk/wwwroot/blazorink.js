// BlazorInk browser layer.
//
// This file deliberately contains NO drawing logic. It does exactly three things that only the browser can do:
//
//   1. Collect pointer samples (including coalesced ones) and hand them to .NET once per animation frame.
//   2. Replay a command buffer produced by the C# renderer onto a 2D canvas context.
//   3. Report device capabilities, sizes and encoded image data back to .NET.
//
// Stroke shape, smoothing, pressure response, hit testing, history and serialization all live in C#.
// The one exception is the optional "wet ink" trail (see drawWetInk): a plain polyline drawn while a pointer
// batch is in flight, used only in Blazor Server to hide network latency. It is replaced by the real
// engine-generated geometry as soon as .NET responds.

// Kept on globalThis so the module can be imported twice (once through Blazor's IJSRuntime and once through
// the WebAssembly runtime's zero-copy import) while both views share the same surfaces.
const surfaces = (globalThis.__blazorInkSurfaces ??= new Map());

// Packed pointer sample layout, matching InputSample.ReadFrom in C#.
const SAMPLE_SIZE = 12;
const FLAG_COALESCED = 1;
const FLAG_PREDICTED = 2;
const FLAG_PRIMARY = 4;
const FLAG_SHIFT = 8;
const FLAG_CTRL = 16;
const FLAG_ALT = 32;

const PHASE_DOWN = 0;
const PHASE_MOVE = 1;
const PHASE_UP = 2;
const PHASE_CANCEL = 3;
const PHASE_HOVER = 4;

const POINTER_KIND = { mouse: 1, touch: 2, pen: 3 };

// Keys the canvas acts on. The browser default is suppressed for these so the page does not also scroll,
// select all its text, or navigate back.
const OWNED_KEYS = new Set(['Escape', 'Delete', 'Backspace', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown']);
const OWNED_SHORTCUTS = new Set(['z', 'y', 'a', '0', '9', '+', '=', '-']);

// Command opcodes, matching CanvasCommand in C#.
const OP = {
  BEGIN_PATH: 0,
  MOVE_TO: 1,
  LINE_TO: 2,
  QUAD_TO: 3,
  CUBIC_TO: 4,
  CLOSE_PATH: 5,
  FILL: 6,
  STROKE: 7,
  SAVE: 8,
  RESTORE: 9,
  TRANSFORM: 10,
  CLIP_RECT: 11,
  DRAW_IMAGE: 12,
  DRAW_TEXT: 13,
  PUSH_GROUP: 14,
  POP_GROUP: 15,
  CLEAR: 16,
  SET_TRANSFORM: 17,
  SET_DASH: 18,
  CLEAR_RECT: 19,
};

const BLEND_NAMES = ['source-over', 'multiply', 'screen', 'destination-out'];
const CAP_NAMES = ['butt', 'round', 'square'];
const JOIN_NAMES = ['miter', 'round', 'bevel'];
const ALIGN_NAMES = ['left', 'center', 'right'];

// The zero-copy WebAssembly path hands over a MemoryView, which exposes slice/set but not element access.
// One slice is a memcpy of the frame's commands, still far cheaper than serializing them.
function normalizeBuffer(buffer, count) {
  if (!buffer) return null;
  if (Array.isArray(buffer) || ArrayBuffer.isView(buffer)) return buffer;
  if (typeof buffer.slice === 'function') return buffer.slice(0, count);
  return null;
}

function cssColor(packed) {
  // Packed as 0xAARRGGBB.
  const a = (packed >>> 24) & 0xff;
  const r = (packed >>> 16) & 0xff;
  const g = (packed >>> 8) & 0xff;
  const b = packed & 0xff;
  return a === 255 ? `rgb(${r},${g},${b})` : `rgba(${r},${g},${b},${(a / 255).toFixed(4)})`;
}

class Surface {
  constructor(id, root, options) {
    this.id = id;
    this.root = root;
    this.documentCanvas = root.querySelector('[data-bi-layer="document"]');
    this.overlayCanvas = root.querySelector('[data-bi-layer="overlay"]');
    this.hitTarget = root.querySelector('[data-bi-layer="input"]') || root;
    this.options = options || {};
    this.dpr = 1;
    this.cssWidth = 0;
    this.cssHeight = 0;
    this.samples = new Float64Array(SAMPLE_SIZE * 256);
    this.sampleCount = 0;
    this.frameRequested = false;
    this.activePointers = new Set();
    this.penActive = false;
    this.disposed = false;
    this.wetInk = new Map();
    this.images = new Map();
    this.frameToken = 0;
    this.rectFrame = -1;
    this.cachedRect = null;
    this.pixelRatioQuery = null;
    this.onPixelRatioChange = null;
    this.groupStack = [];
    this.scratchCanvases = [];

    this.contexts = {
      document: this.createContext(this.documentCanvas),
      overlay: this.createContext(this.overlayCanvas),
    };

    this.onPointerDown = this.handlePointerDown.bind(this);
    this.onPointerMove = this.handlePointerMove.bind(this);
    this.onPointerUp = this.handlePointerUp.bind(this);
    this.onPointerCancel = this.handlePointerCancel.bind(this);
    this.onContextMenu = (e) => { if (this.options.suppressContextMenu) e.preventDefault(); };
    this.onKeyDown = this.handleKeyDown.bind(this);
    this.onFocusOut = this.handleFocusOut.bind(this);
    this.onFrame = this.flush.bind(this);

    const target = this.hitTarget;
    target.addEventListener('pointerdown', this.onPointerDown);
    target.addEventListener('pointermove', this.onPointerMove);
    target.addEventListener('pointerup', this.onPointerUp);
    target.addEventListener('pointercancel', this.onPointerCancel);
    target.addEventListener('pointerleave', this.onPointerCancel);
    target.addEventListener('contextmenu', this.onContextMenu);
    root.addEventListener('keydown', this.onKeyDown);
    root.addEventListener('focusout', this.onFocusOut);

    this.resizeObserver = new ResizeObserver(() => this.notifyResize());
    this.resizeObserver.observe(root);

    // A change of device pixel ratio (moving the window to another display, browser zoom) must re-scale the
    // backing store or the drawing turns blurry.
    this.watchPixelRatio();
  }

  createContext(canvas) {
    if (!canvas) return null;
    const attributes = { alpha: true, desynchronized: this.options.lowLatency !== false };
    const ctx = canvas.getContext('2d', attributes);
    if (ctx && typeof ctx.getContextAttributes === 'function') {
      const actual = ctx.getContextAttributes();
      this.lowLatency = !!(actual && actual.desynchronized);
    }
    return ctx;
  }

  watchPixelRatio() {
    if (typeof matchMedia !== 'function') return;
    const listen = () => {
      if (this.disposed) return;
      this.pixelRatioQuery = matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`);
      // Kept in a field so dispose can detach it: a media query listener is registered on the document and
      // would keep this surface, its canvases and its image cache alive until the ratio happened to change.
      this.onPixelRatioChange = () => {
        this.notifyResize();
        listen();
      };
      this.pixelRatioQuery.addEventListener('change', this.onPixelRatioChange, { once: true });
    };
    listen();
  }

  stopWatchingPixelRatio() {
    if (this.pixelRatioQuery && this.onPixelRatioChange) {
      this.pixelRatioQuery.removeEventListener('change', this.onPixelRatioChange);
    }
    this.pixelRatioQuery = null;
    this.onPixelRatioChange = null;
  }

  setSize(cssWidth, cssHeight, dpr) {
    this.cssWidth = cssWidth;
    this.cssHeight = cssHeight;
    this.dpr = dpr;
    const pixelWidth = Math.max(1, Math.round(cssWidth * dpr));
    const pixelHeight = Math.max(1, Math.round(cssHeight * dpr));
    for (const canvas of [this.documentCanvas, this.overlayCanvas]) {
      if (!canvas) continue;
      if (canvas.width !== pixelWidth || canvas.height !== pixelHeight) {
        canvas.width = pixelWidth;
        canvas.height = pixelHeight;
      }
      canvas.style.width = `${cssWidth}px`;
      canvas.style.height = `${cssHeight}px`;
    }
    this.scratchCanvases.length = 0;
  }

  notifyResize() {
    if (this.disposed || !this.callback) return;
    const rect = this.root.getBoundingClientRect();
    this.callback.resized(rect.width, rect.height, window.devicePixelRatio || 1);
  }

  // ---- Input ------------------------------------------------------------------------------------------------

  shouldIgnore(event) {
    // Palm rejection: while a pen is in contact, touch contacts are almost always the hand resting on the screen.
    if (this.options.rejectTouchWhilePen !== false && this.penActive && event.pointerType === 'touch') return true;
    return false;
  }

  // The element rectangle is read once per event rather than once per sample: getBoundingClientRect forces a
  // layout, and a 240 Hz pen delivers dozens of coalesced samples per event.
  currentRect() {
    if (this.rectFrame !== this.frameToken) {
      this.cachedRect = this.hitTarget.getBoundingClientRect();
      this.rectFrame = this.frameToken;
    }
    return this.cachedRect;
  }

  push(event, phase, flags) {
    if (this.sampleCount * SAMPLE_SIZE + SAMPLE_SIZE > this.samples.length) {
      const grown = new Float64Array(this.samples.length * 2);
      grown.set(this.samples);
      this.samples = grown;
    }
    const rect = this.currentRect();
    const o = this.sampleCount * SAMPLE_SIZE;
    const s = this.samples;
    s[o] = event.pointerId;
    s[o + 1] = POINTER_KIND[event.pointerType] || 0;
    s[o + 2] = phase;
    s[o + 3] = event.clientX - rect.left;
    s[o + 4] = event.clientY - rect.top;
    s[o + 5] = event.pressure;
    s[o + 6] = event.tiltX || 0;
    s[o + 7] = event.tiltY || 0;
    s[o + 8] = event.twist || 0;
    s[o + 9] = event.timeStamp;
    s[o + 10] = event.buttons;
    s[o + 11] = flags
      | (event.isPrimary ? FLAG_PRIMARY : 0)
      | (event.shiftKey ? FLAG_SHIFT : 0)
      | ((event.ctrlKey || event.metaKey) ? FLAG_CTRL : 0)
      | (event.altKey ? FLAG_ALT : 0);
    this.sampleCount++;
  }

  handlePointerDown(event) {
    if (this.shouldIgnore(event)) return;
    if (event.pointerType === 'pen') this.penActive = true;
    // Capturing on the element keeps the gesture alive when the pointer leaves the canvas.
    try { this.hitTarget.setPointerCapture(event.pointerId); } catch { /* capture is best effort */ }
    this.activePointers.add(event.pointerId);
    this.push(event, PHASE_DOWN, 0);
    if (this.options.preventDefaultOnDown !== false) {
      // Preventing the default stops text selection and scrolling, but it also stops the browser from moving
      // focus. Without this the canvas would never be focused by drawing on it and its keyboard shortcuts
      // would be unreachable for anyone using a mouse or a pen.
      event.preventDefault();
      if (this.root.tabIndex >= 0 && document.activeElement !== this.root) {
        this.root.focus({ preventScroll: true });
      }
    }
    this.requestFlush();
  }

  handlePointerMove(event) {
    if (this.shouldIgnore(event)) return;
    const isDown = this.activePointers.has(event.pointerId);
    if (!isDown && !this.options.trackHover) return;

    if (isDown && typeof event.getCoalescedEvents === 'function') {
      // Coalesced events carry every sample the digitizer produced between frames, which is what makes
      // fast handwriting smooth on a 240 Hz pen.
      const coalesced = event.getCoalescedEvents();
      if (coalesced && coalesced.length > 1) {
        for (const sample of coalesced) this.push(sample, PHASE_MOVE, FLAG_COALESCED);
      } else {
        this.push(event, PHASE_MOVE, 0);
      }
    } else {
      this.push(event, isDown ? PHASE_MOVE : PHASE_HOVER, 0);
    }

    if (isDown) {
      if (this.options.wetInk) this.appendWetInk(event);
      event.preventDefault();
    }
    this.requestFlush();
  }

  handlePointerUp(event) {
    if (!this.activePointers.delete(event.pointerId)) return;
    if (event.pointerType === 'pen') this.penActive = false;
    try { this.hitTarget.releasePointerCapture(event.pointerId); } catch { /* already released */ }
    this.push(event, PHASE_UP, 0);
    this.endWetInk(event.pointerId);
    this.requestFlush();
  }

  handlePointerCancel(event) {
    if (!this.activePointers.delete(event.pointerId)) return;
    if (event.pointerType === 'pen') this.penActive = false;
    this.push(event, PHASE_CANCEL, 0);
    this.endWetInk(event.pointerId);
    this.requestFlush();
  }

  // Keyboard shortcuts travel the same path as pointer input, so the component never depends on a framework
  // event and never re-renders per keystroke.
  handleKeyDown(event) {
    if (this.disposed || !this.callback || this.options.handleKeyboard === false) return;
    if (event.isComposing) return;

    // While the inline text editor is open it owns the keyboard. Forwarding these to the canvas would mean
    // typing "Delete" in a comment deleted the selection.
    const editor = this.textEditor();
    if (editor && event.target === editor) {
      if (event.key === 'Escape' || (event.key === 'Enter' && !event.shiftKey)) {
        event.preventDefault();
        this.callback.textKey(event.key, editor.value);
      }
      return;
    }

    if (OWNED_KEYS.has(event.key) || ((event.ctrlKey || event.metaKey) && OWNED_SHORTCUTS.has(event.key.toLowerCase()))) {
      event.preventDefault();
    }
    this.callback.key(event.key, event.ctrlKey || event.metaKey, event.shiftKey, event.altKey);
  }

  textEditor() {
    return this.root.querySelector('textarea.blazorink-text-editor');
  }

  // Clicking away from an open editor commits what was typed, the way any text field behaves.
  handleFocusOut(event) {
    if (this.disposed || !this.callback) return;
    const editor = this.textEditor();
    if (editor && event.target === editor) {
      this.callback.textKey('Blur', editor.value);
    }
  }

  requestFlush() {
    // A new frame invalidates the cached element rectangle: the page may have scrolled or resized.
    this.frameToken++;
    if (this.frameRequested || this.disposed) return;
    this.frameRequested = true;
    requestAnimationFrame(this.onFrame);
  }

  // Delivers one batch of samples per animation frame. Never one call per pointer event.
  flush() {
    this.frameRequested = false;
    const count = this.sampleCount;
    // Cleared unconditionally: samples collected before .NET attached its callback, or after a circuit dropped,
    // would otherwise accumulate for ever and grow the buffer without bound.
    this.sampleCount = 0;
    if (this.disposed || !this.callback || count === 0) return;
    this.callback.samples(this.samples, count);
  }

  // ---- Wet ink (Blazor Server latency compensation) ----------------------------------------------------------

  // Draws a provisional trail so the pen feels responsive while the sample batch travels to the server.
  // It carries no stroke model: a constant-width polyline in the brush colour, cleared as soon as the engine
  // renders the real geometry.
  appendWetInk(event) {
    const ctx = this.contexts.overlay;
    if (!ctx || !this.wetInkStyle) return;
    const rect = this.currentRect();
    const x = event.clientX - rect.left;
    const y = event.clientY - rect.top;
    // Tracked per pointer, so a second contact does not draw a line from where the first one was.
    const previous = this.wetInk.get(event.pointerId);
    if (!previous) {
      this.wetInk.set(event.pointerId, { x, y });
      return;
    }
    ctx.save();
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    ctx.strokeStyle = this.wetInkStyle.color;
    ctx.lineWidth = this.wetInkStyle.width;
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    ctx.globalAlpha = this.wetInkStyle.opacity;
    ctx.beginPath();
    ctx.moveTo(previous.x, previous.y);
    ctx.lineTo(x, y);
    ctx.stroke();
    ctx.restore();
    this.wetInk.set(event.pointerId, { x, y });
  }

  endWetInk(pointerId) {
    if (pointerId === undefined) this.wetInk.clear();
    else this.wetInk.delete(pointerId);
  }

  setWetInkStyle(style) {
    this.wetInkStyle = style;
  }

  // ---- Command replay ----------------------------------------------------------------------------------------

  // The scratch canvas must be the size of whatever is being drawn into. Sizing it from the on-screen canvas
  // would crop every isolated layer when exporting at a different resolution.
  getScratch(width, height) {
    for (let i = this.scratchCanvases.length - 1; i >= 0; i--) {
      const candidate = this.scratchCanvases[i];
      if (candidate.width === width && candidate.height === height) {
        this.scratchCanvases.splice(i, 1);
        return candidate;
      }
    }
    const created = document.createElement('canvas');
    created.width = width;
    created.height = height;
    return created;
  }

  releaseScratch(canvas) {
    if (this.scratchCanvases.length < 4) this.scratchCanvases.push(canvas);
  }

  clearScratch() {
    this.scratchCanvases.length = 0;
  }

  execute(layer, buffer, count, strings) {
    const rootContext = this.contexts[layer];
    if (!rootContext) return;

    const commands = normalizeBuffer(buffer, count);
    if (!commands) return;

    let ctx = rootContext;
    const groups = [];
    let i = 0;
    let dash = null;

    while (i < count) {
      const op = commands[i++];
      switch (op) {
        case OP.BEGIN_PATH:
          ctx.beginPath();
          break;
        case OP.MOVE_TO:
          ctx.moveTo(commands[i++], commands[i++]);
          break;
        case OP.LINE_TO:
          ctx.lineTo(commands[i++], commands[i++]);
          break;
        case OP.QUAD_TO:
          ctx.quadraticCurveTo(commands[i++], commands[i++], commands[i++], commands[i++]);
          break;
        case OP.CUBIC_TO:
          ctx.bezierCurveTo(commands[i++], commands[i++], commands[i++], commands[i++], commands[i++], commands[i++]);
          break;
        case OP.CLOSE_PATH:
          ctx.closePath();
          break;
        case OP.FILL: {
          const color = commands[i++];
          const alpha = commands[i++];
          const blend = commands[i++];
          const evenOdd = commands[i++];
          ctx.globalAlpha = alpha;
          ctx.globalCompositeOperation = BLEND_NAMES[blend] || 'source-over';
          ctx.fillStyle = cssColor(color);
          ctx.fill(evenOdd ? 'evenodd' : 'nonzero');
          ctx.globalAlpha = 1;
          ctx.globalCompositeOperation = 'source-over';
          break;
        }
        case OP.STROKE: {
          const color = commands[i++];
          const width = commands[i++];
          const alpha = commands[i++];
          const blend = commands[i++];
          const cap = commands[i++];
          const join = commands[i++];
          ctx.globalAlpha = alpha;
          ctx.globalCompositeOperation = BLEND_NAMES[blend] || 'source-over';
          ctx.strokeStyle = cssColor(color);
          ctx.lineWidth = width;
          ctx.lineCap = CAP_NAMES[cap] || 'round';
          ctx.lineJoin = JOIN_NAMES[join] || 'round';
          if (dash) { ctx.setLineDash(dash); dash = null; } else { ctx.setLineDash([]); }
          ctx.stroke();
          ctx.globalAlpha = 1;
          ctx.globalCompositeOperation = 'source-over';
          ctx.setLineDash([]);
          break;
        }
        case OP.SET_DASH: {
          const length = commands[i++];
          dash = [];
          for (let d = 0; d < length; d++) dash.push(commands[i++]);
          break;
        }
        case OP.SAVE:
          ctx.save();
          break;
        case OP.RESTORE:
          ctx.restore();
          break;
        case OP.TRANSFORM:
          ctx.transform(commands[i++], commands[i++], commands[i++], commands[i++], commands[i++], commands[i++]);
          break;
        case OP.SET_TRANSFORM:
          ctx.setTransform(commands[i++], commands[i++], commands[i++], commands[i++], commands[i++], commands[i++]);
          break;
        case OP.CLIP_RECT: {
          const x = commands[i++];
          const y = commands[i++];
          const w = commands[i++];
          const h = commands[i++];
          ctx.beginPath();
          ctx.rect(x, y, w, h);
          ctx.clip();
          break;
        }
        case OP.CLEAR: {
          const color = commands[i++];
          const transform = ctx.getTransform();
          ctx.setTransform(1, 0, 0, 1, 0, 0);
          if (color === 0) {
            ctx.clearRect(0, 0, ctx.canvas.width, ctx.canvas.height);
          } else {
            ctx.globalCompositeOperation = 'copy';
            ctx.fillStyle = cssColor(color);
            ctx.fillRect(0, 0, ctx.canvas.width, ctx.canvas.height);
            ctx.globalCompositeOperation = 'source-over';
          }
          ctx.setTransform(transform);
          break;
        }
        case OP.CLEAR_RECT: {
          const x = commands[i++];
          const y = commands[i++];
          const w = commands[i++];
          const h = commands[i++];
          ctx.clearRect(x, y, w, h);
          break;
        }
        case OP.DRAW_IMAGE: {
          const index = commands[i++];
          const dx = commands[i++];
          const dy = commands[i++];
          const dw = commands[i++];
          const dh = commands[i++];
          const hasCrop = commands[i++];
          const sx = commands[i++];
          const sy = commands[i++];
          const sw = commands[i++];
          const sh = commands[i++];
          const alpha = commands[i++];
          const image = this.images.get(strings[index]);
          if (image && image.complete && image.naturalWidth > 0) {
            ctx.globalAlpha = alpha;
            if (hasCrop) ctx.drawImage(image, sx, sy, sw, sh, dx, dy, dw, dh);
            else ctx.drawImage(image, dx, dy, dw, dh);
            ctx.globalAlpha = 1;
          }
          break;
        }
        case OP.DRAW_TEXT: {
          const textIndex = commands[i++];
          const fontIndex = commands[i++];
          const x = commands[i++];
          const y = commands[i++];
          const size = commands[i++];
          const lineHeight = commands[i++];
          const color = commands[i++];
          const alpha = commands[i++];
          const align = commands[i++];
          ctx.globalAlpha = alpha;
          ctx.fillStyle = cssColor(color);
          ctx.font = strings[fontIndex];
          ctx.textAlign = ALIGN_NAMES[align] || 'left';
          ctx.textBaseline = 'alphabetic';
          const lines = strings[textIndex].split('\n');
          for (let l = 0; l < lines.length; l++) {
            ctx.fillText(lines[l], x, y + size * 0.82 + l * lineHeight);
          }
          ctx.globalAlpha = 1;
          break;
        }
        case OP.PUSH_GROUP: {
          const opacity = commands[i++];
          const blend = commands[i++];
          // An isolated group needs its own bitmap: canvas composite operations always apply to the whole
          // surface, so an eraser inside a group would otherwise reach content drawn before it.
          const scratch = this.getScratch(ctx.canvas.width, ctx.canvas.height);
          const scratchCtx = scratch.getContext('2d');
          // A pooled context can carry a clip region or an unbalanced save from an earlier group.
          if (typeof scratchCtx.reset === 'function') {
            scratchCtx.reset();
          } else {
            scratchCtx.setTransform(1, 0, 0, 1, 0, 0);
            scratchCtx.clearRect(0, 0, scratch.width, scratch.height);
          }
          scratchCtx.setTransform(ctx.getTransform());
          groups.push({ parent: ctx, canvas: scratch, opacity, blend });
          ctx = scratchCtx;
          break;
        }
        case OP.POP_GROUP: {
          const group = groups.pop();
          if (!group) break;
          const parent = group.parent;
          ctx = parent;
          parent.save();
          parent.setTransform(1, 0, 0, 1, 0, 0);
          parent.globalAlpha = group.opacity;
          parent.globalCompositeOperation = BLEND_NAMES[group.blend] || 'source-over';
          parent.drawImage(group.canvas, 0, 0);
          parent.restore();
          this.releaseScratch(group.canvas);
          break;
        }
        default:
          // An unknown opcode means the buffer is out of sync; stopping is safer than misinterpreting it.
          return;
      }
    }

    while (groups.length > 0) {
      const group = groups.pop();
      this.releaseScratch(group.canvas);
    }
  }

  async loadImage(uri) {
    if (this.images.has(uri)) {
      const existing = this.images.get(uri);
      if (existing.complete && existing.naturalWidth > 0) return { width: existing.naturalWidth, height: existing.naturalHeight };
    }
    const image = new Image();
    image.decoding = 'async';
    if (!uri.startsWith('data:') && !uri.startsWith('blob:')) image.crossOrigin = 'anonymous';
    const loaded = new Promise((resolve, reject) => {
      image.onload = () => resolve();
      image.onerror = () => reject(new Error('The image could not be loaded.'));
    });
    image.src = uri;
    await loaded;
    this.images.set(uri, image);
    return { width: image.naturalWidth, height: image.naturalHeight };
  }

  measureText(text, font) {
    const ctx = this.contexts.document || this.contexts.overlay;
    if (!ctx) return { width: 0, lines: 1 };
    ctx.save();
    ctx.font = font;
    let width = 0;
    let lines = 0;
    for (const line of text.split('\n')) {
      width = Math.max(width, ctx.measureText(line).width);
      lines++;
    }
    ctx.restore();
    return { width, lines };
  }

  dispose() {
    if (this.disposed) return;
    this.disposed = true;
    const target = this.hitTarget;
    target.removeEventListener('pointerdown', this.onPointerDown);
    target.removeEventListener('pointermove', this.onPointerMove);
    target.removeEventListener('pointerup', this.onPointerUp);
    target.removeEventListener('pointercancel', this.onPointerCancel);
    target.removeEventListener('pointerleave', this.onPointerCancel);
    target.removeEventListener('contextmenu', this.onContextMenu);
    this.root.removeEventListener('keydown', this.onKeyDown);
    this.root.removeEventListener('focusout', this.onFocusOut);
    this.resizeObserver?.disconnect();
    this.stopWatchingPixelRatio();
    this.images.clear();
    this.scratchCanvases.length = 0;
    this.callback = null;
  }
}

// ---- Module API ------------------------------------------------------------------------------------------

export function attach(id, root, options) {
  detach(id);
  const surface = new Surface(id, root, options);
  surfaces.set(id, surface);
  const rect = root.getBoundingClientRect();
  return {
    width: rect.width,
    height: rect.height,
    devicePixelRatio: window.devicePixelRatio || 1,
    maxTouchPoints: navigator.maxTouchPoints || 0,
    coalescedEvents: typeof PointerEvent !== 'undefined' && 'getCoalescedEvents' in PointerEvent.prototype,
    predictedEvents: typeof PointerEvent !== 'undefined' && 'getPredictedEvents' in PointerEvent.prototype,
    pointerEvents: typeof PointerEvent !== 'undefined',
    tilt: typeof PointerEvent !== 'undefined' && 'tiltX' in PointerEvent.prototype,
    twist: typeof PointerEvent !== 'undefined' && 'twist' in PointerEvent.prototype,
    offscreenCanvas: typeof OffscreenCanvas !== 'undefined',
    lowLatency: !!surface.lowLatency,
  };
}

export function detach(id) {
  const surface = surfaces.get(id);
  if (surface) {
    surface.dispose();
    surfaces.delete(id);
  }
}

export function setCallbacks(id, callbacks) {
  const surface = surfaces.get(id);
  if (surface) surface.callback = callbacks;
}

// Wires the surface to a .NET object reference. Sample batches are sent as a plain array once per animation
// frame, never per pointer event.
export function setDotNetCallbacks(id, reference) {
  const surface = surfaces.get(id);
  if (!surface) return;
  // A rejected interop promise would otherwise vanish silently and the canvas would just stop responding.
  const report = (what) => (error) => console.error(`[BlazorInk] ${what} failed`, error);
  surface.callback = {
    samples: (buffer, count) => {
      const view = buffer.subarray(0, count * SAMPLE_SIZE);
      reference.invokeMethodAsync('OnPointerSamples', Array.from(view), count).catch(report('pointer input'));
    },
    resized: (width, height, dpr) => reference.invokeMethodAsync('OnResized', width, height, dpr).catch(report('resize')),
    key: (key, ctrl, shift, alt) => reference.invokeMethodAsync('OnKeyDown', key, ctrl, shift, alt).catch(report('keyboard input')),
    textKey: (key, value) => reference.invokeMethodAsync('OnTextEditorKey', key, value ?? '').catch(report('text editing')),
  };
}

export function setSize(id, cssWidth, cssHeight, dpr) {
  surfaces.get(id)?.setSize(cssWidth, cssHeight, dpr);
}

export function execute(id, layer, commands, count, strings) {
  surfaces.get(id)?.execute(layer, commands, count, strings || []);
}

export function setWetInkStyle(id, color, width, opacity) {
  surfaces.get(id)?.setWetInkStyle(color ? { color, width, opacity } : null);
}

export function setCursor(id, cursor) {
  const surface = surfaces.get(id);
  if (surface) surface.hitTarget.style.cursor = cursor;
}

export function loadImage(id, uri) {
  const surface = surfaces.get(id);
  return surface ? surface.loadImage(uri) : Promise.reject(new Error('Unknown surface.'));
}

export function measureText(id, text, font) {
  return surfaces.get(id)?.measureText(text, font) ?? { width: 0, lines: 1 };
}

export function getSize(id) {
  const surface = surfaces.get(id);
  if (!surface) return null;
  const rect = surface.root.getBoundingClientRect();
  return { width: rect.width, height: rect.height, devicePixelRatio: window.devicePixelRatio || 1 };
}

// Renders a command buffer into a detached canvas and returns encoded image bytes. Used by PNG export so the
// output uses the browser's real fonts and image decoders.
export async function renderToImage(id, pixelWidth, pixelHeight, commands, count, strings, mimeType, quality) {
  const surface = surfaces.get(id);
  if (!surface) throw new Error('Unknown surface.');
  const canvas = document.createElement('canvas');
  canvas.width = Math.max(1, pixelWidth);
  canvas.height = Math.max(1, pixelHeight);
  const ctx = canvas.getContext('2d');

  const previous = surface.contexts.export;
  surface.contexts.export = ctx;
  try {
    surface.execute('export', commands, count, strings || []);
  } finally {
    surface.contexts.export = previous;
  }

  const blob = await new Promise((resolve) => canvas.toBlob(resolve, mimeType || 'image/png', quality));
  if (!blob) throw new Error('The canvas could not be encoded.');
  const buffer = await blob.arrayBuffer();
  return new Uint8Array(buffer);
}

export function saveFile(fileName, bytes, mimeType) {
  const blob = new Blob([bytes], { type: mimeType || 'application/octet-stream' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10000);
}

export function readFile(input) {
  const file = input?.files?.[0];
  if (!file) return Promise.resolve(null);
  return file.arrayBuffer().then((buffer) => ({
    name: file.name,
    type: file.type,
    bytes: new Uint8Array(buffer),
  }));
}

export function prefersReducedMotion() {
  return typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
}

export function prefersHighContrast() {
  return typeof matchMedia === 'function' && (matchMedia('(forced-colors: active)').matches || matchMedia('(prefers-contrast: more)').matches);
}
