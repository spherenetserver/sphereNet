/** Parse a character serial the way the server does: `0x` hex, Sphere-style
 *  hex with a leading 0 (`01a2b`), or decimal. Only the character range
 *  (1..0x3FFFFFFF) is accepted; anything else is null. */
export function parseSerial(input: string): number | null {
  const raw = input.trim()
  let value: number
  if (/^0x[0-9a-f]+$/i.test(raw)) value = parseInt(raw.slice(2), 16)
  else if (/^0[0-9a-f]+$/i.test(raw)) value = parseInt(raw, 16)
  else if (/^[1-9][0-9]*$/.test(raw)) value = parseInt(raw, 10)
  else return null
  return Number.isSafeInteger(value) && value > 0 && value < 0x40000000 ? value : null
}

export function hexSerial(serial: number): string {
  return '0x' + (serial >>> 0).toString(16).toUpperCase().padStart(8, '0')
}

export function hexId(value: number, digits = 4): string {
  return '0x' + (value >>> 0).toString(16).toUpperCase().padStart(digits, '0')
}

/** Equipment layer names (UO LAYER_TYPE numbering). */
const layerNames: Record<number, string> = {
  1: 'OneHanded', 2: 'TwoHanded', 3: 'Shoes', 4: 'Pants', 5: 'Shirt', 6: 'Helm',
  7: 'Gloves', 8: 'Ring', 9: 'Talisman', 10: 'Neck', 11: 'Hair', 12: 'Waist',
  13: 'Chest', 14: 'Bracelet', 15: 'Face', 16: 'FacialHair', 17: 'Tunic',
  18: 'Earrings', 19: 'Arms', 20: 'Cape', 21: 'Pack', 22: 'Robe', 23: 'Skirt',
  24: 'Legs', 25: 'Mount',
}

export function layerName(layer: number): string {
  return layerNames[layer] ?? `Layer ${layer}`
}

const FRAME_KEY = 'spherenet.paperdoll.frame'

/** Whether the paperdoll picture is shown with its frame: the viewer's last
 *  choice, on by default (and when storage is unavailable). */
export function readFramePreference(): boolean {
  try {
    return localStorage.getItem(FRAME_KEY) !== '0'
  } catch {
    return true
  }
}

export function saveFramePreference(frame: boolean): void {
  try {
    localStorage.setItem(FRAME_KEY, frame ? '1' : '0')
  } catch {
    // Private mode / blocked storage: the choice just won't survive a reload.
  }
}
