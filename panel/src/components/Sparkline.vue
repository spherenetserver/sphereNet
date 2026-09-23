<template>
  <svg class="sparkline" :viewBox="`0 0 ${W} ${H}`" preserveAspectRatio="none" role="img" :aria-label="label">
    <title>{{ label }}</title>
    <template v-if="points">
      <path :d="points.area" class="area" :style="{ fill: color }" />
      <path :d="points.line" class="line" :style="{ stroke: color }" vector-effect="non-scaling-stroke" />
    </template>
    <line v-else x1="0" :y1="H - 1" :x2="W" :y2="H - 1" class="empty" vector-effect="non-scaling-stroke" />
  </svg>
</template>

<script setup lang="ts">
import { computed } from 'vue'

const props = withDefaults(defineProps<{
  values: readonly number[]
  color?: string
  label?: string
  /** Pin the bottom of the scale to 0 (counts, memory); otherwise fit min..max. */
  zeroBased?: boolean
}>(), {
  color: 'var(--accent)',
  label: '',
  zeroBased: true,
})

// Fixed coordinate space; the SVG stretches to its box.
const W = 300
const H = 60
const PAD = 2

const points = computed(() => {
  const v = props.values.filter(Number.isFinite)
  if (v.length < 2) return null
  let lo = props.zeroBased ? Math.min(0, ...v) : Math.min(...v)
  let hi = Math.max(...v)
  // Flat series: give it a range so it draws as a line instead of dividing by 0.
  if (hi === lo) {
    hi = lo + 1
    if (!props.zeroBased) lo -= 1
  }
  const dx = W / (v.length - 1)
  const y = (n: number) => PAD + (H - 2 * PAD) * (1 - (n - lo) / (hi - lo))
  const coords = v.map((n, i) => `${(i * dx).toFixed(1)},${y(n).toFixed(1)}`)
  const line = 'M' + coords.join(' L')
  const area = `${line} L${W},${H} L0,${H} Z`
  return { line, area }
})
</script>

<style scoped>
.sparkline { display: block; width: 100%; height: 44px; }
.line { fill: none; stroke-width: 1.5; stroke-linejoin: round; }
.area { opacity: 0.12; stroke: none; }
.empty { stroke: var(--border); stroke-width: 1; stroke-dasharray: 3 3; }
</style>
