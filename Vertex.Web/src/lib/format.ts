// Small formatting helpers shared by pages.

const minute = 60_000
const hour = 60 * minute
const day = 24 * hour

/** "just now", "12 min ago", "3h ago", "Yesterday", "Jun 10", or "Jun 10, 2024" for other years. */
export function formatRelativeTime(unixMs: number, now: number = Date.now()): string {
    const elapsed = now - unixMs
    if (elapsed < minute) return 'just now'
    if (elapsed < hour) return `${Math.floor(elapsed / minute)} min ago`
    if (elapsed < day) return `${Math.floor(elapsed / hour)}h ago`
    if (elapsed < 2 * day) return 'Yesterday'

    const date = new Date(unixMs)
    const sameYear = date.getFullYear() === new Date(now).getFullYear()
    return date.toLocaleDateString(undefined, sameYear ? {month: 'short', day: 'numeric'} : {month: 'short', day: 'numeric', year: 'numeric'})
}

/** The full local date and time, for tooltips. */
export function formatDateTime(unixMs: number): string {
    return new Date(unixMs).toLocaleString()
}

/** Revisions are always shown as #1234. */
export function formatRevision(number: number): string {
    return `#${number}`
}

/** "1 file", "12 files". */
export function plural(count: number, noun: string): string {
    return `${count.toLocaleString()} ${noun}${count === 1 ? '' : 's'}`
}

/** "512 B", "3.4 MB", "1.20 GB". */
export function formatBytes(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
    if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
    return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`
}

/** The last part of a path: "Content/Hero.uasset" -> "Hero.uasset". */
export function fileName(path: string): string {
    const slash = path.lastIndexOf('/')
    return slash >= 0 ? path.slice(slash + 1) : path
}

/** The folder part of a path, or "" at the root. */
export function folderOf(path: string): string {
    const slash = path.lastIndexOf('/')
    return slash >= 0 ? path.slice(0, slash) : ''
}
