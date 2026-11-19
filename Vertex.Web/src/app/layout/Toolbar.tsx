import {Icon} from '../../components/Icon'
import {useSession} from '../session'
import {Selector} from './Selector'

/** The bar above every page: which project and branch the app is looking at, and workspace actions. */
export function Toolbar() {
    const {session, projects, project, selectProject, branches, branch, selectBranch} = useSession()
    const placeholder = session?.loggedIn === false ? 'Not logged in' : 'Loading…'

    return (
        <header className="toolbar">
            <div className="toolbar-selectors">
                <Selector
                    type="Project"
                    icon="archive"
                    items={projects.map(p => ({id: p.id, label: p.name}))}
                    value={project && {id: project.id, label: project.name}}
                    onSelect={selectProject}
                    placeholder={session?.loggedIn && projects.length === 0 ? 'No projects' : placeholder}
                />
                <Selector
                    type="Branch"
                    icon="branch"
                    items={branches.map(b => ({id: b.id, label: b.name}))}
                    value={branch && {id: branch.id, label: branch.name}}
                    onSelect={selectBranch}
                    placeholder={project ? 'Loading…' : placeholder}
                />
            </div>
            <div className="toolbar-actions">
                {/* Sync needs a workspace on this machine, which the app can't open yet. */}
                <button type="button" disabled title="Coming with workspace support. Until then, run 'vx sync'.">
                    <Icon name="refresh"/>
                    <span>
                        <b>Sync</b>
                        <small>Get the latest revision</small>
                    </span>
                </button>
            </div>
        </header>
    )
}
