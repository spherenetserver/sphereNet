import { describe, expect, it } from 'vitest'
import { isValidIp } from './ip'

describe('isValidIp', () => {
  it.each([
    '127.0.0.1', '203.0.113.7', '0.0.0.0', '255.255.255.255',
    '::1', '::', '2001:db8::1', 'fe80::1%eth0', '2001:0db8:0000:0000:0000:ff00:0042:8329',
    '::ffff:192.0.2.1', '1::',
  ])('accepts %s', ip => expect(isValidIp(ip)).toBe(true))

  it.each([
    '', '256.1.1.1', '1.2.3', '1.2.3.4.5', '01.2.3.4', 'abc', '1.2.3.4/24',
    '2001:db8:::1', '1:2:3:4:5:6:7:8:9', 'gggg::1', '1::2::3', ':1:2:3:4:5:6:7', 'fe80::1%',
    '::ffff:999.0.2.1',
  ])('rejects %s', ip => expect(isValidIp(ip)).toBe(false))
})
