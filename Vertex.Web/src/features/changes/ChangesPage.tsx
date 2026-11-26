import {type KeyboardEvent, type MouseEvent, useCallback, useEffect, useMemo, useState} from 'react'
import {useSearchParams} from 'react-router'
import {type Change, useWorkspace} from '../../app/workspace'
import {EmptyState} from '../../components/EmptyState'
import {Icon} from '../../components/Icon'
import {SplitPane} from '../../components/SplitPane'
import {Banner, ContextMenu, type MenuItem, PaneHeader} from '../../components/ui'
import {VirtualList} from '../../components/VirtualList'
import {hasDesktopHost} from '../../host/client'
import {fileName, folderOf, plural} from '../../lib/format'
import {buildRows, type ChangeRow, folderStats, isEligible} from './changeRows'
import {ChangeBadge, FileDetail, SummaryDetail} from './FileDetail'
import {RevertDialog} from './RevertDialog'
import {SubmitBox} from './SubmitBox'

const ROW_HEIGHT = 30
const MODE_KEY = 'vertex.changes.mode'

type Selection = {type: 'file' | 'folder'; path: string} | undefined
type Menu = {x: number; y: number; items: MenuItem[]}

function rememberedMode(): 'tree' | 'flat' {
    try {
        return localStorage.getItem(MODE_KEY) === 'flat' ? 'flat' : 'tree'
    } catch {
        return 'tree'
    }
}

/**
 * Your changes and the submit box, like GitHub Desktop: every change starts checked, uncheck what shouldn't go in,
 * describe it, submit. Files are locked as you save them, so the list also shows who else holds what.
 */
