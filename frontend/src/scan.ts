/** What the plugin's endpoints answer (backend/NetworkScan.cs), and the dialog's own matching (docs/architecture.md §7.42). */

export interface ScannedPort {
  port: number
  name: string | null
}

export interface DiscoveredDevice {
  ipAddress: string
  macAddress: string | null
  vendor: string | null
  isVirtual: boolean
  hostname: string | null
  names: string[]
  operatingSystem: string | null
  ttl: number | null
  respondedTo: string[]
  ports: ScannedPort[]
  details: string[]
}

export interface NetworkScan {
  id: string
  subnet: string
  state: 'running' | 'finished' | 'cancelled' | 'failed'
  total: number
  done: number
  devices: DiscoveredDevice[]
  notes: string[]
  startedAtUtc: string
  finishedAtUtc: string | null
  message: string | null
}

export interface LocalNetwork {
  subnet: string
  interface: string
  address: string
}

/** An NKeeper host, as core's `GET /api/hosts` gives it. */
export interface KnownHost {
  id: string
  name: string
  hostname: string | null
  ipAddress: string | null
  macAddress: string | null
}

/** A loaded service type, as core's `GET /api/plugins` lists it. */
export interface ServiceTypeInfo {
  id: string
  label: string
  defaultPort: number | null
  settings: { key: string; required: boolean }[]
}

export type MatchedBy = 'mac' | 'ip' | 'hostname'

/**
 * Matches a found machine to an NKeeper host, as 1.x's NetworkDeviceMatcher: by MAC address, else IP address, else
 * hostname (each of its names, full or short, against a host's hostname, short hostname or name).
 */
export function matchDevice(device: DiscoveredDevice, hosts: KnownHost[]): { host: KnownHost; matchedBy: MatchedBy } | null {
  const lower = (s: string) => s.toLowerCase()
  const byMac = new Map<string, KnownHost>()
  const byIp = new Map<string, KnownHost>()
  const byName = new Map<string, KnownHost>()
  for (const host of hosts) {
    if (host.macAddress && !byMac.has(lower(host.macAddress))) {
      byMac.set(lower(host.macAddress), host)
    }
    if (host.ipAddress && !byIp.has(host.ipAddress)) {
      byIp.set(host.ipAddress, host)
    }
    for (const name of [host.hostname, host.hostname?.split('.')[0], host.name]) {
      if (name && !byName.has(lower(name))) {
        byName.set(lower(name), host)
      }
    }
  }
  const mac = device.macAddress ? byMac.get(lower(device.macAddress)) : undefined
  if (mac) {
    return { host: mac, matchedBy: 'mac' }
  }
  const ip = byIp.get(device.ipAddress)
  if (ip) {
    return { host: ip, matchedBy: 'ip' }
  }
  for (const name of deviceNames(device)) {
    const found = byName.get(lower(name)) ?? byName.get(lower(name.split('.')[0] ?? name))
    if (found) {
      return { host: found, matchedBy: 'hostname' }
    }
  }
  return null
}

/** The machine's names without where they came from: "nas.lan (reverse DNS)" is "nas.lan". */
function deviceNames(device: DiscoveredDevice): string[] {
  return device.names.map((n) => n.replace(/ \([^)]*\)$/, ''))
}

/**
 * The service type a port becomes: the one loaded type whose default port it is, with no settings it would need to be
 * given. None when no type, or more than one, has that port.
 */
export function serviceTypeFor(port: number, types: ServiceTypeInfo[]): ServiceTypeInfo | null {
  const matching = types.filter((t) => t.defaultPort === port && !t.settings.some((s) => s.required))
  return matching.length === 1 ? (matching[0] ?? null) : null
}

/**
 * The host to add for a machine, as 1.x proposed it: named by its short hostname, else its address (its address when
 * a host already has that name; `taken` holds the names in use, lower-cased), with a service for each open port whose
 * type is known, and its operating system as the `os` tag.
 */
export function newHost(device: DiscoveredDevice, subnet: string, startedAtUtc: string, types: ServiceTypeInfo[], taken: ReadonlySet<string>) {
  const proposed = device.hostname ? (device.hostname.split('.')[0] ?? device.hostname) : device.ipAddress
  const services: { type: string; port: number | null; settings: Record<string, never> }[] = []
  for (const port of device.ports) {
    const type = serviceTypeFor(port.port, types)
    if (type && !services.some((s) => s.type === type.id)) {
      services.push({ type: type.id, port: null, settings: {} })
    }
  }
  return {
    name: taken.has(proposed.toLowerCase()) ? device.ipAddress : proposed,
    hostname: device.hostname,
    ipAddress: device.ipAddress,
    macAddress: device.macAddress,
    description: `Found by a network scan of ${subnet} on ${startedAtUtc.slice(0, 10)}.`,
    isVirtualMachine: device.isVirtual,
    tags: device.operatingSystem ? [{ name: 'os', value: device.operatingSystem }] : [],
    services,
    credentials: [],
  }
}
