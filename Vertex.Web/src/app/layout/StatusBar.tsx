import {useState} from 'react'
import {Icon} from '../../components/Icon'
import {ProgressBar} from '../../components/ui'
import {hasDesktopHost} from '../../host/client'
import {formatBytes, formatRevision} from '../../lib/format'
import {useSession} from '../session'
import {type Operation, useWorkspace} from '../workspace'
import {ActivityPanel} from './ActivityPanel'

/** The strip along the bottom: connection, synced revision, the running operation with Cancel, and activity. */
export function StatusBar() {
    const {session} = useSession()
    const {current, status, operation, cancel, unreadActivity, monitorProblem, scanning} = useWorkspace()
    const [activityOpen, setActivityOpen] = useState(false)

    const problem = monitorProblem ?? status?.serverProblem ?? undefined
    const connection = !hasDesktopHost() ? 'Browser' : !session?.loggedIn ? 'Not logged in' : problem ? 'Offline' : 'Connected'

    return (
        <footer className="status-bar">
            <span className={`connection ${problem || !session?.loggedIn ? 'down' : 'up'}`} title={problem ?? session?.server}>
                <i/>
                {connection}
                {session?.server && <small>{session.server.replace(/^https?:\/\//, '')}</small>}
            </span>
            {current && status && (
                <span title="The revision your files were last synced to">
                    <Icon name="commit" size={13}/>
                    {status.syncedRevision > 0 ? `${formatRevision(status.syncedRevision)} synced` : 'Not synced yet'}
                </span>
            )}
            {current && status && status.lockedByMe > 0 && (
                <span>
                    <Icon name="lock" size={13}/>
                    {status.lockedByMe.toLocaleString()} locked by you
                </span>
            )}
            {scanning && !operation && <span className="muted">Scanning…</span>}
            <div className="status-spacer"/>
            {operation && <OperationProgress operation={operation} onCancel={() => void cancel()}/>}
            {hasDesktopHost() && (
                <button type="button" className={`status-button ${unreadActivity > 0 ? 'unread' : ''}`} onClick={() => setActivityOpen(open => !open)} aria-expanded={activityOpen}>
                    <Icon name="bell" size={14}/>
                    Activity
                    {unreadActivity > 0 && <i>{unreadActivity > 99 ? '99+' : unreadActivity}</i>}
                </button>
            )}
            {activityOpen && <ActivityPanel onClose={() => setActivityOpen(false)}/>}
        </footer>
    )
}

function OperationProgress({operation, onCancel}: {operation: Operation; onCancel: () => void}) {
    const phase = operation.phases.find(p => p.started && p.done < p.total) ?? operation.phases.find(p => p.started)
    const label = phase ? phase.name : operation.step?.replace(/\.\.\.$/, '') ?? operation.title
    const detail = phase
        ? phase.unit === 'Bytes'
            ? `${formatBytes(phase.done)} of ${formatBytes(phase.total)}`
            : `${phase.done.toLocaleString()} of ${phase.total.toLocaleString()} files${phase.transferredBytes > 0 ? ` · ${formatBytes(phase.transferredBytes)}` : ''}`
        : undefined

    return (
        <span className="operation-progress">
            <b>{label}</b>
            <ProgressBar done={phase?.done ?? 0} total={phase?.total ?? 0}/>
            {detail && <small>{detail}</small>}
            <button type="button" className="link-button" onClick={onCancel}>Cancel</button>
        </span>
    )
}
