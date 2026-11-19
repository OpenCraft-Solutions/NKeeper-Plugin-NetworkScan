import { adoptStyles, definePlugin } from '@nkeeper/sdk'
import { NetworkScanDialog } from './NetworkScanDialog'
import { scanStyles } from './styles'

const plugin = definePlugin({
  id: 'network-scan',
  activate() {
    // Constructable stylesheets: the shell's policy blocks <style> elements (docs/architecture.md §7.14).
    adoptStyles(scanStyles)
  },
  // SDK 1.14 (§7.41): the network scan, for administrators, above the host tree. The external scan of one host is a
  // host-wide scanner, which the shell's Scan dialog offers from the backend's contribution alone.
  inventoryTools: { 'network-scan': { label: 'Scan network', title: 'Find the machines on a subnet (a loud network scan)', view: NetworkScanDialog } },
})

export default plugin
