/** Client-side sanity check for an IP block entry. The server stays the
 *  authority (it answers 400 on anything it can't parse); this only catches
 *  typos before a round trip. */

const IPV4 = /^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$/

export function isValidIpv4(s: string): boolean {
  return IPV4.test(s)
}

export function isValidIpv6(input: string): boolean {
  let s = input
  // Zone id (fe80::1%eth0) is not part of the address.
  const pct = s.indexOf('%')
  if (pct >= 0) {
    if (pct === s.length - 1) return false
    s = s.slice(0, pct)
  }
  if (!s.includes(':')) return false

  // Embedded IPv4 tail (::ffff:192.0.2.1) counts as two groups.
  const lastColon = s.lastIndexOf(':')
  const tail = s.slice(lastColon + 1)
  if (tail.includes('.')) {
    if (!isValidIpv4(tail)) return false
    s = s.slice(0, lastColon + 1) + '0:0'
  }

  const halves = s.split('::')
  if (halves.length > 2) return false
  const hex = /^[0-9a-fA-F]{1,4}$/
  const parse = (part: string) => (part === '' ? [] : part.split(':'))
  const head = parse(halves[0])
  const rest = halves.length === 2 ? parse(halves[1]) : []
  if (![...head, ...rest].every(g => hex.test(g))) return false
  const count = head.length + rest.length
  return halves.length === 2 ? count <= 7 : count === 8
}

export function isValidIp(s: string): boolean {
  const t = s.trim()
  return isValidIpv4(t) || isValidIpv6(t)
}
