/** Copies text to the clipboard. `navigator.clipboard` only exists in a secure
 *  context (https or localhost); a panel reached over plain http on a LAN IP
 *  doesn't get it, so fall back to a hidden textarea + execCommand('copy').
 *  Resolves to whether the copy went through. */
export async function copyText(text: string): Promise<boolean> {
  if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText && window.isSecureContext !== false) {
    try {
      await navigator.clipboard.writeText(text)
      return true
    } catch {
      // Permission denied or document not focused: try the legacy path.
    }
  }
  return legacyCopy(text)
}

function legacyCopy(text: string): boolean {
  const ta = document.createElement('textarea')
  ta.value = text
  ta.setAttribute('readonly', '')
  ta.style.position = 'fixed'
  ta.style.top = '0'
  ta.style.left = '0'
  ta.style.opacity = '0'
  ta.style.pointerEvents = 'none'
  const active = document.activeElement as HTMLElement | null
  document.body.appendChild(ta)
  try {
    ta.focus()
    ta.select()
    ta.setSelectionRange(0, text.length)
    // Deprecated but still the only option outside a secure context.
    return document.execCommand('copy')
  } catch {
    return false
  } finally {
    document.body.removeChild(ta)
    active?.focus?.()
  }
}