export function ChangesPage() {
    const {status, statusError, current, reveal, operation} = useWorkspace()
    const [params, setParams] = useSearchParams()
    const [mode, setMode] = useState(rememberedMode)
    const [filter, setFilter] = useState('')
    const [excluded, setExcluded] = useState<ReadonlySet<string>>(() => new Set())
    const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(() => new Set())
    const [selection, setSelection] = useState<Selection>()
    const [menu, setMenu] = useState<Menu>()
    const [revert, setRevert] = useState<string[]>()

    // A file picked in the command palette: select it once, then take it out of the address.
    const pick = params.get('select')
    const [handledPick, setHandledPick] = useState<string | null>(null)
    if (pick !== handledPick) {
        setHandledPick(pick)
        if (pick) {
            setSelection({type: 'file', path: pick})
            setFilter('')
            setCollapsed(previous => new Set([...previous].filter(folder => !pick.startsWith(`${folder}/`))))
        }
    }
    useEffect(() => {
        if (pick) setParams({}, {replace: true})
    }, [pick, setParams])

    const changes = useMemo(() => {
        const list = status?.changes ?? []
        const needle = filter.trim().toLocaleLowerCase()
        const matching = needle ? list.filter(c => c.path.toLocaleLowerCase().includes(needle) || (c.previousPath ?? '').toLocaleLowerCase().includes(needle)) : list.slice()
        return matching.sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0))
    }, [status, filter])

    const included = useCallback((change: Change) => isEligible(change) && !excluded.has(change.path), [excluded])
    const stats = useMemo(() => folderStats(changes, included), [changes, included])
    const rows = useMemo(() => buildRows(changes, filter ? 'flat' : mode, collapsed, stats), [changes, mode, collapsed, stats, filter])

    const all = useMemo(() => status?.changes ?? [], [status])
    const toSubmit = useMemo(() => all.filter(included), [all, included])
    const eligibleCount = useMemo(() => all.filter(isEligible).length, [all])

    const setIncluded = (paths: string[], on: boolean) => {
        setExcluded(previous => {
            const next = new Set(previous)
            for (const path of paths) {
                if (on) next.delete(path)
                else next.add(path)
            }
            return next
        })
    }

    const inFolder = (folder: string) => all.filter(c => c.path.startsWith(`${folder}/`))
    const toggleFolder = (folder: string) => setCollapsed(previous => {
        const next = new Set(previous)
        if (next.has(folder)) next.delete(folder)
        else next.add(folder)
        return next
    })

    const activeIndex = rows.findIndex(row => (row.type === 'file' && selection?.type === 'file' && row.change.path === selection.path) ||
        (row.type === 'folder' && selection?.type === 'folder' && row.path === selection.path))

    const selectRow = (row: ChangeRow | undefined) => {
        if (!row || row.type === 'group') return
        setSelection(row.type === 'file' ? {type: 'file', path: row.change.path} : {type: 'folder', path: row.path})
    }

    const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
        const step = (delta: number) => {
            let index = activeIndex
            do index += delta
            while (index >= 0 && index < rows.length && rows[index].type === 'group')
            if (index >= 0 && index < rows.length) selectRow(rows[index])
        }

        const row = rows[activeIndex]
        if (event.key === 'ArrowDown') {
            event.preventDefault()
            step(1)
        } else if (event.key === 'ArrowUp') {
            event.preventDefault()
            step(-1)
        } else if (event.key === ' ' && row) {
            event.preventDefault()
            if (row.type === 'file' && isEligible(row.change)) setIncluded([row.change.path], !included(row.change))
            if (row.type === 'folder') setIncluded(inFolder(row.path).filter(isEligible).map(c => c.path), row.included < row.eligible)
        } else if ((event.key === 'ArrowLeft' || event.key === 'ArrowRight' || event.key === 'Enter') && row?.type === 'folder') {
            const isCollapsed = collapsed.has(row.path)
            if (event.key === 'Enter' || (event.key === 'ArrowLeft') !== isCollapsed) toggleFolder(row.path)
        } else if (event.key === 'Delete' && row?.type === 'file') {
            setRevert([row.change.path])
        }
    }

    const openMenu = (event: MouseEvent, row: ChangeRow) => {
        event.preventDefault()
        selectRow(row)
        if (row.type === 'file') {
            const path = row.change.path
            setMenu({
                x: event.clientX, y: event.clientY, items: [
                    isEligible(row.change)
                        ? {label: included(row.change) ? 'Leave out of the submit' : 'Include in the submit', icon: 'check', onClick: () => setIncluded([path], !included(row.change))}
                        : {label: `Locked by ${row.change.lockedBy?.username}`, icon: 'lock', onClick: () => undefined, disabled: true},
                    {label: 'Show in file manager', icon: 'external', onClick: () => void reveal(row.change.kind === 'Deleted' ? folderOf(path) : path)},
                    {label: 'Copy path', icon: 'copy', onClick: () => void navigator.clipboard?.writeText(path)},
                    'separator',
                    {label: 'Revert…', icon: 'undo', danger: true, onClick: () => setRevert([path]), disabled: operation !== undefined},
                ],
            })
        } else if (row.type === 'folder') {
            const folder = row.path
            setMenu({
                x: event.clientX, y: event.clientY, items: [
                    {label: 'Include everything in it', icon: 'check', onClick: () => setIncluded(inFolder(folder).filter(isEligible).map(c => c.path), true)},
                    {label: 'Leave it all out', icon: 'close', onClick: () => setIncluded(inFolder(folder).map(c => c.path), false)},
                    {label: 'Show in file manager', icon: 'external', onClick: () => void reveal(folder)},
                    'separator',
                    {label: `Revert ${plural(row.total, 'change')}…`, icon: 'undo', danger: true, onClick: () => setRevert([folder]), disabled: operation !== undefined},
                ],
            })
        }
    }

    if (!hasDesktopHost()) {
        return <EmptyState icon="code" title="Your changes are in the desktop app">Open Vertex Desktop to see, submit and sync the files in your workspace.</EmptyState>
    }

    const selectedChange = selection?.type === 'file' ? all.find(c => c.path === selection.path) : undefined
    const selectedFolder = selection?.type === 'folder' ? selection.path : undefined
    const allOn = eligibleCount > 0 && toSubmit.length === eligibleCount

    const list = (
        <div className="changes-pane">
            <PaneHeader title="Changes" count={status ? all.length : undefined}>
                <button type="button" className="icon-button" title={mode === 'tree' ? 'Show as a flat list' : 'Show as folders'} aria-label="Toggle tree view"
                        onClick={() => {
                            const next = mode === 'tree' ? 'flat' : 'tree'
                            setMode(next)
                            try {
                                localStorage.setItem(MODE_KEY, next)
                            } catch {
                                // Only the preference is lost.
                            }
                        }}>
                    <Icon name={mode === 'tree' ? 'list' : 'tree'} size={15}/>
                </button>
            </PaneHeader>

            {statusError && <Banner tone="error">{statusError.message}</Banner>}
            {status?.serverProblem && <Banner tone="warning" icon="cloud">{status.serverProblem} Your changes are listed; locks and new revisions will update once the server answers.</Banner>}
            {status && status.unreadable.length > 0 && (
                <Banner tone="warning">
                    {plural(status.unreadable.length, 'file')} can't be read (e.g. {status.unreadable[0]}) and won't be submitted. Close the program using them.
                </Banner>
            )}

            {all.length > 0 && (
                <div className="changes-toolbar">
                    <label className="check" title={allOn ? 'Leave everything out' : 'Include everything'}>
                        <input
                            type="checkbox"
                            checked={allOn}
                            ref={input => {
                                if (input) input.indeterminate = toSubmit.length > 0 && !allOn
                            }}
                            onChange={() => setExcluded(allOn ? new Set(all.map(c => c.path)) : new Set())}
                        />
                    </label>
                    <label className="filter">
                        <Icon name="search" size={13}/>
                        <input value={filter} onChange={e => setFilter(e.target.value)} placeholder="Filter changed files"/>
                        {filter && (
                            <button type="button" className="icon-button" aria-label="Clear filter" onClick={() => setFilter('')}>
                                <Icon name="close" size={12}/>
                            </button>
                        )}
                    </label>
                </div>
            )}

            <div className="changes-list">
                {!status ? (
                    statusError ? null : <EmptyState icon="clock" title="Scanning your workspace…"/>
                ) : all.length === 0 ? (
                    <EmptyState icon="check" title="No changes">
                        {status.syncedRevision > 0 ? `Your files match #${status.syncedRevision}.` : 'Add or edit files in the workspace folder and they show up here.'}{' '}
                        Edited files are locked for you as you save them.
                    </EmptyState>
                ) : rows.length === 0 ? (
                    <EmptyState icon="search" title="No matching files">Try another filter.</EmptyState>
                ) : (
                    <VirtualList
                        items={rows}
                        rowHeight={ROW_HEIGHT}
                        label="Changed files"
                        activeIndex={activeIndex}
                        onKeyDown={onKeyDown}
                        render={row => (
                            <Row
                                row={row}
                                active={(row.type === 'file' && selection?.path === row.change.path && selection.type === 'file') ||
                                    (row.type === 'folder' && selection?.type === 'folder' && selection.path === row.path)}
                                collapsed={row.type === 'folder' && collapsed.has(row.path)}
                                included={row.type === 'file' ? included(row.change) : false}
                                onSelect={() => selectRow(row)}
                                onToggleFolder={() => row.type === 'folder' && toggleFolder(row.path)}
                                onCheck={on => {
                                    if (row.type === 'file') setIncluded([row.change.path], on)
                                    if (row.type === 'folder') setIncluded(inFolder(row.path).filter(on ? isEligible : () => true).map(c => c.path), on)
                                }}
                                onMenu={event => openMenu(event, row)}
                            />
                        )}
                    />
                )}
            </div>

            {current && <SubmitBox workspaceRoot={current.root} paths={toSubmit.map(c => c.path)} total={all.length}/>}
        </div>
    )

    return (
        <>
            <SplitPane
                id="changes"
                list={list}
                detail={selectedChange
                    ? <FileDetail change={selectedChange} onRevert={() => setRevert([selectedChange.path])}/>
                    : <SummaryDetail folder={selectedFolder} changes={selectedFolder ? inFolder(selectedFolder) : all} onRevertFolder={selectedFolder ? () => setRevert([selectedFolder]) : undefined}/>}
            />
            {menu && <ContextMenu x={menu.x} y={menu.y} items={menu.items} onClose={() => setMenu(undefined)}/>}
            {revert && <RevertDialog paths={revert} changes={all} onClose={() => setRevert(undefined)}/>}
        </>
    )
}

