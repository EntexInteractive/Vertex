import {useMemo, useState} from 'react'
import {useNavigate} from 'react-router'
import {Icon, type IconName} from '../../components/Icon'
import {Kbd} from '../../components/ui'
import {fileName, folderOf, plural} from '../../lib/format'
import {useSession} from '../session'
import {useShell} from '../shell'
import {useWorkspace} from '../workspace'

type Command = {id: string; label: string; hint?: string; icon: IconName; group: string; run: () => void; disabled?: boolean}

const MAX_FILES = 50

/**
 * Ctrl+K: every action in the app, the branches, recent workspaces and the changed files, searchable by name. New
 * features add their commands here first, so the rail stays small.
 */
export function CommandPalette({onClose}: {onClose: () => void}) {
    const navigate = useNavigate()
    const workspace = useWorkspace()
    const {branches} = useSession()
    const {openDialog, focusSubmit} = useShell()
    const [query, setQuery] = useState('')
    const [active, setActive] = useState(0)

    const {current, status, operation, recent} = workspace
    const commands = useMemo<Command[]>(() => {
        const busy = operation !== undefined
        const list: Command[] = [
            {id: 'sync', group: 'Actions', label: 'Sync', hint: status?.newRevisions ? `${status.newRevisions} new` : undefined, icon: 'download', run: () => void workspace.sync(), disabled: !current || busy},
            {id: 'submit', group: 'Actions', label: 'Submit changes…', hint: status ? plural(status.changes.length, 'change') : undefined, icon: 'upload', run: () => {
                navigate('/changes')
                setTimeout(focusSubmit, 0)
            }, disabled: !current || busy},
            {id: 'refresh', group: 'Actions', label: 'Rescan the workspace', icon: 'refresh', run: workspace.refresh, disabled: !current},
            {id: 'reveal', group: 'Actions', label: 'Show the workspace in the file manager', icon: 'external', run: () => void workspace.reveal(''), disabled: !current},
            {id: 'open', group: 'Workspaces', label: 'Open a workspace folder…', icon: 'folder', run: () => void workspace.browse().catch((e: unknown) => workspace.fail(String(e instanceof Error ? e.message : e)))},
            {id: 'clone', group: 'Workspaces', label: 'Clone a project…', icon: 'download', run: () => openDialog('clone')},
            {id: 'new', group: 'Workspaces', label: 'New project from a folder…', icon: 'plus', run: () => openDialog('new')},
            {id: 'go-changes', group: 'Go to', label: 'Changes', icon: 'code', run: () => navigate('/changes')},
            {id: 'go-history', group: 'Go to', label: 'History', icon: 'history', run: () => navigate('/history')},
            {id: 'go-branches', group: 'Go to', label: 'Branches', icon: 'branch', run: () => navigate('/branches')},
            {id: 'go-locks', group: 'Go to', label: 'Locks', icon: 'lock', run: () => navigate('/locks')},
            {id: 'go-account', group: 'Go to', label: 'Account', icon: 'user', run: () => navigate('/account')},
            {id: 'go-tasks', group: 'Previews', label: 'Tasks (preview)', icon: 'tasks', run: () => navigate('/tasks')},
            {id: 'go-bugs', group: 'Previews', label: 'Bugs (preview)', icon: 'bug', run: () => navigate('/bugs')},
            {id: 'go-graph', group: 'Previews', label: 'Branch graph (preview)', icon: 'graph', run: () => navigate('/graph')},
        ]

        for (const branch of branches) {
            if (branch.id === current?.branchId) continue
            list.push({id: `branch-${branch.id}`, group: 'Switch branch', label: branch.name, icon: 'branch', run: () => void workspace.checkout(branch.id), disabled: !current || busy})
        }

        for (const item of recent) {
            if (item.root === current?.root || !item.exists) continue
            list.push({id: `ws-${item.root}`, group: 'Open workspace', label: item.project ?? item.name, hint: item.root, icon: 'folder', run: () => void workspace.open(item.root)})
        }

        return list
    }, [workspace, status, current, operation, recent, branches, navigate, openDialog, focusSubmit])

    const needle = query.trim().toLocaleLowerCase()
    const visible = useMemo(() => {
        const matching = commands.filter(c => !needle || c.label.toLocaleLowerCase().includes(needle) || c.group.toLocaleLowerCase().includes(needle))
        // Changed files only once there's something to search for; there can be thousands.
        if (needle.length >= 2 && status) {
            let added = 0
            for (const change of status.changes) {
                if (!change.path.toLocaleLowerCase().includes(needle)) continue
                matching.push({
                    id: `file-${change.path}`,
                    group: 'Changed files',
                    label: fileName(change.path),
                    hint: folderOf(change.path) || undefined,
                    icon: 'file',
                    run: () => navigate(`/changes?select=${encodeURIComponent(change.path)}`),
                })
                if (++added >= MAX_FILES) break
            }
        }
        return matching
    }, [commands, needle, status, navigate])

    const run = (command: Command | undefined) => {
        if (!command || command.disabled) return
        onClose()
        command.run()
    }

    let lastGroup = ''
    return (
        <div className="modal-backdrop palette-backdrop" onMouseDown={event => event.target === event.currentTarget && onClose()}>
            <div className="palette" role="dialog" aria-label="Command palette">
                <label className="palette-input">
                    <Icon name="search" size={16}/>
                    <input
                        autoFocus
                        value={query}
                        placeholder="Type a command, branch or file name"
                        onChange={event => {
                            setQuery(event.target.value)
                            setActive(0)
                        }}
                        onKeyDown={event => {
                            if (event.key === 'ArrowDown') {
                                event.preventDefault()
                                setActive(i => Math.min(visible.length - 1, i + 1))
                            } else if (event.key === 'ArrowUp') {
                                event.preventDefault()
                                setActive(i => Math.max(0, i - 1))
                            } else if (event.key === 'Enter') {
                                run(visible[active])
                            } else if (event.key === 'Escape') {
                                onClose()
                            }
                        }}
                    />
                    <Kbd>Esc</Kbd>
                </label>
                <div className="palette-list" role="listbox">
                    {visible.length === 0 && <p className="palette-empty">Nothing matches “{query}”.</p>}
                    {visible.map((command, index) => {
                        const header = command.group !== lastGroup ? command.group : null
                        lastGroup = command.group
                        return (
                            <div key={command.id}>
                                {header && <div className="menu-label">{header}</div>}
                                <button
                                    type="button"
                                    role="option"
                                    aria-selected={index === active}
                                    className={index === active ? 'active' : ''}
                                    disabled={command.disabled}
                                    onMouseEnter={() => setActive(index)}
                                    onClick={() => run(command)}
                                >
                                    <Icon name={command.icon} size={15}/>
                                    <span>{command.label}</span>
                                    {command.hint && <small>{command.hint}</small>}
                                </button>
                            </div>
                        )
                    })}
                </div>
            </div>
        </div>
    )
}
