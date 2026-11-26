import {type Change, useWorkspace} from '../../app/workspace'
import {Avatar} from '../../components/Avatar'
import {Icon} from '../../components/Icon'
import {Banner} from '../../components/ui'
import {fileName, folderOf, formatBytes, plural} from '../../lib/format'

const kindLabels: Record<Change['kind'], {letter: string; label: string}> = {
    Added: {letter: 'A', label: 'Added'},
    Modified: {letter: 'M', label: 'Modified'},
    Deleted: {letter: 'D', label: 'Deleted'},
    Moved: {letter: 'R', label: 'Moved'},
}

/** The A / M / D / R square in front of a changed file. */
export function ChangeBadge({kind}: {kind: Change['kind']}) {
    const {letter, label} = kindLabels[kind]
    return <span className={`change-badge ${kind.toLowerCase()}`} title={label}>{letter}</span>
}

/** One changed file: what changed, who holds its lock, and what you can do about it. */
export function FileDetail({change, onRevert}: {change: Change; onRevert: () => void}) {
    const {status, reveal, operation} = useWorkspace()
    const locked = change.lockedBy
    const folder = folderOf(change.path)
    const online = !status?.serverProblem

    return (
        <article className="detail">
            <header className="detail-header">
                <ChangeBadge kind={change.kind}/>
                <div>
                    <h2>{fileName(change.path)}</h2>
                    <p className="path">{folder || 'Workspace root'}</p>
                </div>
            </header>

            {locked && (
                <Banner tone="error" icon="lock">
                    {locked.sameUser
                        ? <>You hold this file's lock in another workspace on <b>{locked.host}</b>. Submit it from there, or release that lock on the Locks page if the workspace is gone.</>
                        : <><b>{locked.username}</b> on {locked.host} has this file locked, so your change can't be submitted. Wait for them to submit it, or revert your change.</>}
                </Banner>
            )}
            {change.serverChanged && (
                <Banner tone="warning" icon="cloud" action={{label: 'Revert…', onClick: onRevert}}>
                    Someone changed this file on the server since your last sync. Syncing leaves your version alone; revert it to take theirs.
                </Banner>
            )}

            <dl className="detail-facts">
                <dt>Change</dt>
                <dd>{kindLabels[change.kind].label}{change.previousPath ? <> from <code>{change.previousPath}</code></> : null}</dd>
                <dt>Size</dt>
                <dd>{change.kind === 'Deleted' ? '—' : formatBytes(change.size)}</dd>
                <dt>Lock</dt>
                <dd>
                    {locked ? (
                        <span className="holder"><Avatar person={{name: locked.username}}/>{locked.username} on {locked.host}</span>
                    ) : online ? (
                        <span className="holder mine"><Icon name="lock" size={13}/>Locked by you until you submit or revert it</span>
                    ) : (
                        <span className="muted">Locks update once the server answers</span>
                    )}
                </dd>
                <dt>Path</dt>
                <dd><code>{change.path}</code></dd>
            </dl>

            <div className="detail-actions">
                <button type="button" className="button" onClick={() => void reveal(change.kind === 'Deleted' ? folder : change.path)}>
                    <Icon name="external" size={14}/>
                    Show in file manager
                </button>
                <button type="button" className="button" onClick={() => void navigator.clipboard?.writeText(change.path)}>
                    <Icon name="copy" size={14}/>
                    Copy path
                </button>
                <button type="button" className="button danger" onClick={onRevert} disabled={operation !== undefined}>
                    <Icon name="undo" size={14}/>
                    Revert…
                </button>
            </div>
        </article>
    )
}

/** Nothing (or a folder) selected: a summary of the changes in view. */
export function SummaryDetail({folder, changes, onRevertFolder}: {folder: string | undefined; changes: readonly Change[]; onRevertFolder?: () => void}) {
    const {reveal, operation} = useWorkspace()
    const count = (kind: Change['kind']) => changes.filter(c => c.kind === kind).length
    const size = changes.reduce((total, c) => total + (c.kind === 'Deleted' ? 0 : c.size), 0)
    const locked = changes.filter(c => c.lockedBy).length

    return (
        <article className="detail summary-detail">
            <header className="detail-header">
                <span className="detail-icon"><Icon name={folder ? 'folder' : 'code'} size={18}/></span>
                <div>
                    <h2>{folder ? fileName(folder) : 'Your changes'}</h2>
                    <p className="path">{folder ?? 'Select a file to see its details.'}</p>
                </div>
            </header>
            {changes.length > 0 ? (
                <>
                    <div className="summary-tiles">
                        <Tile label="Added" value={count('Added')} kind="added"/>
                        <Tile label="Modified" value={count('Modified')} kind="modified"/>
                        <Tile label="Deleted" value={count('Deleted')} kind="deleted"/>
                        <Tile label="Moved" value={count('Moved')} kind="moved"/>
                    </div>
                    <p className="muted">{plural(changes.length, 'file')}, {formatBytes(size)} of content. Only content the server doesn't have yet is uploaded.</p>
                    {locked > 0 && <Banner tone="error" icon="lock">{plural(locked, 'file')} are locked by someone else and can't be submitted.</Banner>}
                    {folder && (
                        <div className="detail-actions">
                            <button type="button" className="button" onClick={() => void reveal(folder)}>
                                <Icon name="external" size={14}/>
                                Show in file manager
                            </button>
                            {onRevertFolder && (
                                <button type="button" className="button danger" onClick={onRevertFolder} disabled={operation !== undefined}>
                                    <Icon name="undo" size={14}/>
                                    Revert folder…
                                </button>
                            )}
                        </div>
                    )}
                </>
            ) : (
                <div className="tips">
                    <Tip icon="lock" title="Locked as you save">Edit any file and it's locked for you, so nobody else can change it at the same time.</Tip>
                    <Tip icon="upload" title="Submit when ready">Check the files that belong together, describe the change, and submit them as one revision.</Tip>
                    <Tip icon="download" title="Sync to stay current">When others submit, the Sync button shows how many revisions are new. Your changes are never overwritten.</Tip>
                </div>
            )}
        </article>
    )
}

function Tile({label, value, kind}: {label: string; value: number; kind: string}) {
    return (
        <div className={`tile ${kind}`}>
            <b>{value.toLocaleString()}</b>
            <small>{label}</small>
        </div>
    )
}

function Tip({icon, title, children}: {icon: 'lock' | 'upload' | 'download'; title: string; children: string}) {
    return (
        <div className="tip">
            <Icon name={icon} size={16}/>
            <div>
                <b>{title}</b>
                <p>{children}</p>
            </div>
        </div>
    )
}