type RowProps = {
    row: ChangeRow
    active: boolean
    collapsed: boolean
    included: boolean
    onSelect: () => void
    onToggleFolder: () => void
    onCheck: (on: boolean) => void
    onMenu: (event: MouseEvent) => void
}

function Row({row, active, collapsed, included, onSelect, onToggleFolder, onCheck, onMenu}: RowProps) {
    if (row.type === 'group') {
        return (
            <div className={`change-group ${row.tone}`}>
                {row.tone === 'attention' && <Icon name="alert" size={13}/>}
                {row.label}
                <span className="count">{row.count.toLocaleString()}</span>
            </div>
        )
    }

    const indent = {paddingLeft: 10 + row.depth * 16}
    if (row.type === 'folder') {
        const all = row.eligible > 0 && row.included === row.eligible
        return (
            <div className={`change-row folder ${active ? 'active' : ''}`} style={indent} onClick={onSelect} onDoubleClick={onToggleFolder} onContextMenu={onMenu} role="option" aria-selected={active}>
                <input
                    type="checkbox"
                    checked={all}
                    disabled={row.eligible === 0}
                    ref={input => {
                        if (input) input.indeterminate = row.included > 0 && !all
                    }}
                    onClick={event => event.stopPropagation()}
                    onChange={() => onCheck(!all)}
                    aria-label={`Include ${row.name}`}
                />
                <button type="button" className={`twisty ${collapsed ? 'collapsed' : ''}`} aria-label={collapsed ? 'Expand' : 'Collapse'} onClick={event => {
                    event.stopPropagation()
                    onToggleFolder()
                }}>
                    <Icon name="chevron" size={12}/>
                </button>
                <Icon name="folder" size={14}/>
                <span className="change-name">{row.name}</span>
                <span className="count">{row.total.toLocaleString()}</span>
            </div>
        )
    }

    const change = row.change
    const locked = change.lockedBy
    const folder = row.showFolder ? folderOf(change.path) : ''
    const title = locked
        ? `${change.path}\nLocked by ${locked.sameUser ? `you in another workspace on ${locked.host}` : `${locked.username} on ${locked.host}`}. It can't be submitted until they release it.`
        : change.serverChanged
            ? `${change.path}\nAlso changed on the server since your last sync.`
            : change.previousPath ? `${change.previousPath} → ${change.path}` : change.path

    return (
        <div className={`change-row file ${active ? 'active' : ''} ${locked ? 'locked' : ''} ${included || locked ? '' : 'excluded'}`} style={indent} onClick={onSelect} onContextMenu={onMenu}
             role="option" aria-selected={active} title={title}>
            <input type="checkbox" checked={included} disabled={locked !== null} onClick={event => event.stopPropagation()} onChange={() => onCheck(!included)}
                   aria-label={`Include ${fileName(change.path)}`}/>
            <ChangeBadge kind={change.kind}/>
            <span className="change-name">
                {fileName(change.path)}
                {folder && <small>{folder}</small>}
            </span>
            {change.serverChanged && <span className="flag server" title="Also changed on the server"><Icon name="cloud" size={13}/></span>}
            {locked && <span className="flag locked" title={`Locked by ${locked.username}`}><Icon name="lock" size={13}/>{locked.sameUser ? 'you' : locked.username}</span>}
        </div>
    )
}
