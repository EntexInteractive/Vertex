import type {Change} from '../../app/workspace'

/** One line of the changes list. */
export type ChangeRow =
    | {type: 'group'; key: string; label: string; count: number; tone: 'attention' | 'normal'}
    | {type: 'folder'; key: string; path: string; name: string; depth: number; total: number; included: number; eligible: number}
    | {type: 'file'; key: string; change: Change; depth: number; showFolder: boolean}

export type FolderStats = {total: number; included: number; eligible: number}

/** A change someone else's lock or a newer server version stops from being submitted as is. */
export function needsAttention(change: Change): boolean {
    return change.lockedBy !== null || change.serverChanged
}

/** Changes locked by someone else can't be submitted, so they're never selectable. */
export function isEligible(change: Change): boolean {
    return change.lockedBy === null
}

function folders(path: string): string[] {
    const parts = path.split('/')
    const result: string[] = []
    for (let i = 1; i < parts.length; i++) result.push(parts.slice(0, i).join('/'))
    return result
}

/** Totals per folder, for the folder rows' counts and checkboxes. One pass over the changes. */
export function folderStats(changes: readonly Change[], included: (change: Change) => boolean): Map<string, FolderStats> {
    const stats = new Map<string, FolderStats>()
    for (const change of changes) {
        const eligible = isEligible(change)
        const on = eligible && included(change)
        for (const folder of folders(change.path)) {
            let entry = stats.get(folder)
            if (!entry) {
                entry = {total: 0, included: 0, eligible: 0}
                stats.set(folder, entry)
            }
            entry.total++
            if (eligible) entry.eligible++
            if (on) entry.included++
        }
    }
    return stats
}

/**
 * The rows to show: changes that need attention first (flat, with their folder), then the rest as a folder tree or a
 * flat list. Folders in `collapsed` hide what's inside them.
 */
export function buildRows(changes: readonly Change[], mode: 'tree' | 'flat', collapsed: ReadonlySet<string>, stats: Map<string, FolderStats>): ChangeRow[] {
    const attention = changes.filter(needsAttention)
    const rest = attention.length > 0 ? changes.filter(c => !needsAttention(c)) : changes
    const rows: ChangeRow[] = []

    if (attention.length > 0) {
        rows.push({type: 'group', key: 'g-attention', label: 'Needs attention', count: attention.length, tone: 'attention'})
        for (const change of attention) rows.push({type: 'file', key: `a-${change.path}`, change, depth: 0, showFolder: true})
        if (rest.length > 0) rows.push({type: 'group', key: 'g-changes', label: 'Changes', count: rest.length, tone: 'normal'})
    }

    if (mode === 'flat') {
        for (const change of rest) rows.push({type: 'file', key: change.path, change, depth: 0, showFolder: true})
        return rows
    }

    let open: string[] = []
    for (const change of rest) {
        const parents = folders(change.path)
        let common = 0
        while (common < open.length && common < parents.length && open[common] === parents[common]) common++
        open = parents

        // Under a collapsed folder, only the collapsed folder itself shows.
        let hiddenFrom = parents.findIndex(folder => collapsed.has(folder))
        if (hiddenFrom < 0) hiddenFrom = parents.length
        else hiddenFrom += 1

        for (let depth = common; depth < Math.min(parents.length, hiddenFrom); depth++) {
            const path = parents[depth]
            const stat = stats.get(path) ?? {total: 0, included: 0, eligible: 0}
            rows.push({type: 'folder', key: `f-${path}`, path, name: path.slice(path.lastIndexOf('/') + 1), depth, ...stat})
        }

        if (hiddenFrom === parents.length) rows.push({type: 'file', key: change.path, change, depth: parents.length, showFolder: false})
    }

    return rows
}
