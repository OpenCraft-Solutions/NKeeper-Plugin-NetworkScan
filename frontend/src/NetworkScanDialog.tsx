import { Fragment, useEffect, useMemo, useState } from 'react'
import { api, ApiError, type InventoryToolProps } from '@nkeeper/sdk'
import { matchDevice, newHost, serviceTypeFor, type DiscoveredDevice, type KnownHost, type LocalNetwork, type NetworkScan, type ServiceTypeInfo } from './scan'

const BASE = '/api/p/network-scan/admin'
const POLL_MS = 1500
const MATCHED_BY = { mac: 'MAC address', ip: 'IP address', hostname: 'hostname' } as const
const FOUND_BY: Record<string, string> = { ping: 'ping', tcp: 'TCP', arp: 'ARP' }

function messageOf(error: unknown, fallback: string): string {
  if (error instanceof ApiError) {
    const errors = (error.problem as { errors?: Record<string, string[]> } | undefined)?.errors
    return Object.values(errors ?? {})[0]?.[0] ?? (error.message || fallback)
  }
  return fallback
}

/**
 * 1.x's Hosts ▸ Scan network (docs/architecture.md §7.42), as an inventory tool: the server sweeps a subnet and learns
 * what it can about every machine that answers. It warns first, because a sweep is loud. Machines NKeeper doesn't have
 * can be added as hosts, through core's own API, with their names, addresses and the services found on them.
 */
