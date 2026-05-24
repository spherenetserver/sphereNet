<template>
  <div class="gump-page">
    <section class="toolbox">
      <h2>Gump Designer</h2>
      <p class="hint">Create a layout, then export it as Sphere script.</p>
      <button v-for="tool in tools" :key="tool.type" class="tool-btn" @click="addControl(tool.type)">
        {{ tool.label }}
      </button>
      <button class="tool-btn danger" @click="removeSelected" :disabled="selectedIndex < 0">Remove Selected</button>
    </section>

    <section class="canvas-panel">
      <div class="canvas-header">
        <input v-model="dialogName" class="name-input" placeholder="d_panel_test" />
        <span>{{ controls.length }} controls</span>
      </div>
      <div class="canvas" :style="{ width: canvasWidth + 'px', height: canvasHeight + 'px' }">
        <button
          v-for="(control, index) in controls"
          :key="control.id"
          class="control"
          :class="[control.type, { selected: selectedIndex === index }]"
          :style="controlStyle(control)"
          @click="selectedIndex = index"
        >
          {{ controlLabel(control) }}
        </button>
      </div>
    </section>

    <section class="props">
      <h3>Properties</h3>
      <template v-if="selected">
        <label>X <input v-model.number="selected.x" type="number" /></label>
        <label>Y <input v-model.number="selected.y" type="number" /></label>
        <label v-if="hasSize(selected)">Width <input v-model.number="selected.width" type="number" /></label>
        <label v-if="hasSize(selected)">Height <input v-model.number="selected.height" type="number" /></label>
        <label>Hue <input v-model.number="selected.hue" type="number" /></label>
        <label v-if="selected.type === 'resizepic' || selected.type === 'gumppic'">
          Gump ID <input v-model.number="selected.gumpId" type="number" />
        </label>
        <label v-if="selected.type === 'button'">
          Button ID <input v-model.number="selected.buttonId" type="number" />
        </label>
        <label v-if="selected.type === 'text' || selected.type === 'htmlgump'">
          Text <textarea v-model="selected.text" />
        </label>
      </template>
      <p v-else class="hint">Select a control to edit it.</p>

      <h3>Script Export</h3>
      <textarea class="export" :value="scriptExport" readonly />
      <button class="copy-btn" @click="copyExport">Copy Script</button>
      <p v-if="copyMsg" class="hint">{{ copyMsg }}</p>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'

type ControlType = 'resizepic' | 'text' | 'button' | 'gumppic' | 'htmlgump'

interface GumpControl {
  id: number
  type: ControlType
  x: number
  y: number
  width: number
  height: number
  hue: number
  gumpId: number
  pressedId: number
  buttonId: number
  text: string
}

const canvasWidth = 520
const canvasHeight = 420
const dialogName = ref('d_panel_gump')
const selectedIndex = ref(-1)
const copyMsg = ref('')
let nextId = 1

const tools = [
  { type: 'resizepic' as const, label: 'ResizePic' },
  { type: 'text' as const, label: 'Text' },
  { type: 'button' as const, label: 'Button' },
  { type: 'gumppic' as const, label: 'GumpPic' },
  { type: 'htmlgump' as const, label: 'HtmlGump' },
]

const controls = ref<GumpControl[]>([
  makeControl('resizepic', 20, 20),
  makeControl('text', 50, 50),
])

const selected = computed(() => selectedIndex.value >= 0 ? controls.value[selectedIndex.value] : null)

const scriptExport = computed(() => {
  const name = sanitizeDialogName(dialogName.value)
  const lines = [`[DIALOG ${name}]`, '0,0']
  for (const c of controls.value) {
    switch (c.type) {
      case 'resizepic':
        lines.push(`resizepic ${c.x} ${c.y} ${c.gumpId} ${c.width} ${c.height}`)
        break
      case 'text':
        lines.push(`text ${c.x} ${c.y} ${c.hue} ${quote(c.text)}`)
        break
      case 'button':
        lines.push(`button ${c.x} ${c.y} ${c.gumpId} ${c.pressedId} 1 0 ${c.buttonId}`)
        break
      case 'gumppic':
        lines.push(`gumppic ${c.x} ${c.y} ${c.gumpId}`)
        break
      case 'htmlgump':
        lines.push(`htmlgump ${c.x} ${c.y} ${c.width} ${c.height} ${quote(c.text)} 1 1`)
        break
    }
  }
  lines.push('', '[DIALOG ' + name + ' BUTTON]', 'ON=1', 'RETURN 1')
  return lines.join('\n')
})

