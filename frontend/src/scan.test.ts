import { describe, expect, it } from 'vitest'
import { matchDevice, newHost, serviceTypeFor, type DiscoveredDevice, type KnownHost, type ServiceTypeInfo } from './scan'

function device(overrides: Partial<DiscoveredDevice>): DiscoveredDevice {
  return { ipAddress: '10.0.0.99', macAddress: null, vendor: null, isVirtual: false, hostname: null, names: [], operatingSystem: null, ttl: null, respondedTo: ['ping'], ports: [], details: [], ...overrides }
}

const web: KnownHost = { id: 'web', name: 'web01', hostname: 'web01.corp.example', ipAddress: '10.0.0.30', macAddress: 'BC:24:11:AA:BB:CC' }
const db: KnownHost = { id: 'db', name: 'db01', hostname: null, ipAddress: '10.0.0.40', macAddress: null }

describe('matching found machines (1.x NetworkDeviceMatcher)', () => {
  it('prefers the MAC address, then the IP address, then a hostname', () => {
    expect(matchDevice(device({ macAddress: 'bc:24:11:aa:bb:cc', ipAddress: '10.0.0.40' }), [web, db])).toEqual({ host: web, matchedBy: 'mac' })
    expect(matchDevice(device({ macAddress: '00:11:22:33:44:55', ipAddress: '10.0.0.40', names: ['web01 (NetBIOS)'] }), [web, db])).toEqual({ host: db, matchedBy: 'ip' })
    expect(matchDevice(device({ names: ['WEB01.corp.example (reverse DNS)'] }), [web, db])).toEqual({ host: web, matchedBy: 'hostname' })
    expect(matchDevice(device({ names: ['db01.other.example (certificate on port 443)'] }), [web, db])).toEqual({ host: db, matchedBy: 'hostname' })
    expect(matchDevice(device({ names: ['app01 (NetBIOS)'] }), [web, db])).toBeNull()
  })
})

describe('adding a found machine', () => {
  const types: ServiceTypeInfo[] = [
    { id: 'ssh', label: 'SSH', defaultPort: 22, settings: [] },
    { id: 'web', label: 'Web', defaultPort: 443, settings: [] },
    { id: 'idrac', label: 'iDRAC', defaultPort: 443, settings: [] },
    { id: 'zabbix', label: 'Zabbix', defaultPort: 8443, settings: [{ key: 'path', required: true }] },
  ]

  it('gives a port the one service type whose default port it is, and none that needs settings', () => {
    expect(serviceTypeFor(22, types)?.id).toBe('ssh')
    expect(serviceTypeFor(443, types)).toBeNull()
    expect(serviceTypeFor(8443, types)).toBeNull()
  })

  it('names it by its short hostname, else its address, with its services and os tag', () => {
    const found = device({ hostname: 'nas.home.lan', macAddress: 'AA:BB:CC:DD:EE:01', operatingSystem: 'Debian', isVirtual: true, ports: [{ port: 22, name: 'SSH' }, { port: 443, name: 'HTTPS' }] })
    expect(newHost(found, '10.0.0.0/24', '2026-11-19T01:00:00Z', types, new Set())).toEqual({
      name: 'nas',
      hostname: 'nas.home.lan',
      ipAddress: '10.0.0.99',
      macAddress: 'AA:BB:CC:DD:EE:01',
      description: 'Found by a network scan of 10.0.0.0/24 on 2026-11-19.',
      isVirtualMachine: true,
      tags: [{ name: 'os', value: 'Debian' }],
      services: [{ type: 'ssh', port: null, settings: {} }],
      credentials: [],
    })
    expect(newHost(found, '10.0.0.0/24', '2026-11-19T01:00:00Z', types, new Set(['nas'])).name).toBe('10.0.0.99')
    expect(newHost(device({}), '10.0.0.0/24', '2026-11-19T01:00:00Z', types, new Set()).name).toBe('10.0.0.99')
  })
})