export function NetworkScanDialog({ close, hostsChanged }: InventoryToolProps) {
  const [scan, setScan] = useState<NetworkScan | null>(null)
  const [loading, setLoading] = useState(true)
  const [networks, setNetworks] = useState<LocalNetwork[]>([])
  const [hosts, setHosts] = useState<KnownHost[]>([])
  const [types, setTypes] = useState<ServiceTypeInfo[]>([])
  const [subnet, setSubnet] = useState('')
  const [acknowledged, setAcknowledged] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [chosen, setChosen] = useState<ReadonlySet<string>>(new Set())
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(new Set())
  /** Machines added in this dialog: their IP address, and the host's name. */
  const [added, setAdded] = useState<ReadonlyMap<string, string>>(new Map())
  const [addErrors, setAddErrors] = useState<string[]>([])

  // The administrator's last scan (it runs on the server, so it may still be going), the server's networks, and
  // what to match and add machines with: NKeeper's hosts and the loaded service types.
  useEffect(() => {
    let cancelled = false
    void Promise.allSettled([
      api.get<NetworkScan | undefined>(`${BASE}/scans/latest`),
      api.get<LocalNetwork[]>(`${BASE}/networks`),
      api.get<KnownHost[]>('/api/hosts'),
      api.get<{ state: string; serviceTypes?: ServiceTypeInfo[] | null }[]>('/api/plugins'),
    ]).then(([latest, local, known, plugins]) => {
      if (cancelled) {
        return
      }
      const found = local.status === 'fulfilled' ? local.value : []
      setNetworks(found)
      setSubnet((current) => current || found[0]?.subnet || '')
      setHosts(known.status === 'fulfilled' ? known.value : [])
      setTypes(plugins.status === 'fulfilled' ? plugins.value.filter((p) => p.state === 'loaded').flatMap((p) => p.serviceTypes ?? []) : [])
      // A finished scan is only shown again when it's recent; otherwise start afresh.
      if (latest.status === 'fulfilled' && latest.value && (latest.value.state === 'running' || isRecent(latest.value))) {
        setScan(latest.value)
      }
      setLoading(false)
    })
    return () => {
      cancelled = true
    }
  }, [])

  const running = scan?.state === 'running'
  const scanId = scan?.id
  useEffect(() => {
    if (!running || !scanId) {
      return
    }
    const controller = new AbortController()
    const timer = window.setTimeout(() => {
      api.get<NetworkScan>(`${BASE}/scans/${scanId}`, { signal: controller.signal }).then(setScan, (caught: unknown) => {
        if (!controller.signal.aborted) {
          setError(messageOf(caught, 'The scan’s progress couldn’t be read.'))
        }
      })
    }, POLL_MS)
    return () => {
      window.clearTimeout(timer)
      controller.abort()
    }
  }, [running, scanId, scan])

  const matches = useMemo(() => new Map((scan?.devices ?? []).map((d) => [d.ipAddress, matchDevice(d, hosts)])), [scan, hosts])

  async function start() {
    setBusy(true)
    setError(null)
    try {
      setChosen(new Set())
      setExpanded(new Set())
      setAdded(new Map())
      setAddErrors([])
      setScan(await api.post<NetworkScan>(`${BASE}/scans`, { subnet: subnet.trim(), acknowledged }))
    } catch (caught) {
      setError(messageOf(caught, 'The scan couldn’t be started.'))
    } finally {
      setBusy(false)
    }
  }

  async function cancel() {
    if (!scan) {
      return
    }
    setBusy(true)
    try {
      setScan(await api.post<NetworkScan>(`${BASE}/scans/${scan.id}/cancel`))
    } catch (caught) {
      setError(messageOf(caught, 'The scan couldn’t be stopped.'))
    } finally {
      setBusy(false)
    }
  }

  async function addChosen() {
    if (!scan) {
      return
    }
    setBusy(true)
    const errors: string[] = []
    const nowAdded = new Map(added)
    const taken = new Set([...hosts.map((h) => h.name), ...nowAdded.values()].map((name) => name.toLowerCase()))
    for (const device of scan.devices.filter((d) => chosen.has(d.ipAddress) && !matches.get(d.ipAddress) && !nowAdded.has(d.ipAddress))) {
      try {
        const host = await api.post<{ name: string }>('/api/hosts', newHost(device, scan.subnet, scan.startedAtUtc, types, taken))
        nowAdded.set(device.ipAddress, host.name)
        taken.add(host.name.toLowerCase())
      } catch (caught) {
        errors.push(`${device.ipAddress}: ${messageOf(caught, 'it couldn’t be added.')}`)
      }
    }
    setAdded(nowAdded)
    setAddErrors(errors)
    setChosen(new Set())
    setBusy(false)
    if (nowAdded.size > added.size) {
      hostsChanged()
    }
  }

  if (loading) {
    return <p className="nk-ns-muted">Loading…</p>
  }

  if (!scan) {
    return (
      <form
        className="nk-ns"
        aria-label="Start a network scan"
        onSubmit={(event) => {
          event.preventDefault()
          if (acknowledged && subnet.trim()) {
            void start()
          }
        }}
      >
        <p className="nk-ns-warning" role="note">
          <strong>This scan is loud.</strong> The NKeeper server pings every address in the subnet, tries dozens of TCP ports on each, and talks to the services it
          finds (SSH, web, NetBIOS). Firewalls, intrusion detection and prevention systems, honeypots and other security tools may see it as an attack: they may raise
          alerts, block the NKeeper server, or wake up your security team. Only scan networks you’re allowed to, and consider telling whoever watches them first.
        </p>
        {error ? (
          <p className="nk-ns-error" role="alert">
            {error}
          </p>
        ) : null}
        <label className="nk-ns-field">
          Subnet
          <input className="nk-ns-input" placeholder="192.168.1.0/24" value={subnet} onChange={(e) => setSubnet(e.target.value)} autoComplete="off" spellCheck={false} />
          <span className="nk-ns-muted">An IPv4 network in CIDR form, a /20 (4,096 addresses) at most. A /24 takes about a minute.</span>
        </label>
        {networks.length > 0 ? (
          <div className="nk-ns-bar">
            <span className="nk-ns-muted">The server’s networks:</span>
            {networks.map((network) => (
              <button key={network.subnet} type="button" className="nk-ns-chip" onClick={() => setSubnet(network.subnet)} title={`${network.interface}, where the server is ${network.address}`}>
                {network.subnet}
              </button>
            ))}
          </div>
        ) : null}
        <label className="nk-ns-check">
          <input type="checkbox" checked={acknowledged} onChange={(e) => setAcknowledged(e.target.checked)} />I understand this scan is loud and may set off firewalls, honeypots or
          security monitoring, and I’m allowed to scan this network.
        </label>
        <div className="nk-ns-bar nk-ns-end">
          <button type="button" className="nk-ns-button" onClick={close} disabled={busy}>
            Cancel
          </button>
          <button type="submit" className="nk-ns-button" data-danger="" disabled={busy || !acknowledged || subnet.trim() === ''}>
            Start scan
          </button>
        </div>
      </form>
    )
  }

  const addable = scan.devices.filter((d) => !matches.get(d.ipAddress) && !added.has(d.ipAddress))
  const known = scan.devices.length - scan.devices.filter((d) => !matches.get(d.ipAddress)).length
  const percent = scan.total > 0 ? Math.round((scan.done / scan.total) * 100) : 100
  return (
    <div className="nk-ns">
      <div>
        <h3 className="nk-ns-title">Network scan of {scan.subnet}</h3>
        <p className="nk-ns-muted" role="status">
          {running ? `Scanned ${scan.done} of ${scan.total} addresses · ` : ''}
          {scan.devices.length} machine{scan.devices.length === 1 ? '' : 's'} found{known > 0 ? `, ${known} already in NKeeper` : ''}.
        </p>
      </div>
      {running || scan.state !== 'finished' ? (
        <div className="nk-ns-bar">
          <progress max={scan.total} value={scan.done} aria-label="Addresses scanned" />
          <span>
            {running ? `${percent}%` : null}
            {scan.state === 'cancelled' ? 'Stopped. These are the machines found before it stopped.' : null}
          </span>
        </div>
      ) : null}
      {scan.message ? <p className="nk-ns-error">{scan.message}</p> : null}
      {error ? (
        <p className="nk-ns-error" role="alert">
          {error}
        </p>
      ) : null}
      {addErrors.length > 0 ? (
        <div className="nk-ns-error" role="alert">
          Some machines couldn’t be added:
          <ul>
            {addErrors.map((message) => (
              <li key={message}>{message}</li>
            ))}
          </ul>
        </div>
      ) : null}
      {added.size > 0 ? <p className="nk-ns-notice">Added {[...added.values()].join(', ')}.</p> : null}

      {scan.devices.length === 0 ? (
        <p className="nk-ns-muted">{running ? 'Nothing found yet…' : 'No machines answered.'}</p>
      ) : (
        <>
          {addable.length > 1 ? (
            <div>
              <button type="button" className="nk-ns-link" onClick={() => setChosen(chosen.size === addable.length ? new Set() : new Set(addable.map((d) => d.ipAddress)))}>
                {chosen.size === addable.length ? 'Select none' : `Select all ${addable.length} new machines`}
              </button>
            </div>
          ) : null}
          <div className="nk-ns-scroll">
            <table className="nk-ns-table" aria-label="Machines found">
              <thead>
                <tr>
                  <th scope="col">Add</th>
                  <th scope="col">Address</th>
                  <th scope="col">Name</th>
                  <th scope="col">MAC address</th>
                  <th scope="col">System</th>
                  <th scope="col">Open ports</th>
                  <th scope="col">In NKeeper</th>
                </tr>
              </thead>
              <tbody>
                {scan.devices.map((device) => {
                  const open = expanded.has(device.ipAddress)
                  const addedAs = added.get(device.ipAddress)
                  const match = matches.get(device.ipAddress)
                  return (
                    <Fragment key={device.ipAddress}>
                      <tr>
                        <td>
                          {!match && !addedAs ? (
                            <input
                              type="checkbox"
                              aria-label={`Add ${device.ipAddress}`}
                              checked={chosen.has(device.ipAddress)}
                              onChange={(event) => setChosen((current) => toggle(current, device.ipAddress, event.target.checked))}
                            />
                          ) : null}
                        </td>
                        <td>
                          <button
                            type="button"
                            className="nk-ns-link"
                            aria-expanded={open}
                            title={open ? 'Hide details' : 'Show everything found'}
                            onClick={() => setExpanded((current) => toggle(current, device.ipAddress, !open))}
                          >
                            <span aria-hidden="true">{open ? '▾' : '▸'}</span> {device.ipAddress}
                          </button>
                        </td>
                        <td>{device.hostname ?? <span className="nk-ns-muted">unknown</span>}</td>
                        <td>
                          {device.macAddress ? <span className="nk-ns-mono">{device.macAddress}</span> : <span className="nk-ns-muted">unknown</span>}
                          {device.vendor ? <div className="nk-ns-muted">{device.vendor}</div> : null}
                        </td>
                        <td>{device.operatingSystem ?? (device.isVirtual ? 'Virtual machine' : <span className="nk-ns-muted">unknown</span>)}</td>
                        <td>
                          {device.ports.length === 0 ? <span className="nk-ns-muted">none</span> : null}
                          {device.ports.map((port) => (
                            <span key={port.port} className="nk-ns-port" data-known={serviceTypeFor(port.port, types) ? '' : undefined} title={port.name ?? undefined}>
                              {port.port}
                            </span>
                          ))}
                        </td>
                        <td>
                          {addedAs ? (
                            <>Added as {addedAs}</>
                          ) : match ? (
                            <>
                              {match.host.name}
                              <div className="nk-ns-muted">by {MATCHED_BY[match.matchedBy]}</div>
                            </>
                          ) : (
                            <span className="nk-ns-muted">New</span>
                          )}
                        </td>
                      </tr>
                      {open ? (
                        <tr>
                          <td />
                          <td colSpan={6}>
                            <DeviceDetails device={device} types={types} isNew={!match} />
                          </td>
                        </tr>
                      ) : null}
                    </Fragment>
                  )
                })}
              </tbody>
            </table>
          </div>
        </>
      )}

      {scan.notes.length > 0 ? (
        <section>
          <h3 className="nk-ns-title">Notes</h3>
          <ul>
            {scan.notes.map((note) => (
              <li key={note}>{note}</li>
            ))}
          </ul>
        </section>
      ) : null}

      <div className="nk-ns-bar nk-ns-end">
        {running ? (
          <button type="button" className="nk-ns-button" onClick={() => void cancel()} disabled={busy}>
            Stop scan
          </button>
        ) : (
          <button
            type="button"
            className="nk-ns-button"
            disabled={busy}
            onClick={() => {
              setScan(null)
              setAcknowledged(false)
              setError(null)
            }}
          >
            New scan
          </button>
        )}
        <button type="button" className="nk-ns-button" onClick={close}>
          Close
        </button>
        {addable.length > 0 ? (
          <button type="button" className="nk-ns-button" data-primary="" onClick={() => void addChosen()} disabled={busy || chosen.size === 0}>
            {chosen.size === 0 ? 'Add hosts' : `Add ${chosen.size} host${chosen.size === 1 ? '' : 's'}`}
          </button>
        ) : null}
      </div>
    </div>
  )
}