function makeControl(type: ControlType, x = 40, y = 40): GumpControl {
  return {
    id: nextId++,
    type,
    x,
    y,
    width: type === 'resizepic' ? 220 : 140,
    height: type === 'resizepic' ? 160 : 40,
    hue: 0,
    gumpId: type === 'button' ? 4005 : 5054,
    pressedId: 4007,
    buttonId: 1,
    text: type === 'htmlgump' ? 'Html text' : 'Text',
  }
}

function addControl(type: ControlType) {
  controls.value.push(makeControl(type, 40 + controls.value.length * 12, 40 + controls.value.length * 12))
  selectedIndex.value = controls.value.length - 1
}

function removeSelected() {
  if (selectedIndex.value < 0) return
  controls.value.splice(selectedIndex.value, 1)
  selectedIndex.value = -1
}

function hasSize(control: GumpControl) {
  return control.type === 'resizepic' || control.type === 'htmlgump'
}

function controlStyle(control: GumpControl) {
  return {
    left: control.x + 'px',
    top: control.y + 'px',
    width: (hasSize(control) ? control.width : 120) + 'px',
    height: (hasSize(control) ? control.height : 28) + 'px',
  }
}

function controlLabel(control: GumpControl) {
  if (control.type === 'text' || control.type === 'htmlgump') return control.text
  if (control.type === 'button') return `Button ${control.buttonId}`
  return control.type
}

function sanitizeDialogName(name: string) {
  const safe = name.trim().replace(/[^a-zA-Z0-9_]/g, '_')
  return safe.length > 0 ? safe : 'd_panel_gump'
}

function quote(text: string) {
  return `"${text.replace(/"/g, '\\"')}"`
}

async function copyExport() {
  await navigator.clipboard.writeText(scriptExport.value)
  copyMsg.value = 'Copied to clipboard'
  window.setTimeout(() => (copyMsg.value = ''), 1500)
}
</script>

<style scoped>
.gump-page {
  display: grid;
  grid-template-columns: 220px 1fr 340px;
  gap: 16px;
  height: 100%;
  min-height: 0;
}

.toolbox,
.canvas-panel,
.props {
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 14px;
  min-height: 0;
}

h2, h3 { margin: 0 0 10px; color: var(--text-primary); }
.hint { color: var(--text-muted); font-size: 12px; }

.tool-btn,
.copy-btn {
  width: 100%;
  margin: 5px 0;
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--bg-primary);
  color: var(--text-primary);
  cursor: pointer;
}

.tool-btn:hover,
.copy-btn:hover { background: var(--bg-tertiary); }
.tool-btn.danger { color: var(--danger); }
.tool-btn:disabled { opacity: 0.5; cursor: not-allowed; }

.canvas-panel { overflow: auto; }
.canvas-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 10px;
  color: var(--text-muted);
  font-size: 12px;
}

.name-input {
  background: var(--bg-primary);
  color: var(--text-primary);
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 7px 9px;
}

.canvas {
  position: relative;
  background:
    linear-gradient(var(--border) 1px, transparent 1px),
    linear-gradient(90deg, var(--border) 1px, transparent 1px),
    var(--bg-primary);
  background-size: 20px 20px;
  border: 1px solid var(--border);
  border-radius: 8px;
}

.control {
  position: absolute;
  border: 1px dashed var(--accent);
  background: rgba(88, 166, 255, 0.12);
  color: var(--text-primary);
  overflow: hidden;
  cursor: pointer;
}

.control.selected { outline: 2px solid var(--accent); }
.control.resizepic { background: rgba(255, 255, 255, 0.05); }
.control.button { background: rgba(126, 231, 135, 0.13); }
.control.gumppic { background: rgba(240, 180, 41, 0.13); }

.props {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

label {
  display: grid;
  grid-template-columns: 80px 1fr;
  align-items: center;
  gap: 8px;
  color: var(--text-muted);
  font-size: 12px;
}

input,
textarea {
  background: var(--bg-primary);
  color: var(--text-primary);
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 7px 9px;
}

.export {
  flex: 1;
  min-height: 220px;
  font-family: Consolas, Monaco, monospace;
  font-size: 12px;
  resize: none;
}
</style>
