import {useState} from 'react'
import {changedFiles} from '../../mocks/sampleData'
import {ChangeList} from './ChangeList'
import {DiffPane} from './DiffPane'
import {ProjectSidebar} from './ProjectSidebar'

/**
 * Branches and connection (real), changed files and diff (sample data until the app can open a workspace folder,
 * scan it and submit).
 */
export function WorkspacePage() {
    const [selected, setSelected] = useState(0)
    return (
        <div className="workspace-layout">
            <ProjectSidebar/>
            <ChangeList files={changedFiles} selected={selected} onSelect={setSelected}/>
            <DiffPane file={changedFiles[selected]}/>
        </div>
    )
}
