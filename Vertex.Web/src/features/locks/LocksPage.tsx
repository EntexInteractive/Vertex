import {useMemo, useState} from 'react'
import {useSession} from '../../app/session'
import {useWorkspace} from '../../app/workspace'
import {Avatar} from '../../components/Avatar'
import {EmptyState} from '../../components/EmptyState'
import {Icon} from '../../components/Icon'
import {SplitPane} from '../../components/SplitPane'
import {Banner, Modal, PaneHeader} from '../../components/ui'
import {VirtualList} from '../../components/VirtualList'
import {call, hasDesktopHost, host, HostError, type HostSchemas} from '../../host/client'
import {useHostEvents, useHostQuery} from '../../host/useHost'
import {fileName, folderOf, formatDateTime, formatRelativeTime, plural} from '../../lib/format'

type Lock = HostSchemas['LockDto']
const ROW_HEIGHT = 44

/** Who holds which files on this branch. Release a lock left behind in a workspace that's gone, like `vx unlock`. */
export function LocksPage() {
    const {current} = useWorkspace()
    const {session} = useSession()
    const holder = (l: Lock) => l.mine ? 'You, here' : l.username === session?.username ? `You, on ${l.host}` : l.username
    const [reload, setReload] = useState(0)
    const [filter, setFilter] = useState('')
    const [selected, setSelected] = useState<string>()
    const [confirm, setConfirm] = useState<Lock>()
    const [message, setMessage] = useState<{tone: 'success' | 'error'; text: string}>()

    const locks = useHostQuery(signal => call(host.GET('/api/workspaces/current/locks', {signal})), [current?.root, current?.branchId, reload])

    // Someone locked or released something: show it.
    useHostEvents(event => {
        if (event.type === 'workspace.activity') setReload(n => n + 1)
    })

    const needle = filter.trim().toLocaleLowerCase()
    const visible = useMemo(() => (locks.data ?? []).filter(l => !needle || l.path.toLocaleLowerCase().includes(needle) || l.username.toLocaleLowerCase().includes(needle)), [locks.data, needle])
    const mine = (locks.data ?? []).filter(l => l.mine).length
    const lock = visible.find(l => l.path === selected)

    if (!hasDesktopHost()) return <EmptyState icon="lock" title="Locks are in the desktop app">Open Vertex Desktop to see who holds which files.</EmptyState>

    const unlock = async (target: Lock) => {
        try {
            const result = await call(host.POST('/api/workspaces/current/unlock', {body: {paths: [target.path]}}))
            setMessage(result.released.length > 0
                ? {tone: 'success', text: `Released ${target.path}.`}
                : {tone: 'error', text: `Couldn't release ${target.path}: only people with manage access can release other people's locks. Ask ${target.username} to submit or revert it.`})
            setReload(n => n + 1)
        } catch (error) {
            setMessage({tone: 'error', text: error instanceof HostError ? error.message : String(error)})
        }
    }

    const list = (
        <div className="locks-pane">
            <PaneHeader title="Locks" count={locks.data?.length}>
                <button type="button" className="icon-button" title="Refresh" aria-label="Refresh" onClick={() => setReload(n => n + 1)}>
                    <Icon name="refresh" size={15}/>
                </button>
            </PaneHeader>
            {message && <Banner tone={message.tone} onDismiss={() => setMessage(undefined)}>{message.text}</Banner>}
            {locks.error && <Banner tone="error">{locks.error.message}</Banner>}
            {(locks.data?.length ?? 0) > 0 && (
                <div className="changes-toolbar">
                    <label className="filter">
                        <Icon name="search" size={13}/>
                        <input value={filter} onChange={e => setFilter(e.target.value)} placeholder="Filter by file or person"/>
                    </label>
                </div>
            )}
            <div className="changes-list">
                {locks.loading && !locks.data ? (
                    <EmptyState icon="clock" title="Loading locks…"/>
                ) : visible.length === 0 ? (
                    <EmptyState icon="lock" title={needle ? 'No matching locks' : 'Nobody holds a lock'}>
                        {needle ? 'Try another filter.' : 'Files are locked while someone changes them, and released when they submit or revert.'}
                    </EmptyState>
                ) : (
                    <VirtualList items={visible} rowHeight={ROW_HEIGHT} label="Locks" activeIndex={visible.findIndex(l => l.path === selected)} render={item => (
                        <button type="button" className={`lock-row ${item.path === selected ? 'active' : ''}`} onClick={() => setSelected(item.path)}>
                            <Avatar person={{name: item.username}}/>
                            <span>
                                <b>{fileName(item.path)}</b>
                                <small>{folderOf(item.path) || 'Workspace root'}</small>
                            </span>
                            <em className={item.mine ? 'mine' : ''}>{holder(item)}</em>
                        </button>
                    )}/>
                )}
            </div>
            {mine > 0 && <p className="pane-footnote muted">{plural(mine, 'file')} locked by this workspace. They're released when you submit or revert them.</p>}
        </div>
    )

    const detail = lock ? (
        <article className="detail">
            <header className="detail-header">
                <span className="detail-icon"><Icon name="lock" size={18}/></span>
                <div>
                    <h2>{fileName(lock.path)}</h2>
                    <p className="path">{lock.path}</p>
                </div>
            </header>
            <dl className="detail-facts">
                <dt>Held by</dt>
                <dd><span className="holder"><Avatar person={{name: lock.username}}/>{lock.mine ? 'You, in this workspace' : lock.username === session?.username ? 'You, in another workspace' : lock.username}</span></dd>
                <dt>Machine</dt>
                <dd>{lock.host}</dd>
                <dt>Since</dt>
                <dd title={formatDateTime(lock.createdAt)}>{formatRelativeTime(lock.createdAt)}</dd>
            </dl>
            {lock.mine ? (
                <p className="muted">Your lock is released when you submit or revert this file on the Changes page.</p>
            ) : (
                <div className="detail-actions">
                    <button type="button" className="button danger" onClick={() => setConfirm(lock)}>
                        <Icon name="unlock" size={14}/>
                        Release lock…
                    </button>
                </div>
            )}
        </article>
    ) : (
        <EmptyState icon="lock" title="Select a lock">See who holds it and since when, or release one left behind.</EmptyState>
    )

    return (
        <>
            <SplitPane id="locks" list={list} detail={detail}/>
            {confirm && (
                <Modal title={`Release ${fileName(confirm.path)}?`} onClose={() => setConfirm(undefined)} footer={
                    <>
                        <button type="button" className="button" onClick={() => setConfirm(undefined)} autoFocus>Cancel</button>
                        <button type="button" className="button danger solid" onClick={() => {
                            const target = confirm
                            setConfirm(undefined)
                            void unlock(target)
                        }}>Release lock</button>
                    </>
                }>
                    <p>
                        {confirm.username} locked this file on {confirm.host}. Release it only if that workspace is gone or they agreed:
                        if they still have changes, both of you could edit it at once.
                    </p>
                </Modal>
            )}
        </>
    )
}