/** Everything the scan learned about one machine. */
function DeviceDetails({ device, types, isNew }: { device: DiscoveredDevice; types: ServiceTypeInfo[]; isNew: boolean }) {
  const services = [...new Set(device.ports.map((p) => serviceTypeFor(p.port, types)?.label).filter((l): l is string => !!l))]
  return (
    <dl className="nk-ns-details">
      <dt>Found by</dt>
      <dd>
        {device.respondedTo.map((how) => FOUND_BY[how] ?? how).join(', ')}
        {device.ttl ? ` (ping TTL ${device.ttl})` : ''}
      </dd>
      {device.names.length > 0 ? (
        <>
          <dt>Names</dt>
          <dd>{device.names.join(', ')}</dd>
        </>
      ) : null}
      {device.ports.length > 0 ? (
        <>
          <dt>Ports</dt>
          <dd>{device.ports.map((p) => (p.name ? `${p.port} ${p.name}` : String(p.port))).join(', ')}</dd>
        </>
      ) : null}
      {device.details.length > 0 ? (
        <>
          <dt>Said</dt>
          <dd>
            <ul>
              {device.details.map((detail) => (
                <li key={detail}>{detail}</li>
              ))}
            </ul>
          </dd>
        </>
      ) : null}
      {isNew ? (
        <>
          <dt>Would add</dt>
          <dd>
            {device.hostname ? device.hostname.split('.')[0] : device.ipAddress}
            {services.length > 0 ? ` with ${services.join(', ')}` : ''}
            {device.operatingSystem ? ` and the tag os: ${device.operatingSystem}` : ''}
          </dd>
        </>
      ) : null}
    </dl>
  )
}

function toggle(set: ReadonlySet<string>, key: string, on: boolean): ReadonlySet<string> {
  const next = new Set(set)
  if (on) {
    next.add(key)
  } else {
    next.delete(key)
  }
  return next
}

/** A finished scan is offered again for an hour, so closing the dialog doesn't lose it. */
function isRecent(scan: NetworkScan): boolean {
  return scan.finishedAtUtc !== null && Date.now() - Date.parse(scan.finishedAtUtc) < 60 * 60 * 1000
}
