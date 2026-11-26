import {useEffect, useRef, useState} from 'react'
import {useNavigate} from 'react-router'
import {Icon} from '../../components/Icon'
import {Kbd} from '../../components/ui'
import {hasDesktopHost} from '../../host/client'
import {formatRelativeTime, plural} from '../../lib/format'
import {useSession} from '../session'
import {useShell} from '../shell'
import {useWorkspace} from '../workspace'
import {Selector} from './Selector'

/**
 * The bar across the top: which workspace and branch you're in, search, and the one button you most likely need next
 * (Sync when the server is ahead, Submit when you have changes).
 */
export function ContextBar() {
    const {current, status} = useWorkspace()
    const {openPalette} = useShell()

    return (
        <header className="context-bar">
            <div className="context-brand" aria-hidden="true">
                <span>V</span>
            </div>
            {hasDesktopHost() && <WorkspaceSwitcher/>}
            {current && <BranchPicker/>}
            <button type="button" className="palette-trigger" onClick={openPalette}>
                <Icon name="search" size={14}/>
                <span>Search files, branches and commands</span>
                <Kbd>Ctrl K</Kbd>
            </button>
            <div className="context-spacer"/>
            {current && status?.serverProblem && (
                <span className="offline-pill" title={status.serverProblem}>
                    <Icon name="cloud" size={14}/>
                    Offline
                </span>
            )}
            {current && <PrimaryAction/>}
        </header>
    )
}

function PrimaryAction() {
    const {status, operation, sync} = useWorkspace()
    const {focusSubmit} = useShell()
    const navigate = useNavigate()

    if (operation) {
        return (
            <button type="button" className="button primary busy" disabled>
                <Icon name="refresh" size={15}/>
                {operation.title}…
            </button>
        )
    }

    const fresh = status?.newRevisions ?? 0
    const changes = status?.changes.length ?? 0

    // Files changed both here and on the server hold the sync back until they're settled, so point at them instead.
    const conflicts = status?.changes.filter(c => c.serverChanged).length ?? 0
    if (conflicts > 0) {
        return (
            <button type="button" className="button attention" onClick={() => navigate('/changes')}
                    title="These files changed both here and on the server, so they can't be submitted or synced as they are. Revert yours to take the server's version (copy your file somewhere first if you need it).">
                <Icon name="alert" size={15}/>
                {conflicts === 1 ? '1 file needs attention' : `${conflicts.toLocaleString()} files need attention`}
            </button>
        )
    }

    if (fresh > 0) {
        return (
            <button type="button" className="button primary" onClick={() => void sync()} title="Get the latest revisions from the server. Your changes are kept.">
                <Icon name="download" size={15}/>
                Sync
                <span className="button-badge">{fresh >= 100 ? '99+' : fresh} new</span>
            </button>
        )
    }

    if (changes > 0) {
        return (
            <button type="button" className="button primary" onClick={() => {
                navigate('/changes')
                setTimeout(focusSubmit, 0)
            }}>
                <Icon name="upload" size={15}/>
                Submit
                <span className="button-badge">{changes.toLocaleString()}</span>
            </button>
        )
    }

    return (
        <button type="button" className="button" onClick={() => void sync()} disabled={!status} title="Check the server for new revisions">
            <Icon name="check" size={15}/>
            Up to date
        </button>
    )
}

function WorkspaceSwitcher() {
    const {current, recent, open, browse, forget, fail} = useWorkspace()
    const {openDialog} = useShell()
    const [menu, setMenu] = useState(false)
    const root = useRef<HTMLDivElement>(null)

    useEffect(() => {
        if (!menu) return
        const close = (event: MouseEvent | KeyboardEvent) => {
            if (event instanceof KeyboardEvent ? event.key === 'Escape' : !root.current?.contains(event.target as Node)) setMenu(false)
        }
        document.addEventListener('mousedown', close)
        document.addEventListener('keydown', close)
        return () => {
            document.removeEventListener('mousedown', close)
            document.removeEventListener('keydown', close)
        }
    }, [menu])

    const run = (action: () => Promise<void>) => {
        setMenu(false)
        action().catch((error: unknown) => fail(error instanceof Error ? error.message : String(error)))
    }

    return (
        <div className="selector-wrap" ref={root}>
            <button type="button" className="selector" aria-expanded={menu} onClick={() => setMenu(m => !m)}>
                <span>
                    <small>Workspace</small>
                    <b>
                        <Icon name="folder" size={14}/>
                        {current ? current.project : 'None open'}
                    </b>
                </span>
                <Icon name="chevron" size={14}/>
            </button>
            {menu && (
                <div className="selector-menu workspace-menu">
                    {recent.length > 0 && <div className="menu-label">Recent workspaces</div>}
                    {recent.map(item => {
                        const isCurrent = item.root === current?.root
                        return (
                            <div key={item.root} className={`workspace-item ${isCurrent ? 'selected' : ''} ${item.exists ? '' : 'missing'}`}>
                                <button type="button" disabled={!item.exists} onClick={() => run(() => open(item.root))} title={item.root}>
                                    <Icon name="folder" size={14}/>
                                    <span>
                                        <b>{item.project ?? item.name}</b>
                                        <small>{item.exists ? `${item.branch ?? 'main'} · ${item.root}` : `Missing · ${item.root}`}</small>
                                    </span>
                                    {isCurrent ? <Icon name="check" size={14}/> : <em>{formatRelativeTime(item.lastOpened)}</em>}
                                </button>
                                {!isCurrent && (
                                    <button type="button" className="icon-button" aria-label={`Remove ${item.name} from the list`} title="Remove from the list (files stay)"
                                            onClick={() => run(() => forget(item.root))}>
                                        <Icon name="close" size={13}/>
                                    </button>
                                )}
                            </div>
                        )
                    })}
                    <hr/>
                    <button type="button" onClick={() => run(browse)}>
                        <Icon name="folder" size={14}/>
                        Open a workspace folder…
                    </button>
                    <button type="button" onClick={() => {
                        setMenu(false)
                        openDialog('clone')
                    }}>
                        <Icon name="download" size={14}/>
                        Clone a project…
                    </button>
                    <button type="button" onClick={() => {
                        setMenu(false)
                        openDialog('new')
                    }}>
                        <Icon name="plus" size={14}/>
                        New project from a folder…
                    </button>
                </div>
            )}
        </div>
    )
}

function BranchPicker() {
    const {current, status, checkout, fail} = useWorkspace()
    const {branches} = useSession()
    const items = branches.map(b => ({id: b.id, label: b.name}))
    const value = current ? {id: current.branchId, label: current.branch} : undefined

    return (
        <Selector
            type="Branch"
            icon="branch"
            items={items}
            value={value}
            placeholder={current?.branch ?? 'main'}
            onSelect={id => {
                if (id === current?.branchId) return
                const changes = status?.changes.length ?? 0
                if (changes > 0) {
                    fail(`${plural(changes, 'file')} changed here. Submit or revert them before switching branches.`)
                    return
                }
                void checkout(id)
            }}
        />
    )
}
