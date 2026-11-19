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
