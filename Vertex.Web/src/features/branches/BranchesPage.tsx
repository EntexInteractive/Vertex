import {useState} from 'react'
import {useSession} from '../../app/session'
import {useWorkspace} from '../../app/workspace'
import {EmptyState} from '../../components/EmptyState'
import {Icon} from '../../components/Icon'
import {SplitPane} from '../../components/SplitPane'
import {Banner, PaneHeader} from '../../components/ui'
import {plural} from '../../lib/format'

/** The project's branches, which one this workspace is on, and where each merges. Switching is one click. */
export function BranchesPage() {
    const {branches} = useSession()
    const {current, status, checkout, operation} = useWorkspace()
    const [selected, setSelected] = useState<string>()

    const byId = new Map(branches.map(b => [b.id, b]))
    const branch = branches.find(b => b.id === (selected ?? current?.branchId))
    const changes = status?.changes.length ?? 0
    const isCurrent = branch?.id === current?.branchId

    const list = (
        <div className="branches-pane">
            <PaneHeader title="Branches" count={branches.length}/>
            <div className="branch-list">
                {branches.length === 0 ? (
                    <EmptyState icon="branch" title="No branches">Open a workspace to see its project's branches.</EmptyState>
                ) : branches.map(b => (
                    <button key={b.id} type="button" className={`branch-row ${b.id === branch?.id ? 'active' : ''}`} onClick={() => setSelected(b.id)}>
                        <Icon name="branch" size={14}/>
                        <span>
                            <b>{b.name}</b>
                            <small>{b.mergeTo ? `merges into ${byId.get(b.mergeTo)?.name ?? 'another branch'}` : b.parentId ? `from ${byId.get(b.parentId)?.name ?? 'another branch'}` : 'first branch'}</small>
                        </span>
                        {b.id === current?.branchId && <em>Current</em>}
                    </button>
                ))}
            </div>
        </div>
    )

    const detail = branch ? (
        <article className="detail">
            <header className="detail-header">
                <span className="detail-icon"><Icon name="branch" size={18}/></span>
                <div>
                    <h2>{branch.name}</h2>
                    <p className="path">{isCurrent ? 'This workspace is on this branch.' : 'Another branch of this project.'}</p>
                </div>
            </header>
            <dl className="detail-facts">
                <dt>Created from</dt>
                <dd>{branch.parentId ? byId.get(branch.parentId)?.name ?? '—' : 'The project’s first branch'}</dd>
                <dt>Merges into</dt>
                <dd>{branch.mergeTo ? <>{byId.get(branch.mergeTo)?.name ?? '—'} <span className="muted">after every submit</span></> : 'Nothing automatically'}</dd>
            </dl>
            {!isCurrent && (
                <>
                    {changes > 0 && <Banner tone="warning">{plural(changes, 'file')} changed here. Submit or revert them before switching, so nothing is carried between branches.</Banner>}
                    <div className="detail-actions">
                        <button type="button" className="button primary" disabled={changes > 0 || operation !== undefined} onClick={() => void checkout(branch.id)}>
                            <Icon name="branch" size={14}/>
                            Switch to {branch.name}
                        </button>
                    </div>
                    <p className="muted">Switching downloads only the files that differ between the branches.</p>
                </>
            )}
            <p className="muted">Create, delete and merge branches with <code>vx branch</code> and <code>vx merge</code> for now.</p>
        </article>
    ) : (
        <EmptyState icon="branch" title="Select a branch"/>
    )

    return <SplitPane id="branches" list={list} detail={detail} initial={320}/>
}
